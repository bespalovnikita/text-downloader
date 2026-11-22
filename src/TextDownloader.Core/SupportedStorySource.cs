namespace TextDownloader.Core;

/// <summary>Routes supported reading URLs without applying Sefan URL rules to another site.</summary>
public sealed class SupportedStorySource : IBookStorySource
{
    private readonly SefanSource _sefan;
    private readonly BestweaponSource _bestweapon;

    public SupportedStorySource(SefanSource? sefan = null, BestweaponSource? bestweapon = null)
    {
        _sefan = sefan ?? new SefanSource();
        _bestweapon = bestweapon ?? new BestweaponSource();
    }

    /// <summary>Creates sources that share request spacing per site across previews and downloads.</summary>
    public static Func<TimeSpan, IStorySource> CreateFactory()
    {
        var sefanThrottle = new PageRequestThrottle();
        var bestweaponThrottle = new PageRequestThrottle();
        return interval => new SupportedStorySource(new SefanSource(null, interval, sefanThrottle),
            new BestweaponSource(null, interval, bestweaponThrottle));
    }

    public bool CanHandle(Uri uri) => _sefan.CanHandle(uri) || _bestweapon.CanHandle(uri);
    public Uri NormalizeToFirstPage(Uri uri) => SourceFor(uri).NormalizeToFirstPage(uri);
    public Task<StoryPage> GetPageAsync(Uri uri, bool confirmAdult, CancellationToken cancellationToken) =>
        SourceFor(uri).GetPageAsync(uri, confirmAdult, cancellationToken);

    public Task<BookDocument> DownloadBookAsync(Uri url, bool confirmAdult, bool refresh, string cacheDirectory,
        IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        IStorySource source = SourceFor(url);
        return source is IBookStorySource bookSource
            ? bookSource.DownloadBookAsync(url, confirmAdult, refresh, cacheDirectory, progress, cancellationToken)
            : new StoryDownloader(source, cacheDirectory).DownloadAsync(url, confirmAdult, refresh, progress, cancellationToken);
    }

    private IStorySource SourceFor(Uri uri)
    {
        if (_sefan.CanHandle(uri)) return _sefan;
        if (_bestweapon.CanHandle(uri)) return _bestweapon;
        throw new ArgumentException("Поддерживаются страницы чтения Sefan и публикации bestweapon.cc/post_номер.");
    }

    public void Dispose()
    {
        _sefan.Dispose();
        _bestweapon.Dispose();
    }
}
