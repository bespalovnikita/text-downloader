using System.Diagnostics;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TextDownloader.Core.Updates;

internal static class UpdateTests
{
    private const string ManifestName = "windows-package.json";
    private const string PointerName = ".text-downloader-update.json";
    private static readonly Dictionary<string, byte[]> OldFiles = new()
    {
        ["TextDownloader.exe"] = Encoding.UTF8.GetBytes("synthetic old executable"),
        ["TextDownloader.Core.dll"] = Encoding.UTF8.GetBytes("synthetic old core"),
        ["assets/app.ico"] = [1, 2, 3, 4],
        ["docs/old.md"] = Encoding.UTF8.GetBytes("old owned document"),
    };
    private static readonly Dictionary<string, byte[]> NewFiles = new()
    {
        ["TextDownloader.exe"] = Encoding.UTF8.GetBytes("synthetic new executable"),
        ["TextDownloader.Core.dll"] = Encoding.UTF8.GetBytes("synthetic new core"),
        ["assets/app.ico"] = [5, 6, 7, 8],
        ["docs/new.md"] = Encoding.UTF8.GetBytes("new owned document"),
    };

    public static async Task RunAsync()
    {
        UpgradePreservesExtras();
        InvalidPackagesAreRejected();
        CollisionsAndChangedOwnedFilesAreRejected();
        CancellationDoesNotInstall();
        FaultRollsBackExactOldFiles();
        RecoveryNeverOverwritesForeignChanges();
        ReparsePointsAreRejected();
        LockedWindowsFileRollsBack();
        await CrashRecoveryAndOwnerValidationAsync();
    }

    // A child mode belongs to this test executable only, never the released application.
    public static int RunCrashChild(string[] args)
    {
        if (args.Length != 4) return 2;
        using var prepared = AppUpdateInstaller.Prepare(args[1], args[2], args[3], "0.2.0", CancellationToken.None);
        AppUpdateInstaller.Apply(prepared, _ => Environment.Exit(73));
        return 3;
    }

