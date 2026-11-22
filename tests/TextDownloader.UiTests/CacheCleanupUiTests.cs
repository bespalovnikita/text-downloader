using System.IO;
using System.Security.Cryptography;
using System.Text;
using TextDownloader;
using TextDownloader.Core;

internal static class CacheCleanupUiTests
{
    public static async Task RunAsync(string profile)
    {
        var store = new AppStore(profile);
        QueueItem Task(string slug, QueueStatus status, bool dedicated = true)
        {
            var uri = new Uri($"https://sefan.ru/stories/ru/2/drama/{slug}_1.html");
            return new QueueItem
            {
                Url = uri.AbsoluteUri, Status = status, UsesDedicatedCache = dedicated,
                ResumeDownload = status != QueueStatus.Completed, CompletedPages = 2, TotalPages = 3,
                Checkpoint = new DownloadCheckpoint(uri, uri, new Uri(uri.AbsoluteUri.Replace("_1.html", "_1-3.html")))
            };
        }

        string Job(Guid id) => Path.Combine(store.CacheDirectory, "jobs", id.ToString("N"));
        var old = Task("old", QueueStatus.Paused);
        var fresh = Task("fresh", QueueStatus.Failed);
        var completed = Task("completed", QueueStatus.Completed);
        var shared = Task("shared", QueueStatus.Failed, dedicated: false);
        var legacy = Task("legacy", QueueStatus.Paused, dedicated: false);
        legacy.CacheRunId = Guid.NewGuid();
        string oldRefresh = Path.Combine(store.CacheDirectory, "refresh", legacy.CacheRunId.Value.ToString("N"));
        string sharedKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(shared.Url))).ToLowerInvariant();
        string sharedPath = Path.Combine(store.CacheDirectory, sharedKey);
        string orphan = Job(Guid.NewGuid());
        string finishedOrphan = Job(Guid.NewGuid());
        async Task WriteAsync(string folder, int days, string name = "00002.json")
        {
            string path = Path.Combine(folder, "chapter", name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "Синтетическая страница.");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-days));
        }
        foreach (string folder in new[] { Job(old.Id), Job(fresh.Id), Job(completed.Id), oldRefresh, sharedPath, orphan, finishedOrphan })
            await WriteAsync(folder, 4);
        await WriteAsync(Job(fresh.Id), 2, "00003.json");
        await File.WriteAllTextAsync(Path.Combine(finishedOrphan, ".completed"), "");
        await store.SaveAsync(new StateSnapshot { Queue = [old, fresh, completed, shared, legacy] });

        var window = new MainWindow(profile, enableTray: false);
        window.Show();
        await window.Ready.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert(!Directory.Exists(Job(old.Id)) && !Directory.Exists(oldRefresh) && !Directory.Exists(orphan),
                "Expired owned cache folders were not removed.");
            Assert(Directory.Exists(Job(fresh.Id)) && Directory.Exists(Job(completed.Id)) &&
                Directory.Exists(finishedOrphan) && Directory.Exists(sharedPath), "Fresh, completed or shared caches were deleted.");
            Assert(window.Items.Where(item => item.Id == old.Id || item.Id == shared.Id || item.Id == legacy.Id)
                .All(item => item.CompletedPages == 0 && item.TotalPages is null && item.Checkpoint is null &&
                    item.UsesDedicatedCache && item.CacheRunId is null && !item.ResumeDownload), "Expired jobs kept their resume point.");
            Assert(window.Items.Single(item => item.Id == fresh.Id).CompletedPages == 2 && !window.IsBusy,
                "Cleanup changed fresh progress or started downloading.");
        }
        finally { window.Close(); }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
