using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TextDownloader.Core.Updates;

namespace TextDownloader.Updates;

internal sealed record AppUpdateHandoff(int Schema, string Directory, string TargetDirectory,
    int ParentId, long ParentStartTicks, string MutexName, string HelperName,
    string ProfileDirectory, bool Recovery);

internal sealed record AppUpdateResult(int Schema, string TargetDirectory, string Version,
    bool Success, bool Recovered, string? Error);

// Adapted from video-downloader's private single-file update handoff. A temporary
// copy of the installed EXE waits for its owner to exit, then updates and restarts it.
// These arguments are internal process coordination, not a user-facing CLI.
internal static class AppUpdateBootstrap
{
    private const string Argument = "--td-internal-update";
    private const string ResultArgument = "--td-update-result";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };

    internal static string ScratchRoot => Path.Combine(Path.GetTempPath(), "TextDownloaderUpdates");

    internal static Process StartHelper(PreparedAppUpdate prepared, string mutexName,
        Process? owner = null, string? sourceExecutable = null,
        string? profileDirectory = null, bool recovery = false)
    {
        string executable = Path.GetFullPath(sourceExecutable ?? Environment.ProcessPath ??
            throw new InvalidOperationException("Не удалось определить путь приложения."));
        if (!executable.Equals(Path.Combine(Path.GetFullPath(prepared.TargetDirectory), "TextDownloader.exe"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Для обновления установите полный Windows ZIP и запустите TextDownloader.exe.");
        string profile = Path.GetFullPath(profileDirectory ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TextDownloader"));
        if (!string.Equals(mutexName, App.InstanceMutexName(profile), StringComparison.Ordinal))
            throw new InvalidDataException("Обновление относится к другому профилю приложения.");
        AppUpdatePackage.AssertNoReparse(prepared.Directory);
        AppUpdatePackage.AssertNoReparse(executable);
        string name = "TextDownloader.Update." + Guid.NewGuid().ToString("N") + ".exe";
        string helper = Path.Combine(prepared.Directory, name);
        File.Copy(executable, helper, false);
        if (AppUpdatePackage.HashFile(helper) != AppUpdatePackage.HashFile(executable))
            throw new IOException("Не удалось проверить временную копию установщика.");
        using var current = owner is null ? Process.GetCurrentProcess() : null;
        owner ??= current!;
        string handoff = helper + ".json";
        WriteJson(handoff, new AppUpdateHandoff(1, prepared.Directory, prepared.TargetDirectory,
            owner.Id, owner.StartTime.ToUniversalTime().Ticks, mutexName, name, profile, recovery));
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, WorkingDirectory = prepared.Directory };
        start.Environment["TD_DATA_DIRECTORY"] = profile;
        start.ArgumentList.Add(Argument);
        start.ArgumentList.Add(handoff);
        return Process.Start(start) ?? throw new IOException("Не удалось запустить установщик обновления.");
    }

    internal static bool TryStartRecovery(string targetDirectory, string mutexName, string? profileDirectory = null)
    {
        var prepared = AppUpdateInstaller.FindPending(targetDirectory);
        if (prepared is null) return false;
        using var helper = StartHelper(prepared, mutexName, profileDirectory: profileDirectory, recovery: true);
        return true;
    }

    internal static void ScheduleCompletedCleanup()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            AppUpdateInstaller.CleanupCompleted(ScratchRoot, directory =>
            {
                foreach (string file in Directory.EnumerateFiles(directory, "TextDownloader.Update.*.exe").Take(8))
                    foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(file)))
                        using (process)
                        {
                            try
                            {
                                if (!process.HasExited && string.Equals(process.MainModule?.FileName, file, StringComparison.OrdinalIgnoreCase)) return true;
                            }
                            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
                            { return true; }
                        }
                return false;
            });
        });
    }

    internal static bool TryRun(string[] args)
    {
        if (args.Length == 0 || args[0] != Argument) return false;
        try { RunAsync(args).GetAwaiter().GetResult(); }
        catch (Exception error)
        {
            System.Windows.Forms.MessageBox.Show(
                "Не удалось завершить обновление. Файлы восстановления сохранены; не удаляйте их до восстановления приложения.\n\n" + error.Message,
                "Обновление Text Downloader", System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Warning);
        }
        return true;
    }

    private static async Task RunAsync(string[] args)
    {
        if (args.Length != 2) throw new InvalidDataException("Некорректный запуск установщика.");
        string file = Path.GetFullPath(args[1]);
        AppUpdatePackage.AssertNoReparse(file);
        if (new FileInfo(file).Length > 16 * 1024) throw new InvalidDataException("Слишком большой файл запуска установщика.");
        var handoff = JsonSerializer.Deserialize<AppUpdateHandoff>(File.ReadAllBytes(file), Json) ??
            throw new InvalidDataException("Пустые параметры установщика.");
        string directory = Path.GetFullPath(handoff.Directory);
        string root = Path.GetFullPath(ScratchRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string profile = Path.GetFullPath(handoff.ProfileDirectory);
        if (handoff.Schema != 1 || !directory.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            !Regex.IsMatch(Path.GetFileName(directory), @"^text-downloader-update-[0-9a-f]{32}$") ||
            !Regex.IsMatch(handoff.HelperName, @"^TextDownloader\.Update\.[0-9a-f]{32}\.exe$") ||
            !file.Equals(Path.Combine(directory, handoff.HelperName) + ".json", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Environment.ProcessPath, Path.Combine(directory, handoff.HelperName), StringComparison.OrdinalIgnoreCase) ||
            handoff.MutexName != App.InstanceMutexName(profile) || handoff.ParentId <= 0 || handoff.ParentStartTicks <= 0)
            throw new InvalidDataException("Параметры установщика не принадлежат этому процессу.");
        AppUpdatePackage.AssertNoReparse(directory);
        // The prepared directory is retained until the restarted application has
        // read the result and can clean it after this helper has actually exited.
        var prepared = handoff.Recovery
            ? AppUpdateInstaller.FindPending(handoff.TargetDirectory) ?? throw new InvalidDataException("Прерванное обновление не найдено.")
            : AppUpdateInstaller.OpenPrepared(directory);
        if (!prepared.Directory.Equals(directory, StringComparison.OrdinalIgnoreCase) ||
            !prepared.TargetDirectory.Equals(Path.GetFullPath(handoff.TargetDirectory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Обновление относится к другому каталогу приложения.");
        await WaitForOwnerAsync(handoff.ParentId, handoff.ParentStartTicks, TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false);
        Exception? failure = null;
        using (var singleton = new Mutex(false, handoff.MutexName))
        {
            bool acquired;
            try { acquired = singleton.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("Другая копия приложения ещё использует эту установку.");
            try
            {
                if (handoff.Recovery) AppUpdateInstaller.Recover(prepared);
                else AppUpdateInstaller.Apply(prepared);
            }
            catch (Exception error) { failure = error; }
            finally { singleton.ReleaseMutex(); }
        }
        // An incomplete rollback preserves its journal and backup, and must not
        // start an installation whose files could still belong to two versions.
        if (failure is AggregateException) throw failure;
        var outcome = new AppUpdateResult(1, prepared.TargetDirectory, prepared.Version,
            failure is null, handoff.Recovery, failure?.Message);
        string resultFile = Path.Combine(prepared.Directory, "result.json");
        WriteJson(resultFile, outcome);
        using var restarted = Restart(prepared.TargetDirectory, resultFile, profile);
    }

    internal static async Task WaitForOwnerAsync(int id, long startTicks, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Process? owner = null;
        try
        {
            try { owner = Process.GetProcessById(id); }
            catch (ArgumentException) { return; }
            try
            {
                if (owner.StartTime.ToUniversalTime().Ticks != startTicks) return;
                await owner.WaitForExitAsync(cancellationToken).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (
                (error is ArgumentException or InvalidOperationException ||
                 error is System.ComponentModel.Win32Exception { NativeErrorCode: 87 }) &&
                OriginalOwnerHasExited(id, startTicks))
            {
                // GetProcessById does not pin the process handle: it can exit
                // before StartTime/WaitForExitAsync opens that handle. Confirm
                // disappearance or PID reuse; access errors remain failures.
            }
        }
        finally { owner?.Dispose(); }
    }

    private static bool OriginalOwnerHasExited(int id, long startTicks)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var observed = Process.GetProcessById(id);
                return observed.HasExited || observed.StartTime.ToUniversalTime().Ticks != startTicks;
            }
            catch (ArgumentException) { return true; }
            catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 87) { return true; }
            catch (InvalidOperationException) { } // Retry a handle-opening race with a fresh Process.
        }
        return false;
    }

    internal static Process Restart(string directory, string? resultFile = null, string? profileDirectory = null)
    {
        var start = new ProcessStartInfo(Path.Combine(directory, "TextDownloader.exe"))
        { UseShellExecute = false, WorkingDirectory = directory };
        if (profileDirectory is not null) start.Environment["TD_DATA_DIRECTORY"] = profileDirectory;
        if (resultFile is not null) { start.ArgumentList.Add(ResultArgument); start.ArgumentList.Add(resultFile); }
        return Process.Start(start) ?? throw new IOException("Не удалось перезапустить приложение.");
    }

    internal static AppUpdateResult? ReadResult(string[] args, string targetDirectory)
    {
        int index = Array.IndexOf(args, ResultArgument);
        if (index < 0 || index + 1 >= args.Length) return null;
        try
        {
            string file = Path.GetFullPath(args[index + 1]);
            string root = Path.GetFullPath(ScratchRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(file) != "result.json" ||
                !Regex.IsMatch(Path.GetFileName(Path.GetDirectoryName(file)) ?? "", @"^text-downloader-update-[0-9a-f]{32}$")) return null;
            AppUpdatePackage.AssertNoReparse(file);
            if (new FileInfo(file).Length > 16 * 1024) return null;
            var result = JsonSerializer.Deserialize<AppUpdateResult>(File.ReadAllBytes(file), Json);
            return result?.Schema == 1 && string.Equals(Path.GetFullPath(result.TargetDirectory), Path.GetFullPath(targetDirectory), StringComparison.OrdinalIgnoreCase)
                ? result : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return null; }
    }

    private static void WriteJson<T>(string path, T data)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(output, data, Json);
                output.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { File.Delete(temporary); }
    }
}