    private static void UpgradePreservesExtras()
    {
        using var f = new Fixture();
        var extras = SnapshotExtras(f.Target);
        using var prepared = f.Prepare();
        Assert(prepared.Version == "0.2.0", "Prepared version mismatch.");
        Assert(!File.Exists(Path.Combine(f.Target, PointerName)), "Prepare changed the installed application.");
        Assert(File.ReadAllBytes(Path.Combine(f.Target, "TextDownloader.exe")).SequenceEqual(OldFiles["TextDownloader.exe"]), "Prepare installed executable early.");
        AppUpdateInstaller.Apply(prepared);
        foreach (var file in NewFiles) Bytes(file.Value, File.ReadAllBytes(Path.Combine(f.Target, file.Key)), "New owned file: " + file.Key);
        Assert(!File.Exists(Path.Combine(f.Target, "docs/old.md")), "Removed package file remained after upgrade.");
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(f.Target, ManifestName)));
        Assert(manifest.RootElement.GetProperty("version").GetString() == "0.2.0", "Manifest version not upgraded.");
        foreach (var file in extras) Bytes(file.Value, File.ReadAllBytes(Path.Combine(f.Target, file.Key)), "Unowned user file changed: " + file.Key);
        Assert(AppUpdateInstaller.FindPending(f.Target) is null, "Completed update left a pending pointer.");
        string foreign = Path.Combine(f.Scratch, "unrelated", "keep.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(foreign)!);
        File.WriteAllText(foreign, "foreign scratch content");
        AppUpdateInstaller.CleanupCompleted(f.Scratch, _ => false);
        Assert(File.Exists(foreign), "Cleanup deleted an unowned directory.");
    }

    private static void InvalidPackagesAreRejected()
    {
        // Each case uses real ZIP bytes and verifies that the original installation is untouched.
        foreach (string badPath in new[] { "../escaped.md", "/absolute.md", "docs/../escaped.md", "C:/escaped.md", "docs\\bad.md" })
        {
            using var f = new Fixture();
            var original = Snapshot(f.Target);
            string zip = f.Package("bad-path.zip", additional: [(badPath, Encoding.UTF8.GetBytes("unsafe"))]);
            ThrowsInvalid(() => AppUpdateInstaller.Prepare(zip, f.Target, f.Scratch, "0.2.0", CancellationToken.None));
            SameSnapshot(original, f.Target, "Unsafe ZIP path changed target.");
            Assert(!File.Exists(Path.Combine(f.Root, "escaped.md")), "ZIP escaped its staging directory.");
        }
        foreach (string bad in new[] { "hash", "duplicate-entry", "duplicate-manifest", "application", "version", "size", "unlisted", "zip-link" })
        {
            using var f = new Fixture();
            var original = Snapshot(f.Target);
            byte[] manifest = Manifest("0.2.0", NewFiles);
            var entries = NewFiles.Select(file => (file.Key, file.Value)).ToList();
            if (bad == "hash") entries[0] = (entries[0].Key, Encoding.UTF8.GetBytes("tampered content"));
            if (bad == "duplicate-entry") entries.Add(entries[0]);
            if (bad == "unlisted") entries.Add(("docs/unlisted.md", Encoding.UTF8.GetBytes("not in manifest")));
            if (bad is "duplicate-manifest" or "application" or "version" or "size")
            {
                var json = JsonNode.Parse(manifest)!.AsObject();
                if (bad == "duplicate-manifest") json["files"]!.AsArray().Add(json["files"]![0]!.DeepClone());
                if (bad == "application") json["application"] = "VideoDownloader";
                if (bad == "version") json["version"] = "99.0.0";
                if (bad == "size") json["files"]![0]!["size"] = long.MaxValue;
                manifest = JsonSerializer.SerializeToUtf8Bytes(json);
            }
            string zip = f.Package("bad-" + bad + ".zip", entries, manifest, symbolicLink: bad == "zip-link");
            ThrowsInvalid(() => AppUpdateInstaller.Prepare(zip, f.Target, f.Scratch, "0.2.0", CancellationToken.None));
            SameSnapshot(original, f.Target, "Invalid package changed target: " + bad);
        }
        using (var f = new Fixture())
        {
            var original = Snapshot(f.Target);
            byte[] zip = File.ReadAllBytes(f.Zip);
            int header = -1;
            for (int index = 0; index <= zip.Length - 46; index++)
                if (BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(index, 4)) == 0x02014b50) { header = index; break; }
            Assert(header >= 0, "Synthetic ZIP has no central entry.");
            // An enormous expanded-length claim must be refused before allocating/extracting it.
            BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(header + 24, 4), 2U * 1024 * 1024 * 1024);
            string oversized = Path.Combine(f.Root, "oversized-zip-entry.zip");
            File.WriteAllBytes(oversized, zip);
            ThrowsInvalid(() => AppUpdateInstaller.Prepare(oversized, f.Target, f.Scratch, "0.2.0", CancellationToken.None));
            SameSnapshot(original, f.Target, "Oversized ZIP entry changed target.");
        }
    }

    private static void CollisionsAndChangedOwnedFilesAreRejected()
    {
        using (var f = new Fixture())
        {
            File.WriteAllText(Path.Combine(f.Target, "docs/new.md"), "a user document, not package-owned");
            var original = Snapshot(f.Target);
            ThrowsInvalid(() => f.Prepare());
            SameSnapshot(original, f.Target, "A new package file overwrote a user file.");
        }
        using (var f = new Fixture())
        {
            File.WriteAllText(Path.Combine(f.Target, "TextDownloader.Core.dll"), "locally modified owned bytes");
            var original = Snapshot(f.Target);
            ThrowsInvalid(() => f.Prepare());
            SameSnapshot(original, f.Target, "Modified owned file was silently replaced.");
        }
    }

    private static void CancellationDoesNotInstall()
    {
        using var f = new Fixture();
        var original = Snapshot(f.Target);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Throws<OperationCanceledException>(() => AppUpdateInstaller.Prepare(f.Zip, f.Target, f.Scratch, "0.2.0", cancellation.Token));
        SameSnapshot(original, f.Target, "Cancelled preparation changed target.");
        Assert(AppUpdateInstaller.FindPending(f.Target) is null, "Cancelled preparation left an installation pointer.");
    }

    private static void FaultRollsBackExactOldFiles()
    {
        using var f = new Fixture();
        var original = Snapshot(f.Target);
        using var prepared = f.Prepare();
        bool mutated = false;
        Throws<IOException>(() => AppUpdateInstaller.Apply(prepared, _ =>
        {
            mutated = true;
            throw new IOException("Synthetic mid-apply disk failure.");
        }));
        Assert(mutated, "Fault hook was not reached after publication.");
        SameSnapshot(original, f.Target, "Rollback did not restore exact original bytes/file set.");
        Assert(AppUpdateInstaller.FindPending(f.Target) is null, "Successful rollback left a pending installation.");
    }

    private static void ReparsePointsAreRejected()
    {
        using var f = new Fixture();
        string target = Path.Combine(f.Target, "TextDownloader.Core.dll");
        string outside = Path.Combine(f.Root, "external-core.dll");
        File.WriteAllBytes(outside, OldFiles["TextDownloader.Core.dll"]);
        File.Delete(target);
        try { File.CreateSymbolicLink(target, outside); }
        catch (Exception error) when (error is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            Console.WriteLine("SKIP symlink creation unavailable: " + error.GetType().Name);
            return;
        }
        ThrowsInvalid(() => f.Prepare());
        Bytes(OldFiles["TextDownloader.Core.dll"], File.ReadAllBytes(outside), "Update followed a symlink to an external file.");
    }

    private static void RecoveryNeverOverwritesForeignChanges()
    {
        foreach (bool changeTouchedFile in new[] { false, true })
        {
            using var f = new Fixture();
            var original = Snapshot(f.Target);
            using var prepared = f.Prepare();
            string changedRelative = changeTouchedFile ? "TextDownloader.Core.dll" : "docs/old.md";
            string changedPath = Path.Combine(f.Target, changedRelative);
            byte[] foreign = Encoding.UTF8.GetBytes("foreign user bytes must survive rollback");
            Throws<AggregateException>(() => AppUpdateInstaller.Apply(prepared, _ =>
            {
                File.WriteAllBytes(changedPath, foreign);
                throw new IOException("Synthetic failure after an external edit.");
            }));
            Bytes(foreign, File.ReadAllBytes(changedPath), "Rollback overwrote an externally changed file.");
            if (!changeTouchedFile)
                Bytes(NewFiles["TextDownloader.Core.dll"], File.ReadAllBytes(Path.Combine(f.Target, "TextDownloader.Core.dll")),
                    "Recovery started mutating files before checking all live ownership hashes.");
            Assert(File.Exists(Path.Combine(f.Target, PointerName)) && Directory.Exists(prepared.BackupDirectory),
                "Failed rollback discarded its pointer or verified backup.");
            var blocked = Snapshot(f.Target);
            Throws<AggregateException>(() => AppUpdateInstaller.Recover(prepared));
            SameSnapshot(blocked, f.Target, "Explicit recovery changed files while a foreign edit was present.");
            File.WriteAllBytes(changedPath, changeTouchedFile ? NewFiles[changedRelative] : OldFiles[changedRelative]);
            AppUpdateInstaller.Recover(prepared);
            SameSnapshot(original, f.Target, "Recovery did not finish after restoring the expected owned bytes.");
        }
    }

    private static void LockedWindowsFileRollsBack()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new Fixture();
        var original = Snapshot(f.Target);
        using var locked = new FileStream(Path.Combine(f.Target, "TextDownloader.Core.dll"), FileMode.Open, FileAccess.Read, FileShare.Read);
        using var prepared = f.Prepare();
        Throws<IOException>(() => AppUpdateInstaller.Apply(prepared));
        SameSnapshot(original, f.Target, "A Windows sharing lock caused partial installation.");
    }

    private static async Task CrashRecoveryAndOwnerValidationAsync()
    {
        using var f = new Fixture();
        var original = Snapshot(f.Target);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.Environment["DOTNET_ROOT"] = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory()).Parent!.Parent!.Parent!.FullName;
        foreach (string argument in new[] { "--update-crash", f.Zip, f.Target, f.Scratch }) start.ArgumentList.Add(argument);
        using var child = Process.Start(start) ?? throw new IOException("Cannot launch updater test child.");
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        catch { try { child.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } throw; }
        Assert(child.ExitCode == 73, "Crash child did not stop at the mutation hook: " + await stdout + await stderr);
        using var pending = AppUpdateInstaller.FindPending(f.Target) ?? throw new IOException("Crash lost the durable recovery pointer.");
        Assert(pending.TargetDirectory == Path.GetFullPath(f.Target), "Recovery belongs to a different installation.");

        string other = Path.Combine(f.Root, "other-installation");
        WriteInstallation(other, "0.1.0", OldFiles);
        var otherOriginal = Snapshot(other);
        File.Copy(Path.Combine(f.Target, PointerName), Path.Combine(other, PointerName));
        ThrowsInvalid(() => AppUpdateInstaller.FindPending(other));
        File.Delete(Path.Combine(other, PointerName));
        SameSnapshot(otherOriginal, other, "Foreign pointer changed another installation.");

        string ownerFile = Path.Combine(pending.Directory, "update-owner.json");
        byte[] owner = File.ReadAllBytes(ownerFile);
        var stale = JsonNode.Parse(owner)!.AsObject();
        stale["TargetDirectory"] = other;
        File.WriteAllBytes(ownerFile, JsonSerializer.SerializeToUtf8Bytes(stale));
        ThrowsInvalid(() => AppUpdateInstaller.FindPending(f.Target));
        File.WriteAllBytes(ownerFile, owner);
        AppUpdateInstaller.Recover(pending);
        SameSnapshot(original, f.Target, "Crash recovery did not restore exact original installation.");
        Assert(AppUpdateInstaller.FindPending(f.Target) is null, "Recovery did not remove the durable pointer.");
    }

    private static Dictionary<string, byte[]> SnapshotExtras(string path) => Snapshot(path)
        .Where(file => !OldFiles.ContainsKey(file.Key) && file.Key != ManifestName).ToDictionary(file => file.Key, file => file.Value);
    private static Dictionary<string, byte[]> Snapshot(string path) => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
        .ToDictionary(file => Path.GetRelativePath(path, file).Replace('\\', '/'), File.ReadAllBytes);
    private static void SameSnapshot(Dictionary<string, byte[]> expected, string path, string message)
    {
        var actual = Snapshot(path);
        Assert(expected.Keys.Order().SequenceEqual(actual.Keys.Order()), message + " File set: " + string.Join(",", actual.Keys));
        foreach (var file in expected) Bytes(file.Value, actual[file.Key], message + " " + file.Key);
    }
    private static byte[] Manifest(string version, Dictionary<string, byte[]> files) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        schema = 1, application = "TextDownloader", version,
        files = files.Select(file => new { path = file.Key, size = file.Value.LongLength, sha256 = Convert.ToHexString(SHA256.HashData(file.Value)).ToLowerInvariant() }).ToArray(),
    });
    private static void WriteInstallation(string directory, string version, Dictionary<string, byte[]> files)
    {
        Directory.CreateDirectory(directory);
        foreach (var file in files)
        {
            string path = Path.Combine(directory, file.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, file.Value);
        }
        File.WriteAllBytes(Path.Combine(directory, ManifestName), Manifest(version, files));
    }
    private static void Bytes(byte[] expected, byte[] actual, string message) => Assert(expected.SequenceEqual(actual), message);
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static void ThrowsInvalid(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is IOException or InvalidDataException) { return; }
        throw new Exception("Expected invalid package or installation to be rejected.");
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "TextDownloader-UpdateTests-" + Guid.NewGuid().ToString("N"));
        public string Target => Path.Combine(Root, "installed");
        public string Scratch => Path.Combine(Root, "scratch");
        public string Zip { get; }
        public Fixture()
        {
            WriteInstallation(Target, "0.1.0", OldFiles);
            Directory.CreateDirectory(Path.Combine(Target, "profile"));
            File.WriteAllText(Path.Combine(Target, "profile/session.json"), "synthetic user settings");
            File.WriteAllText(Path.Combine(Target, "user-book.epub"), "synthetic user's book");
            Zip = Package("new.zip");
        }
        public PreparedAppUpdate Prepare() => AppUpdateInstaller.Prepare(Zip, Target, Scratch, "0.2.0", CancellationToken.None);
        public string Package(string name, IEnumerable<(string Key, byte[] Value)>? entries = null, byte[]? manifest = null,
            IEnumerable<(string Key, byte[] Value)>? additional = null, bool symbolicLink = false)
        {
            string path = Path.Combine(Root, name);
            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var file in (entries ?? NewFiles.Select(file => (file.Key, file.Value))).Concat(additional ?? []))
            {
                var entry = archive.CreateEntry(file.Key);
                if (symbolicLink && file.Key == "TextDownloader.Core.dll") entry.ExternalAttributes = unchecked((int)0xA1FF0000);
                using var stream = entry.Open();
                stream.Write(file.Value);
            }
            using (var stream = archive.CreateEntry(ManifestName).Open()) stream.Write(manifest ?? Manifest("0.2.0", NewFiles));
            return path;
        }
        public void Dispose() { try { Directory.Delete(Root, recursive: true); } catch (IOException) { } }
    }
}
