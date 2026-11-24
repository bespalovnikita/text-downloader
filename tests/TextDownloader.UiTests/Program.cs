using System.IO.Compression;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Xml.Linq;
using TextDownloader;
using TextDownloader.Core;

internal static class Program
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "TextDownloader-UiTests-" + Guid.NewGuid().ToString("N"));
    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            int code = 0;
            try
            {
                Directory.CreateDirectory(Root);
                await RunScenariosAsync();
                await CacheCleanupUiTests.RunAsync(Path.Combine(Root, "cache-cleanup"));
                await ManualRecoveryAsync();
                await SavedPageRecoveryAsync();
                await UpdateUiTests.RunAsync(Path.Combine(Root, "updates"));
                await BestweaponUiTests.RunAsync(Path.Combine(Root, "bestweapon"));
                Console.WriteLine("PASS: real WPF add/preview/pause/resume/cancel/retry/export/history/restore/corrupt-profile scenarios");
                Console.WriteLine("PASS: real WPF update dialog, version checks, progress/cancel, busy/read-only guards and saved-state handoff");
                Console.WriteLine("PASS: real WPF Bestweapon post preview, whole-book export with chapter navigation and restored history");
            }
            catch (Exception error) { Console.Error.WriteLine(error); code = 1; }
            finally
            {
                foreach (Window window in app.Windows.Cast<Window>().ToArray()) window.Close();
                await Task.Delay(100);
                app.Shutdown(code);
                try { Directory.Delete(Root, true); } catch (IOException) { }
            }
        };
        return app.Run();
    }

    private static async Task RunScenariosAsync()
    {
        string profile = Path.Combine(Root, "profile");
        var network = new FakeNetwork();
        var window = new MainWindow(profile, () => new FakeSource(network), enableTray: false);
        window.Show();
        await window.Ready.WaitAsync(TimeSpan.FromSeconds(10));
        Assert(!((Button)window.FindName("CopyQueueUrlButton")).IsEnabled &&
            !((Button)window.FindName("CopyDownloadUrlButton")).IsEnabled, "Copy URL was enabled without a selection");
        window.OutputDirectory = Path.Combine(Root, "books");
        var delayInput = (TextBox)window.FindName("PageDelayInput");
        delayInput.SetCurrentValue(TextBox.TextProperty, "0,09");
        Assert(!window.CanStart && window.PageDelayError.Length > 0, "Delay below the minimum was accepted");
        delayInput.SetCurrentValue(TextBox.TextProperty, "0,1");
        Assert(window.CanStart && window.PageDelayError.Length == 0, "Minimum delay was rejected");

        // Add through the actual routed button, normalising a later page and a duplicate.
        ((TextBox)window.FindName("UrlInput")).Text = "https://sefan.ru/stories/ru/2/drama/fixture_1-2.html\nhttps://sefan.ru/stories/ru/2/drama/fixture_1.html";
        Click(window, "AddButton");
        await UntilAsync(() => window.Items.Count == 1, "Add button did not populate queue");
        await window.ActivePreview.WaitAsync(TimeSpan.FromSeconds(10));
        Assert(window.Items[0].Url.EndsWith("_1.html"), "First-page normalisation");
        Assert(window.PreviewTitle == "Тестовый рассказ", "Actual preview binding");
        Assert(((TextBlock)window.FindName("PreviewTitleText")).Text == "Тестовый рассказ", "WPF preview TextBlock");

        // Stop in a real in-flight request. No partial book or success is allowed.
        network.HoldSecondPage = true;
        window.RefreshCache = true;
        Click(window, "StartButton");
        await ReachedSecondPageAsync(window, network);
        Click(window, "PauseButton");
        await window.ActiveSession.WaitAsync(TimeSpan.FromSeconds(10));
        Assert(window.Items[0].Status == QueueStatus.Paused && !window.IsBusy, "Pause state");
        Assert(window.History.Count == 0, "Pause entered success history");
        Assert(!Directory.Exists(window.OutputDirectory) || !Directory.EnumerateFiles(window.OutputDirectory).Any(), "Pause exported partial file");
        int firstPageCalls = network.PageOneCalls;

        network.HoldSecondPage = false;
        Click(window, "StartButton");
        await window.ActiveSession.WaitAsync(TimeSpan.FromSeconds(10));
        Assert(window.Items[0].Status == QueueStatus.Completed, "Resume did not finish: " + window.Items[0].Message);
        Assert(network.PageOneCalls == firstPageCalls, "Resume re-downloaded the first page of a fresh download");
        window.RefreshCache = false;
        Assert(window.Items[0].Files.Count == 2 && window.Items[0].Files.All(File.Exists), "Both formats not published");
        Assert(window.History.Count == 1 && window.History[0].Pages == 2, "Success history");
        string fb2 = window.Items[0].Files.Single(file => file.EndsWith(".fb2"));
        XDocument book = XDocument.Load(fb2);
        XNamespace ns = "http://www.gribuser.ru/xml/fictionbook/2.0";
        Assert(book.Descendants(ns + "section").Descendants(ns + "p").Any(p => p.Value.Contains("первом и втором")), "Pagination seam corrupted");
        using (var archive = ZipFile.OpenRead(window.Items[0].Files.Single(file => file.EndsWith(".epub"))))
            Assert(archive.GetEntry("mimetype") is not null, "EPUB archive missing mimetype");

        // Age gate fails without consent, and retry starts only from explicit Start.
        network.RequireAdult = true;
        await window.AddUrlsAsync("https://sefan.ru/stories/ru/2/drama/chitat_gated-fixture_1.html");
        Click(window, "StartButton");
        await window.ActiveSession.WaitAsync(TimeSpan.FromSeconds(10));
        Assert(window.Items[1].Status == QueueStatus.Failed, "Age confirmation silently accepted");
        Assert(window.History.Count == 1, "Failed gate entered history");
        VerifyCopyUrls(window);
        VerifyJournal(window);
        ((CheckBox)window.FindName("AdultCheck")).IsChecked = true;
        Click(window, "RetryButton");
        Assert(!window.IsBusy && window.Items[1].Status == QueueStatus.Queued, "Retry automatically started");
        Click(window, "StartButton");
        await window.ActiveSession.WaitAsync(TimeSpan.FromSeconds(10));
        Assert(window.Items[1].Status == QueueStatus.Completed && window.History.Count == 2, "Consent and retry did not succeed");

        // Cancel also leaves no published/recorded partial result.
        network.RequireAdult = false;
        network.HoldSecondPage = true;
        network.SecondPageStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await window.AddUrlsAsync("https://sefan.ru/stories/ru/2/drama/chitat_cancel-fixture_1.html");
        Click(window, "StartButton");
        await ReachedSecondPageAsync(window, network);
        Click(window, "CancelButton");
        await window.ActiveSession.WaitAsync(TimeSpan.FromSeconds(10));
        Assert(window.Items[2].Status == QueueStatus.Cancelled && window.History.Count == 2, "Cancellation state/history");
        await window.AddUrlsAsync("https://sefan.ru/stories/ru/2/drama/chitat_waiting-fixture_1.html");
        await CloseAsync(window);

        var restored = new MainWindow(profile, () => new FakeSource(network), enableTray: false);
        restored.Show();
        await restored.Ready.WaitAsync(TimeSpan.FromSeconds(10));
        Assert(restored.Items.Count == 4 && restored.History.Count == 2, "Queue/history did not restore");
        Assert(!restored.IsBusy && restored.Items[3].Status == QueueStatus.Queued, "Restore started a download");
        Assert(decimal.Parse(restored.PageDelayText, System.Globalization.CultureInfo.CurrentCulture) == 0.1m, "Delay did not restore");
        Assert(restored.OutputDirectory == Path.Combine(Root, "books") && restored.ConfirmAdult, "Settings did not restore");
        Assert(restored.JournalLevel == DownloadLogLevel.Detail, "Journal level did not restore");

        // A cached export can beat a slow preview request. Completion must cancel
        // that request and keep the ready-book state in the real preview controls.
        restored.SelectedItem = restored.Items[0];
        await restored.ActivePreview.WaitAsync(TimeSpan.FromSeconds(10));
        network.HoldSecondPage = false;
        network.HoldNextFirstPage = true;
        Click(restored, "PreviewButton");
        await network.SlowPreviewStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Click(restored, "RepeatSelectedButton");
        Click(restored, "StartButton");
        await restored.ActiveSession.WaitAsync(TimeSpan.FromSeconds(10));
        await restored.ActivePreview.WaitAsync(TimeSpan.FromSeconds(10));
        Assert(restored.PreviewStatus.StartsWith("Книга готова", StringComparison.Ordinal), "Slow preview replaced completed-book state");
        await CloseAsync(restored);

        string badProfile = Path.Combine(Root, "corrupt");
        Directory.CreateDirectory(badProfile);
        string badFile = Path.Combine(badProfile, "session.json");
        const string damaged = "{ damaged profile";
        await File.WriteAllTextAsync(badFile, damaged);
        var corrupt = new MainWindow(badProfile, () => new FakeSource(network), enableTray: false);
        corrupt.Show();
        await corrupt.Ready.WaitAsync(TimeSpan.FromSeconds(10));
        Assert(!corrupt.CanStart, "Corrupt state allowed download/start");
        await CloseAsync(corrupt);
        Assert(await File.ReadAllTextAsync(badFile) == damaged, "Corrupt profile overwritten");
    }

    private static void VerifyJournal(MainWindow window)
    {
        var level = (ComboBox)window.FindName("JournalLevelCombo");
        var selectedOnly = (CheckBox)window.FindName("JournalSelectedCheck");
        var journal = (ListBox)window.FindName("JournalBox");
        Assert(window.JournalText.Contains("Очередь запущена") && !window.JournalText.Contains("[ПОДРОБНО]"),
            "Normal journal filter did not show the general log");
        level.SetCurrentValue(Selector.SelectedValueProperty, DownloadLogLevel.Detail);
        Assert(window.JournalText.Contains("из кеша") && journal.Items.Count > 0, "Detailed entries were not recorded or shown");

        selectedOnly.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
        Assert(window.SelectedItem == window.Items[1] && window.JournalText.Contains(window.Items[1].Url) &&
            !window.JournalText.Contains(window.Items[0].Url) && !window.JournalText.Contains("Очередь запущена"),
            "The selected journal included unrelated or global events");
        window.SelectedItem = window.Items[0];
        Assert(window.JournalText.Contains("Готово.") && !window.JournalText.Contains(window.Items[1].Url),
            "The journal did not follow a change in selection");
        window.SelectedItem = window.Items[1];
        level.SetCurrentValue(Selector.SelectedValueProperty, DownloadLogLevel.Error);
        Assert(window.JournalText.Contains("[ОШИБКА]") && !window.JournalText.Contains("[ПОДРОБНО]") &&
            !window.JournalText.Contains("[ИНФО]"), "Error-only log filter is incorrect");
        selectedOnly.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
        level.SetCurrentValue(Selector.SelectedValueProperty, DownloadLogLevel.Detail);
    }

    private static void VerifyCopyUrls(MainWindow window)
    {
        var previousClipboard = Clipboard.GetDataObject();
        try
        {
            var queue = (ListBox)window.FindName("QueueList");
            queue.SetCurrentValue(Selector.SelectedItemProperty, window.Items[0]);
            Click(window, "CopyQueueUrlButton");
            Assert(Clipboard.GetText() == window.Items[0].Url, "Queue copy did not copy the selected completed item's URL");

            var downloads = (DataGrid)window.FindName("DownloadsGrid");
            downloads.SetCurrentValue(Selector.SelectedItemProperty, window.Items[1]);
            Click(window, "CopyDownloadUrlButton");
            Assert(Clipboard.GetText() == window.Items[1].Url, "Downloads copy did not copy the selected failed item's URL");
        }
        finally
        {
            if (previousClipboard is null) Clipboard.Clear();
            else Clipboard.SetDataObject(previousClipboard, true);
        }
    }

    private static async Task ManualRecoveryAsync()
    {
        const string first = "https://sefan.ru/stories/ru/2/drama/recovery_1.html";
        const string second = "https://sefan.ru/stories/ru/2/drama/recovery_1-2.html";
        const string replacement = "https://sefan.ru/stories/ru/2/i/recovery_1-2.html";
        string profile = Path.Combine(Root, "recovery-profile");
        var network = new FakeNetwork { FailedPage = second };
        var window = new MainWindow(profile, () => new FakeSource(network), enableTray: false);
        window.Show();
        await window.Ready.WaitAsync(TimeSpan.FromSeconds(10));
        window.OutputDirectory = Path.Combine(Root, "recovery-books");
        await window.AddUrlsAsync(first);
        await window.ActivePreview.WaitAsync(TimeSpan.FromSeconds(10));
        await window.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert(window.Items[0].Status == QueueStatus.Failed && window.History.Count == 0, "Download error was lost");
        int firstPageCalls = network.PageOneCalls;
        await CloseAsync(window);

        var restored = new MainWindow(profile, () => new FakeSource(network), enableTray: false);
        restored.Show();
        await restored.Ready.WaitAsync(TimeSpan.FromSeconds(10));
        var lastPage = (TextBox)restored.FindName("LastSuccessfulPageInput");
        var recoveryInput = (TextBox)restored.FindName("RecoveryPageInput");
        Assert(lastPage.IsVisible && lastPage.IsReadOnly && lastPage.Text == first, "Last successful page was unavailable for copying");
        Assert(recoveryInput.Text == second, "The failed page was not offered for replacement");
        recoveryInput.Text = first;
        Click(restored, "UseRecoveryPageButton");
        Assert(restored.Items[0].Status == QueueStatus.Failed, "Replacing the pending page with an earlier page was allowed");
        recoveryInput.Text = replacement;
        Click(restored, "UseRecoveryPageButton");
        Assert(restored.Items[0].Status == QueueStatus.Queued, "Replacement did not queue a retry");
        await restored.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert(restored.Items[0].Status == QueueStatus.Completed && restored.History.Count == 1, "Replacement did not complete the download");
        Assert(network.PageOneCalls == firstPageCalls, "Recovery re-downloaded the saved page");
        await CloseAsync(restored);
    }

    private static async Task SavedPageRecoveryAsync()
    {
        const string first = "https://sefan.ru/stories/ru/2/drama/saved-recovery_1.html";
        const string second = "https://sefan.ru/stories/ru/2/drama/saved-recovery_1-2.html";
        string profile = Path.Combine(Root, "saved-recovery-profile");
        var network = new FakeNetwork { FailedPage = second };
        var window = new MainWindow(profile, () => new FakeSource(network), enableTray: false);
        window.Show();
        await window.Ready.WaitAsync(TimeSpan.FromSeconds(10));
        window.OutputDirectory = Path.Combine(Root, "saved-recovery-books");
        await window.AddUrlsAsync(first);
        await window.ActivePreview.WaitAsync(TimeSpan.FromSeconds(10));
        await window.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var importButton = (Button)window.FindName("ImportRecoveryPageButton");
        Assert(importButton.IsVisible && importButton.IsEnabled, "HTML import is unavailable after a failed page");

        string htmlPath = Path.Combine(Root, "saved-page.txt");
        string Page(string address) => $"""
            <html><head><link rel="canonical" href="{address}"></head>
            <body><h1>Тестовый рассказ</h1><div id="rsz"> и странице из браузера.</div>
            <span class="but">Далее</span></body></html>
            """;
        await File.WriteAllTextAsync(htmlPath, Page(first));
        await window.ImportRecoveryPageAsync(htmlPath).WaitAsync(TimeSpan.FromSeconds(10));
        Assert(window.Items[0].Status == QueueStatus.Failed && ((TextBlock)window.FindName("RecoveryError")).Text.Length > 0,
            "A wrong saved page replaced the checkpoint");

        await File.WriteAllTextAsync(htmlPath, Page(second));
        await window.ImportRecoveryPageAsync(htmlPath).WaitAsync(TimeSpan.FromSeconds(10));
        Assert(window.Items[0].Status == QueueStatus.Queued && window.Items[0].Checkpoint?.LastSuccessfulPageUrl == new Uri(second),
            "An imported page did not advance the checkpoint");
        int firstPageCalls = network.PageOneCalls;
        await CloseAsync(window);

        var restored = new MainWindow(profile, () => new FakeSource(network), enableTray: false);
        restored.Show();
        await restored.Ready.WaitAsync(TimeSpan.FromSeconds(10));
        await restored.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert(restored.Items[0].Status == QueueStatus.Completed && restored.History.Count == 1 && restored.History[0].Pages == 2,
            "The saved page did not survive an application restart and export");
        Assert(network.PageOneCalls == firstPageCalls, "Export requested the cached first page again");
        await CloseAsync(restored);
    }

    private static void Click(MainWindow window, string name)
    {
        var button = (Button)window.FindName(name);
        Assert(button.IsEnabled, "Disabled control: " + name);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static async Task ReachedSecondPageAsync(MainWindow window, FakeNetwork network)
    {
        try
        {
            await Task.WhenAny(network.SecondPageStarted.Task, window.ActiveSession).WaitAsync(TimeSpan.FromSeconds(10));
            if (!network.SecondPageStarted.Task.IsCompleted && window.ActiveSession.IsCompleted)
                throw new InvalidOperationException("The session stopped before requesting page two.");
            await network.SecondPageStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception error)
        {
            throw new Exception("Download did not reach page two. " + window.CurrentStatus + "\n" +
                string.Join("\n", window.Items.Select(item => item.Status + ": " + item.Message)) +
                "\nJournal:\n" + window.JournalText, error);
        }
    }
    private static async Task CloseAsync(Window window)
    {
        window.Close();
        await UntilAsync(() => !window.IsVisible, "Window did not close");
    }
    private static async Task UntilAsync(Func<bool> predicate, string failure)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline) throw new Exception(failure);
            await Task.Delay(20);
        }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }

    private sealed class FakeNetwork
    {
        public bool HoldSecondPage, RequireAdult, HoldNextFirstPage;
        public int PageOneCalls;
        public string? FailedPage;
        public TaskCompletionSource<bool> SecondPageStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> SlowPreviewStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class FakeSource(FakeNetwork network) : IStorySource
    {
        private readonly SefanSource _urls = new();
        public bool CanHandle(Uri uri) => _urls.CanHandle(uri);
        public Uri NormalizeToFirstPage(Uri uri) => _urls.NormalizeToFirstPage(uri);
        public int? GetPageNumber(Uri uri) => _urls.GetPageNumber(uri);
        public Uri ValidatePageRoute(Uri first, Uri candidate, int? number) => _urls.ValidatePageRoute(first, candidate, number);
        public Uri ValidateReplacementPage(Uri expected, Uri candidate) => _urls.ValidateReplacementPage(expected, candidate);
        public bool CanImportSavedPage(Uri uri) => _urls.CanImportSavedPage(uri);
        public Task<StoryPage> ReadSavedPageAsync(Uri expected, Stream html, CancellationToken token) =>
            _urls.ReadSavedPageAsync(expected, html, token);
        public async Task<StoryPage> GetPageAsync(Uri uri, bool confirmAdult, CancellationToken cancellationToken)
        {
            if (uri.AbsoluteUri == network.FailedPage) throw new IOException("Synthetic connection failure");
            if (network.RequireAdult && !confirmAdult) throw new AgeConfirmationRequiredException();
            bool second = uri.AbsolutePath.EndsWith("_1-2.html");
            if (!second && network.HoldNextFirstPage)
            {
                network.HoldNextFirstPage = false;
                network.SlowPreviewStarted.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            if (second)
            {
                network.SecondPageStarted.TrySetResult(true);
                if (network.HoldSecondPage) await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            else Interlocked.Increment(ref network.PageOneCalls);
            await Task.Delay(30, cancellationToken);
            return new StoryPage(uri, second ? 2 : 1, "Тестовый рассказ", "Автор теста",
                second ? " и втором фрагменте.\nКонец рассказа." : "Текст на первом",
                second ? null : new Uri(uri.AbsoluteUri.Replace("_1.html", "_1-2.html")), 2);
        }
        public void Dispose() => _urls.Dispose();
    }
}
