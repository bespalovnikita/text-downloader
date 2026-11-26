namespace TextDownloader.Core;

/// <summary>Routes URLs and recovery requests to the registered source adapters.</summary>
public sealed class SupportedStorySource : IBookStorySource
{
    private readonly IStorySource[] _sources;

    public SupportedStorySource(SefanSource? sefan = null, BestweaponSource? bestweapon = null)
        : this(new IStorySource[] { sefan ?? new SefanSource(), bestweapon ?? new BestweaponSource() }
            .Concat(PublicFictionSites.All.Select(site => new PublicFictionSource(site,
                TimeSpan.FromSeconds(1), new PageRequestThrottle())))) { }

    public SupportedStorySource(IEnumerable<IStorySource> sources) => _sources = sources.ToArray();

    public static Func<TimeSpan, IStorySource> CreateFactory()
    {
        var create = CreateLoggedFactory();
        return interval => create(interval, null);
    }

    public static Func<TimeSpan, IProgress<DownloadLogEvent>?, IStorySource> CreateLoggedFactory()
    {
        var create = CreateSessionFactory();
        return (interval, log) => create(interval, log, null, null);
    }

    public static Func<TimeSpan, IProgress<DownloadLogEvent>?, IReadingBrowser?, SiteAuthorization?, IStorySource> CreateSessionFactory()
    {
        // Register new sources here. Each source retains its own request interval state
        // across its previews and downloads.
        var sefanThrottle = new PageRequestThrottle();
        var bestweaponThrottle = new PageRequestThrottle();
        var publicFiction = PublicFictionSites.All.Select(site => (Site: site, Throttle: new PageRequestThrottle())).ToArray();
        return (interval, log, browser, rule) => new SupportedStorySource(
            [new SefanSource(null, interval, sefanThrottle, log, browser, rule),
             new BestweaponSource(null, interval, bestweaponThrottle, log, browser, rule),
             .. publicFiction.Select(entry => new PublicFictionSource(entry.Site, interval, entry.Throttle, log,
                 rule?.Host == entry.Site.Host ? rule : null))]);
    }

    public bool CanHandle(Uri uri) => _sources.Any(source => source.CanHandle(uri));
    public bool CanRecover(Uri uri) => _sources.Any(source => source.CanRecover(uri));
    public bool CanImportSavedPage(Uri uri) => _sources.Any(source => source.CanImportSavedPage(uri));
    public bool CanReadInBrowser(Uri uri) => _sources.Any(source => source.CanReadInBrowser(uri));
    public Uri NormalizeToFirstPage(Uri uri) => SourceFor(uri).NormalizeToFirstPage(uri);
    public int? GetPageNumber(Uri uri) => SourceFor(uri).GetPageNumber(uri);
    public Uri ValidatePageRoute(Uri first, Uri candidate, int? expectedNumber) =>
        SourceFor(first).ValidatePageRoute(first, candidate, expectedNumber);
    public Task<StoryPage> ReadSavedPageAsync(Uri expected, Stream html, CancellationToken token) =>
        SourceFor(expected).ReadSavedPageAsync(expected, html, token);
    public StorySourceInfo Describe(Uri uri) => SourceFor(uri).Describe(uri);
    public string DescribePreview(StoryPage page) => SourceFor(page.Url).DescribePreview(page);
    public Uri? GetAlternateStart(Uri uri, Exception? error = null) =>
        _sources.FirstOrDefault(source => source.CanHandle(uri))?.GetAlternateStart(uri, error);

    public Uri ValidateReplacementPage(Uri expected, Uri candidate)
    {
        var source = _sources.FirstOrDefault(adapter => adapter.CanRecover(expected))
            ?? throw new ArgumentException("Адрес продолжения не поддерживается.");
        return source.ValidateReplacementPage(expected, candidate);
    }

    public Task<StoryPage> GetPageAsync(Uri uri, bool confirmAdult, CancellationToken cancellationToken) =>
        SourceFor(uri).GetPageAsync(uri, confirmAdult, cancellationToken);

    public Task<BookDocument> DownloadBookAsync(Uri url, bool confirmAdult, bool refresh, string cacheDirectory,
        IProgress<DownloadProgress>? progress, CancellationToken cancellationToken, DownloadRecovery? recovery = null) =>
        new StoryDownloader(SourceFor(url), cacheDirectory)
            .DownloadAsync(url, confirmAdult, refresh, progress, cancellationToken, recovery);

    private IStorySource SourceFor(Uri uri) => _sources.FirstOrDefault(source => source.CanHandle(uri))
        ?? throw new ArgumentException("Адрес страницы чтения не поддерживается.");

    public void Dispose()
    {
        foreach (var source in _sources) source.Dispose();
    }
}
