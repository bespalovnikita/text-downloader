using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using TextDownloader;
using TextDownloader.Core;
using TextDownloader.Core.Updates;
using TextDownloader.Updates;

internal static class UpdateUiTests
{
    public static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        await HttpClientValidationAsync(Path.Combine(root, "transport"));
        await DialogAndHandoffAsync(Path.Combine(root, "dialog"));
        await ReadOnlyProfileCannotInstallAsync(Path.Combine(root, "readonly"));
    }

    private static async Task DialogAndHandoffAsync(string root)
    {
        using var fixture = new UpdateFixture(root);
        var network = new SourceGate();
        var fake = new FakeUpdateClient(fixture.Zip);
        string profile = Path.Combine(root, "profile");
        var window = new MainWindow(profile, () => new GatedSource(network), enableTray: false,
            updateClient: fake, updateTargetDirectory: fixture.Target);
        window.Show();
        await window.Ready.WaitAsync(TimeSpan.FromSeconds(10));
        window.OutputDirectory = Path.Combine(root, "books");
        window.ConfirmAdult = true;
        window.MinimizeToTray = false;

        var dialog = await OpenUpdateAsync(window);
        await UntilAsync(() => fake.Checks > 0 && !dialog.IsBusy, "Current-version check did not finish.");
        Assert(((TextBlock)dialog.FindName("CurrentVersionText")).Text == "0.1.0", "Current version was not bound to the real control.");
        Assert(dialog.Release is null && !Button(dialog, "InstallButton").IsEnabled, "Current version incorrectly offers installation.");
        dialog.Close();
        await UntilAsync(() => !dialog.IsVisible && window.CurrentUpdateWindow is null, "Current-version dialog did not close.");

        // Keep a real download active while the update dialog checks a new release.
        await window.AddUrlsAsync("https://sefan.ru/stories/ru/2/drama/update-fixture_1.html");
        await window.ActivePreview.WaitAsync(TimeSpan.FromSeconds(10));
        network.Hold = true;
        Click(window, "StartButton");
        await network.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert(window.IsBusy, "Synthetic download was not active.");
        fake.Release = Release();
        dialog = await OpenUpdateAsync(window);
        await UntilAsync(() => dialog.Release is not null && !dialog.IsBusy, "New-release check did not finish.");
        Assert(((TextBlock)dialog.FindName("AvailableVersionText")).Text == "0.2.0", "Available version was not displayed.");
        Assert(!Button(dialog, "InstallButton").IsEnabled, "Installation was enabled during an active download.");

        network.Hold = false;
        network.Release.TrySetResult(true);
        await window.ActiveSession.WaitAsync(TimeSpan.FromSeconds(10));
        await UntilAsync(() => Button(dialog, "InstallButton").IsEnabled, "Install eligibility did not refresh after the download finished.");
        Assert(window.History.Count == 1, "The completed job was lost before updating.");

        fake.HoldPreparation = true;
        Click(dialog, "InstallButton");
        await fake.PreparationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await UntilAsync(() => dialog.Status.Contains("321", StringComparison.Ordinal), "Download progress was not rendered.");
        Assert(dialog.IsBusy && !Button(dialog, "CheckButton").IsEnabled && !Button(dialog, "InstallButton").IsEnabled,
            "Preparation did not disable competing update operations.");
        Click(dialog, "CancelOperationButton");
        await dialog.OperationTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert(fake.CancellationObserved && !dialog.IsBusy && window.IsVisible, "Cancelled preparation closed the application or ignored cancellation.");
        Assert(!File.Exists(Path.Combine(fixture.Target, ".text-downloader-update.json")), "Cancellation began installation.");

        // Leave both a finished and a waiting job in the real checkpoint before handoff.
        dialog.Close();
        await UntilAsync(() => window.CurrentUpdateWindow is null, "Cancelled update dialog did not release its owner.");
        await window.AddUrlsAsync("https://sefan.ru/stories/ru/2/drama/waiting-update-fixture_1.html");
        await window.ActivePreview.WaitAsync(TimeSpan.FromSeconds(10));
        fake.HoldPreparation = false;
        dialog = await OpenUpdateAsync(window);
        await UntilAsync(() => dialog.Release is not null && !dialog.IsBusy, "Final release check did not finish.");
        Click(dialog, "InstallButton");
        await UntilAsync(() => !window.IsVisible, "Successful preparation did not reach application handoff.");
        await dialog.OperationTask.WaitAsync(TimeSpan.FromSeconds(10));
        var saved = await new AppStore(profile).LoadAsync();
        Assert(saved.Settings.OutputDirectory == Path.Combine(root, "books") && saved.Settings.ConfirmAdult,
            "Handoff did not preserve settings.");
        Assert(saved.Queue.Count == 2 && saved.Queue[0].Status == QueueStatus.Completed && saved.Queue[1].Status == QueueStatus.Queued,
            "Handoff did not preserve finished/waiting queue states.");
        Assert(saved.History.Count == 1 && saved.History[0].Files.All(File.Exists), "Handoff lost successful book paths/history.");
        Assert(!File.Exists(Path.Combine(fixture.Target, ".text-downloader-update.json")),
            "The plain WPF test host unexpectedly installed an update instead of handing off.");
        fake.Prepared?.Dispose();
    }

    private static async Task ReadOnlyProfileCannotInstallAsync(string root)
    {
        using var fixture = new UpdateFixture(root);
        string profile = Path.Combine(root, "profile");
        Directory.CreateDirectory(profile);
        string state = Path.Combine(profile, "session.json");
        const string original = "{ intentionally corrupt state";
        await File.WriteAllTextAsync(state, original);
        var fake = new FakeUpdateClient(fixture.Zip) { Release = Release() };
        var window = new MainWindow(profile, enableTray: false, updateClient: fake, updateTargetDirectory: fixture.Target);
        window.Show();
        await window.Ready.WaitAsync(TimeSpan.FromSeconds(10));
        var dialog = await OpenUpdateAsync(window);
        await UntilAsync(() => dialog.Release is not null && !dialog.IsBusy, "Read-only update check did not finish.");
        Assert(!Button(dialog, "InstallButton").IsEnabled && fake.Preparations == 0, "Read-only checkpoint allowed update handoff.");
        dialog.Close();
        await UntilAsync(() => window.CurrentUpdateWindow is null, "Read-only dialog did not release its owner.");
        window.Close();
        await UntilAsync(() => !window.IsVisible, "Read-only window did not close.");
        Assert(await File.ReadAllTextAsync(state) == original, "Update check rewrote a corrupt profile.");
    }

    private static async Task HttpClientValidationAsync(string root)
    {
        using var fixture = new UpdateFixture(root);
        using var handler = new ReleaseHandler(File.ReadAllBytes(fixture.Zip));
        using var http = new HttpClient(handler);
        using var client = new UpdateClient(http, new Version(0, 1, 0));
        handler.Version = "0.1.0";
        Assert(await client.CheckAsync(CancellationToken.None) is null, "HTTP check offered the installed version.");
        handler.Version = "0.2.0";
        var release = await client.CheckAsync(CancellationToken.None) ?? throw new Exception("HTTP check missed a newer release.");
        Assert(release.Version == new Version(0, 2, 0), "HTTP check parsed a wrong version.");
        handler.ForeignAsset = true;
        await ThrowsAsync<InvalidDataException>(() => client.CheckAsync(CancellationToken.None));
        handler.ForeignAsset = false;
        handler.Draft = true;
        await ThrowsAsync<InvalidDataException>(() => client.CheckAsync(CancellationToken.None));
        handler.Draft = false;

        var progress = new List<UpdateProgress>();
        await ThrowsAsync<InvalidDataException>(() => client.PrepareAsync(release, fixture.Target,
            Path.Combine(root, "scratch"), new InlineProgress(progress.Add), CancellationToken.None));
        Assert(progress.Any(value => value.CompletedBytes > 0), "HTTP transfer did not report progress.");
        Assert(File.ReadAllText(Path.Combine(fixture.Target, "TextDownloader.exe")) == "old exe", "Bad SHA-256 changed the installed executable.");
        Assert(!File.Exists(Path.Combine(fixture.Target, ".text-downloader-update.json")), "Bad SHA-256 created an installation pointer.");

        handler.HoldArchive = true;
        using var stop = new CancellationTokenSource();
        Task preparing = client.PrepareAsync(release, fixture.Target, Path.Combine(root, "cancel-scratch"), null, stop.Token);
        await handler.ArchiveRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
        stop.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => preparing);
        Assert(File.ReadAllText(Path.Combine(fixture.Target, "TextDownloader.exe")) == "old exe", "Cancelled HTTP preparation changed the installation.");
    }

    private static AppRelease Release() => new(new Version(0, 2, 0), "v0.2.0",
        new Uri($"https://github.com/{UpdateClient.Repository}/releases/download/v0.2.0/{UpdateClient.ArchiveName}"),
        new Uri($"https://github.com/{UpdateClient.Repository}/releases/download/v0.2.0/SHA256SUMS.txt"));
    private static async Task<UpdateWindow> OpenUpdateAsync(MainWindow owner)
    {
        Click(owner, "UpdateButton");
        await UntilAsync(() => owner.CurrentUpdateWindow?.IsLoaded == true, "Update button did not open the real dialog.");
        return owner.CurrentUpdateWindow!;
    }
    private static Button Button(Window owner, string name) => (Button)owner.FindName(name);
    private static void Click(Window owner, string name)
    {
        var button = Button(owner, name);
        Assert(button.IsEnabled, "Disabled control: " + name);
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
    }
    private static async Task UntilAsync(Func<bool> predicate, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline) throw new Exception(message);
            await Task.Delay(20);
        }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private sealed class InlineProgress(Action<UpdateProgress> report) : IProgress<UpdateProgress>
    { public void Report(UpdateProgress value) => report(value); }
    private sealed class FakeUpdateClient(string zip) : IAppUpdateClient
    {
        public Version CurrentVersion => new(0, 1, 0);
        public AppRelease? Release { get; set; }
        public int Checks, Preparations;
        public bool HoldPreparation, CancellationObserved;
        public TaskCompletionSource<bool> PreparationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public PreparedAppUpdate? Prepared;
        public Task<AppRelease?> CheckAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); Checks++; return Task.FromResult(Release); }
        public async Task<PreparedAppUpdate> PrepareAsync(AppRelease release, string current, string scratch,
            IProgress<UpdateProgress>? progress, CancellationToken token)
        {
            Preparations++;
            progress?.Report(new UpdateProgress("Скачано 321 байт тестового обновления", 321, 1000));
            PreparationStarted.TrySetResult(true);
            if (HoldPreparation)
            {
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { CancellationObserved = true; throw; }
            }
            return Prepared = await Task.Run(() => AppUpdateInstaller.Prepare(zip, current, scratch, "0.2.0", token), token);
        }
    }
    private sealed class SourceGate
    {
        public bool Hold;
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class GatedSource(SourceGate gate) : IStorySource
    {
        private readonly SefanSource urls = new();
        public bool CanHandle(Uri uri) => urls.CanHandle(uri);
        public Uri NormalizeToFirstPage(Uri uri) => urls.NormalizeToFirstPage(uri);
        public async Task<StoryPage> GetPageAsync(Uri uri, bool confirmAdult, CancellationToken token)
        {
            if (gate.Hold) { gate.Started.TrySetResult(true); await gate.Release.Task.WaitAsync(token); }
            return new StoryPage(uri, 1, "Синтетический рассказ для обновления", "Тестовый автор", "Искусственный абзац.", null, 1);
        }
        public void Dispose() => urls.Dispose();
    }
    private sealed class ReleaseHandler(byte[] archive) : HttpMessageHandler
    {
        public string Version = "0.2.0";
        public bool ForeignAsset, Draft, HoldArchive;
        public TaskCompletionSource<bool> ArchiveRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            byte[] content;
            if (request.RequestUri!.Host == "api.github.com")
            {
                string prefix = ForeignAsset ? "https://example.org/" : $"https://github.com/{UpdateClient.Repository}/releases/download/v{Version}/";
                content = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    draft = Draft, prerelease = false, tag_name = "v" + Version,
                    assets = new[] { new { name = UpdateClient.ArchiveName, browser_download_url = prefix + UpdateClient.ArchiveName, size = archive.Length },
                        new { name = "SHA256SUMS.txt", browser_download_url = prefix + "SHA256SUMS.txt", size = 100 } },
                });
            }
            else if (request.RequestUri.AbsolutePath.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal))
                content = Encoding.UTF8.GetBytes(new string('0', 64) + "  " + UpdateClient.ArchiveName + "\n");
            else
            {
                if (HoldArchive) { ArchiveRequested.TrySetResult(true); await Task.Delay(Timeout.Infinite, token); }
                content = archive;
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(content) };
        }
    }
    private sealed class UpdateFixture : IDisposable
    {
        public string Root { get; }
        public string Target => Path.Combine(Root, "installed");
        public string Zip => Path.Combine(Root, "new.zip");
        public UpdateFixture(string root)
        {
            Root = root;
            Directory.CreateDirectory(Target);
            var oldFiles = new Dictionary<string, byte[]> { ["TextDownloader.exe"] = Encoding.UTF8.GetBytes("old exe"),
                ["TextDownloader.Core.dll"] = Encoding.UTF8.GetBytes("old core"), ["assets/app.ico"] = [1, 2, 3] };
            foreach (var file in oldFiles)
            {
                string path = Path.Combine(Target, file.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, file.Value);
            }
            File.WriteAllBytes(Path.Combine(Target, "windows-package.json"), Manifest("0.1.0", oldFiles));
            var newFiles = oldFiles.ToDictionary(file => file.Key, file => Encoding.UTF8.GetBytes("new " + file.Key));
            using var zip = ZipFile.Open(Zip, ZipArchiveMode.Create);
            foreach (var file in newFiles) { using var stream = zip.CreateEntry(file.Key).Open(); stream.Write(file.Value); }
            using var manifest = zip.CreateEntry("windows-package.json").Open();
            manifest.Write(Manifest("0.2.0", newFiles));
        }
        private static byte[] Manifest(string version, Dictionary<string, byte[]> files) => JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = 1, application = "TextDownloader", version,
            files = files.Select(file => new { path = file.Key, size = file.Value.Length,
                sha256 = Convert.ToHexString(SHA256.HashData(file.Value)).ToLowerInvariant() }).ToArray(),
        });
        public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
    }
}
