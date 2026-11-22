using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace TextDownloader;

internal sealed class QueueCache(string root)
{
    private const string CompletedMarker = ".completed";
    private static readonly TimeSpan Retention = TimeSpan.FromDays(3);
    private string Jobs => Path.Combine(root, "jobs");
    private string Refresh => Path.Combine(root, "refresh");

    public string For(QueueItem item) => item.UsesDedicatedCache
        ? Path.Combine(Jobs, (item.CacheRunId ?? item.Id).ToString("N"))
        : item.CacheRunId is { } oldRun ? Path.Combine(Refresh, oldRun.ToString("N")) : root;

    public static Task MarkCompletedAsync(string directory) =>
        Directory.Exists(directory) ? File.WriteAllBytesAsync(Path.Combine(directory, CompletedMarker), []) : Task.CompletedTask;

    public (int Removed, int Reset, IReadOnlyList<string> Warnings) CleanExpired(StateSnapshot state, DateTimeOffset now)
    {
        DateTimeOffset cutoff = now - Retention;
        var warnings = new List<string>();
        int removed = 0;
        int reset = 0;
        var owned = state.Queue.Where(item => item.UsesDedicatedCache || item.CacheRunId is not null)
            .ToLookup(For, StringComparer.OrdinalIgnoreCase);

        string[] folders;
        try
        {
            if (SafeDirectory(root) is null) return (0, 0, []);
            var jobs = SafeDirectory(Jobs);
            var refresh = SafeDirectory(Refresh);
            folders = (jobs?.EnumerateDirectories() ?? [])
                .Where(directory => Guid.TryParseExact(directory.Name, "N", out _))
                .Select(directory => directory.FullName)
                .Concat(owned.Select(group => group.Key).Where(path =>
                    Path.GetDirectoryName(path) == Jobs && jobs is not null ||
                    Path.GetDirectoryName(path) == Refresh && refresh is not null))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return (0, 0, [error.Message]);
        }

        foreach (string path in folders)
        {
            try
            {
                var items = owned[path];
                if (items.Any(item => item.Status == QueueStatus.Completed) || File.Exists(Path.Combine(path, CompletedMarker))) continue;
                var directory = SafeDirectory(path);
                if (directory is not null)
                {
                    if (LastWrite(directory) >= cutoff) continue;
                    directory.Delete(recursive: true);
                    removed++;
                }
                else if (Directory.Exists(path)) continue; // Leave reparse points in place.

                foreach (var item in items.Where(item => directory is not null || item.ResumeDownload ||
                    item.CompletedPages > 0 || item.Checkpoint is not null))
                {
                    ResetProgress(item);
                    reset++;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                warnings.Add(error.Message);
            }
        }

        // Older shared entries can overlap a completed book or another attempt.
        // Detach those stale attempts without deleting shared files.
        foreach (var item in state.Queue.Where(q => !q.UsesDedicatedCache && q.CacheRunId is null &&
            q.Status != QueueStatus.Completed && (q.ResumeDownload || q.CompletedPages > 0)))
        {
            try
            {
                var directories = TextDownloader.Core.StoryCacheLayout.LegacyDirectories(root, new Uri(item.Url), item.Checkpoint)
                    .Where(path => SafeDirectory(Path.GetDirectoryName(path)!) is not null)
                    .Select(SafeDirectory).OfType<DirectoryInfo>().ToArray();
                if (directories.Length == 0 || directories.Max(LastWrite) >= cutoff) continue;
                ResetProgress(item);
                reset++;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                warnings.Add(error.Message);
            }
        }

        return (removed, reset, warnings);
    }

    private static DirectoryInfo? SafeDirectory(string path)
    {
        var directory = new DirectoryInfo(path);
        return directory.Exists && !directory.Attributes.HasFlag(FileAttributes.ReparsePoint) ? directory : null;
    }

    private static DateTimeOffset LastWrite(DirectoryInfo directory)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        return directory.EnumerateFiles("*", options).Select(file => (DateTimeOffset?)file.LastWriteTimeUtc)
            .Max() ?? directory.LastWriteTimeUtc;
    }

    private static void ResetProgress(QueueItem item)
    {
        item.UsesDedicatedCache = true;
        item.CacheRunId = null;
        item.ResumeDownload = false;
        item.Checkpoint = null;
        item.ReplacementPageUrl = null;
        item.CompletedPages = 0;
        item.TotalPages = null;
        item.Message = "Срок хранения кеша истёк. Загрузка начнётся с первой страницы.";
    }
}
