using System.IO.Compression;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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
                Console.WriteLine("PASS: real WPF add/preview/pause/resume/cancel/retry/export/history/restore/corrupt-profile scenarios");
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
        window.OutputDirectory = Path.Combine(Root, "books");

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
        Assert(network.PageOneCalls == firstPageCalls, "Resume re-downloaded first page instead of cache");
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
        Assert(restored.OutputDirectory == Path.Combine(Root, "books") && restored.ConfirmAdult, "Settings did not restore");

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
        public TaskCompletionSource<bool> SecondPageStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> SlowPreviewStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class FakeSource(FakeNetwork network) : IStorySource
    {
        private readonly SefanSource _urls = new();
        public bool CanHandle(Uri uri) => _urls.CanHandle(uri);
        public Uri NormalizeToFirstPage(Uri uri) => _urls.NormalizeToFirstPage(uri);
        public async Task<StoryPage> GetPageAsync(Uri uri, bool confirmAdult, CancellationToken cancellationToken)
        {
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
