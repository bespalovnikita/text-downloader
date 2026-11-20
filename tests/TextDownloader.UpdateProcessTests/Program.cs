using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using TextDownloader;
using TextDownloader.Core;
using TextDownloader.Core.Updates;
using TextDownloader.Updates;

internal static class Program
{
    private const string Marker = "docs/update-smoke.md";
    private const int CrashExitCode = 77;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Published update process tests require Windows.");
            if (args.Length == 2 && args[0] == "--crash-apply")
            {
                // This command belongs only to the test harness, never the product.
                var prepared = AppUpdateInstaller.OpenPrepared(Path.GetFullPath(args[1]));
                string marker = AppUpdatePackage.FilePath(prepared.TargetDirectory, Marker);
                Require(!File.Exists(marker), "The crash marker must be new to this installation.");
                AppUpdateInstaller.Apply(prepared, _ =>
                {
                    if (!File.Exists(marker)) return;
                    Console.WriteLine("CRASH CHECKPOINT: durable new sidecar installed before executable replacement");
                    Console.Out.Flush();
                    // Unlike an exception, this bypasses Apply's automatic rollback.
                    Environment.Exit(CrashExitCode);
                });
                throw new InvalidOperationException("The updater did not reach the crash checkpoint.");
            }
            if (args.Length != 2) throw new ArgumentException("Pass current and next published package directories.");
            RunAsync(Path.GetFullPath(args[0]), Path.GetFullPath(args[1])).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static async Task RunAsync(string currentDirectory, string nextDirectory)
    {
        var current = AppUpdatePackage.ReadManifest(Path.Combine(currentDirectory, AppUpdatePackage.ManifestName));
        var next = AppUpdatePackage.ReadManifest(Path.Combine(nextDirectory, AppUpdatePackage.ManifestName));
        AppUpdatePackage.ValidateDirectory(currentDirectory, current);
        AppUpdatePackage.ValidateDirectory(nextDirectory, next);
        Require(AppUpdatePackage.ParseVersion(next.Version) > AppUpdatePackage.ParseVersion(current.Version), "The fixture must use a genuinely newer version.");
        Require(!current.Files.Any(file => file.Path == Marker) && next.Files.Any(file => file.Path == Marker), "The next fixture must introduce its own sidecar.");
        RequireVersion(Path.Combine(currentDirectory, "TextDownloader.exe"), current.Version);
        RequireVersion(Path.Combine(nextDirectory, "TextDownloader.exe"), next.Version);
        string root = Path.Combine(Path.GetTempPath(), "TextDownloader-update-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string archive = Path.Combine(root, "next-package.zip");
            ZipFile.CreateFromDirectory(nextDirectory, archive, CompressionLevel.Fastest, false);
            await NormalHandoffAsync(Path.Combine(root, "normal"), currentDirectory, archive, current, next);
            await InterruptedStartupRecoveryAsync(Path.Combine(root, "recovery"), currentDirectory, archive, current, next);
        }
        finally { await DeleteOwnedDirectoryAsync(root); }
    }

