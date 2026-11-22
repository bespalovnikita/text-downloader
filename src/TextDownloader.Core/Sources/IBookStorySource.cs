namespace TextDownloader.Core;

/// <summary>A source whose logical book is assembled from independently addressed posts.</summary>
public interface IBookStorySource : IStorySource
{
    Task<BookDocument> DownloadBookAsync(Uri url, bool confirmAdult, bool refresh, string cacheDirectory,
        IProgress<DownloadProgress>? progress, CancellationToken cancellationToken, DownloadRecovery? recovery = null);
}

/// <summary>A series whose parts can reuse the common page downloader and cache.</summary>
internal interface IPagedBookSource : IStorySource
{
    Task<BookDocument> DownloadSeriesAsync(StoryDownloader downloader, Uri first, bool confirmAdult, bool refresh,
        IProgress<DownloadProgress>? progress, CancellationToken cancellationToken, DownloadRecovery? recovery);
}
