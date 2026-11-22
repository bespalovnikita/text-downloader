namespace TextDownloader.Core;

/// <summary>A source whose logical book is assembled from independently addressed posts.</summary>
public interface IBookStorySource : IStorySource
{
    Task<BookDocument> DownloadBookAsync(Uri url, bool confirmAdult, bool refresh, string cacheDirectory,
        IProgress<DownloadProgress>? progress, CancellationToken cancellationToken, DownloadRecovery? recovery = null);
}