    private static async Task NormalHandoffAsync(string root, string currentDirectory, string archive,
        AppPackageManifest current, AppPackageManifest next)
    {
        await using var fixture = await Fixture.CreateAsync(root, currentDirectory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var owner = fixture.StartInstalled();
        await UntilAsync(() =>
        {
            Require(!owner.HasExited, "The original published application exited before opening its WPF window.");
            return WpfWindow(owner.Id) != IntPtr.Zero;
        }, deadline.Token, "The original published application did not open its WPF window.");
        fixture.Prepared = AppUpdateInstaller.Prepare(archive, fixture.Installed, AppUpdateBootstrap.ScratchRoot, next.Version, deadline.Token);
        string mutexName = App.InstanceMutexName(fixture.Profile);
        // Object existence is not ownership: a secondary process can retain a
        // handle while the updater and restarted app acquire the now-free mutex.
        using var secondaryHandle = new Mutex(false, mutexName);
        var helper = fixture.Track(AppUpdateBootstrap.StartHelper(fixture.Prepared, mutexName,
            owner, fixture.Executable, fixture.Profile));
        await Task.Delay(1000, deadline.Token);
        Require(!owner.HasExited && !helper.HasExited, "The single-file helper must run alone and wait for its real GUI owner.");
        Require(!Directory.EnumerateFiles(fixture.Prepared.Directory, "*.dll", SearchOption.TopDirectoryOnly).Any(), "The temporary helper unexpectedly has adjacent runtime DLLs.");
        fixture.VerifyInstalled(current);
        await CloseGuiAsync(owner, deadline.Token);
        await helper.WaitForExitAsync(deadline.Token);
        Require(helper.ExitCode == 0, "The actual single-file update helper failed.");
        var restarted = await fixture.WaitForRestartedGuiAsync(new[] { owner.Id }, deadline.Token);
        Require(!restarted.HasExited, "The updated WPF application exited unexpectedly.");
        fixture.VerifyInstalled(next);
        Require(AppUpdateInstaller.FindPending(fixture.Installed) is null, "A successful update left a recovery pointer.");
        await fixture.VerifyProfileAndCanariesAsync();
        await CloseGuiAsync(restarted, deadline.Token);
        await fixture.VerifyProfileAndCanariesAsync();
        Console.WriteLine($"PASS published process update: actual {current.Version} GUI exits, copied standalone helper installs {next.Version}, real new-version WPF GUI restarts, profile and user files preserved");
    }

    private static async Task InterruptedStartupRecoveryAsync(string root, string currentDirectory, string archive,
        AppPackageManifest current, AppPackageManifest next)
    {
        await using var fixture = await Fixture.CreateAsync(root, currentDirectory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        fixture.Prepared = AppUpdateInstaller.Prepare(archive, fixture.Installed, AppUpdateBootstrap.ScratchRoot, next.Version, deadline.Token);
        string oldManifestHash = AppUpdatePackage.HashFile(Path.Combine(fixture.Installed, AppUpdatePackage.ManifestName));
        var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new IOException("The test host executable is unknown."))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--crash-apply");
        start.ArgumentList.Add(fixture.Prepared.Directory);
        var crashed = fixture.Track(Process.Start(start) ?? throw new IOException("Could not start the crash fixture."));
        Task<string> stdout = crashed.StandardOutput.ReadToEndAsync();
        Task<string> stderr = crashed.StandardError.ReadToEndAsync();
        await crashed.WaitForExitAsync(deadline.Token);
        string output = await stdout;
        string errors = await stderr;
        Require(crashed.ExitCode == CrashExitCode && output.Contains("CRASH CHECKPOINT", StringComparison.Ordinal),
            "The real updater child did not exit at its durable checkpoint: " + output + errors);
        Require(File.Exists(AppUpdatePackage.FilePath(fixture.Installed, Marker)), "The crash did not leave a genuinely installed new sidecar.");
        Require(AppUpdateInstaller.FindPending(fixture.Installed) is not null, "The crashed process did not leave a valid pending-recovery pointer.");
        RequireVersion(fixture.Executable, current.Version);
        // Launch the actual installed EXE. Only its production startup is allowed
        // to initiate recovery; this test never calls Recover itself.
        using var secondaryHandle = new Mutex(false, App.InstanceMutexName(fixture.Profile));
        var recoveryOwner = fixture.StartInstalled();
        var restarted = await fixture.WaitForRestartedGuiAsync(new[] { recoveryOwner.Id, crashed.Id }, deadline.Token);
        await recoveryOwner.WaitForExitAsync(deadline.Token);
        fixture.VerifyInstalled(current);
        Require(AppUpdatePackage.HashFile(Path.Combine(fixture.Installed, AppUpdatePackage.ManifestName)) == oldManifestHash,
            "Startup recovery did not restore the exact original manifest bytes.");
        Require(!File.Exists(AppUpdatePackage.FilePath(fixture.Installed, Marker)), "Recovery retained a newly introduced update sidecar.");
        Require(AppUpdateInstaller.FindPending(fixture.Installed) is null, "Startup recovery did not clear the pending pointer.");
        await fixture.VerifyProfileAndCanariesAsync();
        await CloseGuiAsync(restarted, deadline.Token);
        Console.WriteLine("PASS published startup recovery: abrupt Apply child exit leaves a real journal; installed EXE rolls back and restarts the old-version WPF GUI with profile/books intact");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; }
        public string Installed => Path.Combine(Root, "installed");
        public string Profile => Path.Combine(Root, "profile");
        public string Executable => Path.Combine(Installed, "TextDownloader.exe");
        public PreparedAppUpdate? Prepared { get; set; }
        private readonly List<(Process Process, long Started, string? Path)> _processes = new();
        private readonly Dictionary<string, string> _canaries = new(StringComparer.OrdinalIgnoreCase);
        private readonly Guid _queueId = Guid.NewGuid();
        private Fixture(string root) => Root = root;

        public static async Task<Fixture> CreateAsync(string root, string published)
        {
            var fixture = new Fixture(root);
            Directory.CreateDirectory(fixture.Installed);
            foreach (string path in Directory.EnumerateFiles(published, "*", SearchOption.AllDirectories))
            {
                string destination = Path.Combine(fixture.Installed, Path.GetRelativePath(published, path));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(path, destination, false);
            }
            string book = Path.Combine(root, "books", "preserved.epub");
            fixture.AddCanary(book, "outside-package user book");
            fixture.AddCanary(Path.Combine(fixture.Installed, "user-note.txt"), "unowned beside-package file");
            fixture.AddCanary(Path.Combine(fixture.Installed, "downloads", "preserved.fb2"), "inside-installation user book");
            fixture.AddCanary(Path.Combine(fixture.Profile, "cache", "user-cache.bin"), "existing profile cache");
            await new AppStore(fixture.Profile).SaveAsync(new StateSnapshot
            {
                Settings = new Settings { OutputDirectory = Path.GetDirectoryName(book)!, Format = BookFormat.Fb2, MinimizeToTray = false },
                Queue = new List<QueueItem>
                {
                    new() { Id = fixture._queueId, Url = "https://example.invalid/preserved-queue", Title = "Preserved queued story", Status = QueueStatus.Queued }
                },
                History = new List<HistoryEntry>
                {
                    new() { Url = "https://example.invalid/preserved-success", Title = "Preserved successful book", Pages = 7,
                        CompletedAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), Files = new List<string> { book } }
                }
            });
            return fixture;
        }

