namespace TextDownloader.Core;

/// <summary>Opens a page in the browser and gives its actual address and HTML to the source validator.</summary>
public interface IReadingBrowser
{
    Task<T> ReadPageAsync<T>(Uri target, Func<Uri, string, T> readPage, CancellationToken token) where T : class;
}

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
