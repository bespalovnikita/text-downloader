namespace TextDownloader.Core;

/// <summary>Routes URLs and recovery requests to the registered source adapters.</summary>
public sealed class SupportedStorySource : IBookStorySource
{
    private readonly IStorySource[] _sources;

    public SupportedStorySource(SefanSource? sefan = null, BestweaponSource? bestweapon = null)
        : this(new IStorySource[] { sefan ?? new SefanSource(), bestweapon ?? new BestweaponSource() }) { }

    public SupportedStorySource(IEnumerable<IStorySource> sources) => _sources = sources.ToArray();

    public static Func<TimeSpan, IStorySource> CreateFactory()
    {
        var create = CreateLoggedFactory();
        return interval => create(interval, null);
    }

    public static Func<TimeSpan, IProgress<DownloadLogEvent>?, IStorySource> CreateLoggedFactory()
    {
        // Register new sources here. Each source retains its own request interval state
        // across its previews and downloads.
        Func<TimeSpan, IProgress<DownloadLogEvent>?, IStorySource>[] entries =
        [
            WithThrottle((interval, throttle, log) => new SefanSource(null, interval, throttle, log)),
            WithThrottle((interval, throttle, log) => new BestweaponSource(null, interval, throttle, log)),
        ];
        return (interval, log) => new SupportedStorySource(entries.Select(create => create(interval, log)));
    }

    private static Func<TimeSpan, IProgress<DownloadLogEvent>?, IStorySource> WithThrottle(
        Func<TimeSpan, PageRequestThrottle, IProgress<DownloadLogEvent>?, IStorySource> create)
    {
        var throttle = new PageRequestThrottle();
        return (interval, log) => create(interval, throttle, log);
    }

    public bool CanHandle(Uri uri) => _sources.Any(source => source.CanHandle(uri));
    public bool CanRecover(Uri uri) => _sources.Any(source => source.CanRecover(uri));
    public bool CanImportSavedPage(Uri uri) => _sources.Any(source => source.CanImportSavedPage(uri));
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