        private void AddCanary(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            _canaries.Add(path, AppUpdatePackage.HashFile(path));
        }

        public Process StartInstalled()
        {
            var start = new ProcessStartInfo(Executable) { UseShellExecute = false, WorkingDirectory = Installed };
            start.Environment["TD_DATA_DIRECTORY"] = Profile;
            return Track(Process.Start(start) ?? throw new IOException("Could not start the isolated installed application."));
        }

        public Process Track(Process process)
        {
            string? path = null;
            try { path = process.MainModule?.FileName; } catch (System.ComponentModel.Win32Exception) { }
            _processes.Add((process, process.StartTime.ToUniversalTime().Ticks, path));
            return process;
        }

        public async Task<Process> WaitForRestartedGuiAsync(IReadOnlyCollection<int> excluded, CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var candidate in Process.GetProcessesByName("TextDownloader"))
                {
                    bool selected = false;
                    try
                    {
                        if (!excluded.Contains(candidate.Id) && !candidate.HasExited &&
                            string.Equals(candidate.MainModule?.FileName, Executable, StringComparison.OrdinalIgnoreCase) && WpfWindow(candidate.Id) != IntPtr.Zero)
                        {
                            selected = true;
                            return Track(candidate);
                        }
                    }
                    catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                    finally { if (!selected) candidate.Dispose(); }
                }
                await Task.Delay(50, cancellationToken);
            }
        }

        public void VerifyInstalled(AppPackageManifest expected)
        {
            var actual = AppUpdatePackage.ReadManifest(Path.Combine(Installed, AppUpdatePackage.ManifestName));
            Require(actual.Version == expected.Version && actual.Files.Count == expected.Files.Count, "The installed package manifest has the wrong version or file count.");
            Require(actual.Files.OrderBy(file => file.Path, StringComparer.Ordinal).SequenceEqual(
                expected.Files.OrderBy(file => file.Path, StringComparer.Ordinal)), "The installed manifest's file records differ from the published package.");
            // Do not use ValidateDirectory here: deliberately retained user files
            // are outside the package and must continue to exist beside it.
            foreach (var file in expected.Files)
            {
                string path = AppUpdatePackage.FilePath(Installed, file.Path);
                Require(new FileInfo(path).Length == file.Size && AppUpdatePackage.HashFile(path) == file.Sha256,
                    "Installed package bytes differ: " + file.Path);
            }
            RequireVersion(Executable, expected.Version);
        }

        public async Task VerifyProfileAndCanariesAsync()
        {
            var restored = await new AppStore(Profile).LoadAsync();
            Require(restored.Queue.Count == 1 && restored.Queue[0].Id == _queueId &&
                restored.Queue[0].Url == "https://example.invalid/preserved-queue" && restored.Queue[0].Title == "Preserved queued story" &&
                restored.Queue[0].Status == QueueStatus.Queued,
                "The updater lost the queue or automatically started it.");
            Require(restored.History.Count == 1 && restored.History[0].Url == "https://example.invalid/preserved-success" &&
                restored.History[0].Title == "Preserved successful book" && restored.History[0].Pages == 7 &&
                restored.History[0].CompletedAt == new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero) &&
                restored.History[0].Files.SequenceEqual(new[] { Path.Combine(Root, "books", "preserved.epub") }),
                "Successful history changed during update/restart.");
            Require(restored.Settings.OutputDirectory == Path.Combine(Root, "books") && restored.Settings.Format == BookFormat.Fb2,
                "The updater lost output/format settings.");
            foreach (var canary in _canaries)
                Require(File.Exists(canary.Key) && AppUpdatePackage.HashFile(canary.Key) == canary.Value, "User file/cache/book changed: " + canary.Key);
        }

        public async ValueTask DisposeAsync()
        {
            // Also find the recovery helper started by the real application,
            // whose PID is intentionally not supplied by a production test hook.
            if (Prepared is not null)
                foreach (var process in Process.GetProcesses())
                {
                    bool selected = false;
                    try
                    {
                        if (process.ProcessName.StartsWith("TextDownloader.Update.", StringComparison.Ordinal) && !process.HasExited &&
                            string.Equals(Path.GetDirectoryName(process.MainModule?.FileName), Prepared.Directory, StringComparison.OrdinalIgnoreCase))
                        {
                            if (_processes.All(item => item.Process.Id != process.Id)) { Track(process); selected = true; }
                        }
                    }
                    catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                    finally { if (!selected) process.Dispose(); }
                }
            foreach (var owned in _processes)
            {
                try
                {
                    if (!owned.Process.HasExited && owned.Process.StartTime.ToUniversalTime().Ticks == owned.Started &&
                        (owned.Path is null || string.Equals(owned.Process.MainModule?.FileName, owned.Path, StringComparison.OrdinalIgnoreCase)))
                    {
                        owned.Process.Kill(true);
                        await owned.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    }
                }
                finally { owned.Process.Dispose(); }
            }
            Prepared?.Dispose();
            if (Prepared is not null) await DeleteOwnedDirectoryAsync(Prepared.Directory);
            await DeleteOwnedDirectoryAsync(Root);
        }
    }

    private static async Task CloseGuiAsync(Process process, CancellationToken cancellationToken)
    {
        process.Refresh();
        Require(process.CloseMainWindow(), "Could not close the actual WPF application normally.");
        await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        Require(process.ExitCode == 0, "The application failed while closing its window.");
    }

    private static void RequireVersion(string executable, string expected)
    {
        var version = AppUpdatePackage.ParseVersion(expected);
        var actual = FileVersionInfo.GetVersionInfo(executable);
        Require(actual.FileMajorPart == version.Major && actual.FileMinorPart == version.Minor && actual.FileBuildPart == version.Build,
            "The actual Windows PE version is not " + expected + ": " + actual.FileVersion);
    }

    private static async Task UntilAsync(Func<bool> predicate, CancellationToken cancellationToken, string message)
    {
        while (!predicate())
        {
            if (cancellationToken.IsCancellationRequested) throw new TimeoutException(message);
            await Task.Delay(50, cancellationToken);
        }
    }

    private static async Task DeleteOwnedDirectoryAsync(string directory)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, true); return; }
            catch (Exception error) when (attempt < 50 && error is IOException or UnauthorizedAccessException)
            { await Task.Delay(100); }
        }
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static IntPtr WpfWindow(int processId)
    {
        IntPtr result = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint owner);
            if (owner != processId) return true;
            var name = new StringBuilder(256);
            GetClassName(window, name, name.Capacity);
            var title = new StringBuilder(256);
            GetWindowText(window, title, title.Capacity);
            if (name.ToString().StartsWith("HwndWrapper[", StringComparison.Ordinal) && title.ToString() == "Text Downloader")
            { result = window; return false; }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private delegate bool EnumWindowProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int maximum);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximum);
}
