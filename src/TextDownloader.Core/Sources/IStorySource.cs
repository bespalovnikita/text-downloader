namespace TextDownloader.Core;

public sealed record StorySourceInfo(string Name, string CountLabel = "страниц");

public interface IStorySource : IDisposable
{
    bool CanHandle(Uri uri);
    Uri NormalizeToFirstPage(Uri uri);
    Task<StoryPage> GetPageAsync(Uri uri, bool confirmAdult, CancellationToken cancellationToken);

    StorySourceInfo Describe(Uri uri) => new(uri.Host);
    string DescribePreview(StoryPage page) => "Начало текста. Файлы появятся после завершения загрузки.";

    bool CanRecover(Uri uri) => CanHandle(uri);

    Uri ValidateReplacementPage(Uri expected, Uri candidate)
    {
        if (CanRecover(expected) && expected == candidate) return expected;
        throw new ArgumentException("Укажите адрес той же страницы, на которой остановилась загрузка.");
    }

    // Paged sources can verify URL-specific numbering independently of the
    // number parsed from the page. Cursor-based sources can leave it unspecified.
    int? GetPageNumber(Uri page) => null;

    Uri ValidatePageRoute(Uri first, Uri candidate, int? expectedNumber)
    {
        if (CanHandle(candidate) && NormalizeToFirstPage(candidate) == NormalizeToFirstPage(first))
            return candidate;
        throw new InvalidDataException("Адрес ведёт на страницу другого произведения.");
    }

    // A null error asks whether there is an alternate route for a saved checkpoint.
    Uri? GetAlternateStart(Uri first, Exception? error = null) => null;
}
