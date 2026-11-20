using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TextDownloader.Core;

/// <summary>Downloads and verifies all pages, retaining only replaceable text in its resume cache.</summary>
public sealed class StoryDownloader
{
    private const long MaxCacheBytes = 8 * 1024 * 1024;
    private const long MaxStoryCharacters = 50_000_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IStorySource _source;
    private readonly string _cacheDirectory;
    public int MaxPages { get; init; } = 10000;

    public StoryDownloader(IStorySource source, string cacheDirectory)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _cacheDirectory = Path.GetFullPath(cacheDirectory);
    }

    public async Task<StoryPage> PreviewAsync(Uri url, bool confirmAdult, CancellationToken token)
    {
        var first = _source.NormalizeToFirstPage(url);
        try { return await _source.GetPageAsync(first, confirmAdult, token).ConfigureAwait(false); }
        catch (SefanStoryScopeMismatchException) when (!SefanSource.IsNeutralGenre(first))
        {
            token.ThrowIfCancellationRequested();
            // Retry only the original story, never the rejected destination.
            return await _source.GetPageAsync(SefanSource.WithNeutralGenre(first), confirmAdult, token).ConfigureAwait(false);
        }
    }

    public async Task<BookDocument> DownloadAsync(Uri url, bool confirmAdult, bool refresh,
        IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        if (MaxPages < 1) throw new ArgumentOutOfRangeException(nameof(MaxPages));
        if (_source is IBookStorySource bookSource)
            return await bookSource.DownloadBookAsync(url, confirmAdult, refresh, _cacheDirectory,
                progress, cancellationToken).ConfigureAwait(false);
        var first = _source.NormalizeToFirstPage(url);
        try { return await DownloadAttemptAsync(first, confirmAdult, refresh, progress, cancellationToken).ConfigureAwait(false); }
        catch (SefanStoryScopeMismatchException) when (!SefanSource.IsNeutralGenre(first))
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new DownloadProgress(0, null,
                "Сайт изменил маршрут рассказа. Повторяем загрузку с первой страницы через /i/."));
            // A separate attempt has its own cache key and empty in-memory page
            // list. Its scope error, if any, is deliberately not retried again.
            return await DownloadAttemptAsync(SefanSource.WithNeutralGenre(first), confirmAdult, refresh,
                progress, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<BookDocument> DownloadAttemptAsync(Uri first, bool confirmAdult, bool refresh,
        IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var cache = Path.Combine(_cacheDirectory, Hash(Encoding.UTF8.GetBytes(first.AbsoluteUri)).ToLowerInvariant());
        var pages = new List<StoryPage>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        int? expectedTotal = null;
        long characters = 0;
        Uri? next = first;
        while (next is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(next.AbsoluteUri) || pages.Count >= MaxPages)
                throw new InvalidDataException("Повтор страницы или превышен предел страниц; книга не создана.");
            var number = pages.Count + 1;
            var page = refresh ? null : await ReadCacheAsync(cache, number, first, next, cancellationToken).ConfigureAwait(false);
            var wasCached = page is not null;
            page ??= await _source.GetPageAsync(next, confirmAdult, cancellationToken).ConfigureAwait(false);
            ValidatePage(page, first, next, number);
            if (page.TotalPages is { } total)
            {
                if (expectedTotal is not null && total != expectedTotal)
                    throw new InvalidDataException("Число страниц изменилось. Включите загрузку заново без кеша.");
                expectedTotal = total;
            }
            if (expectedTotal is not null && (number > expectedTotal || (number == expectedTotal && page.NextUrl is not null)))
                throw new InvalidDataException("Навигация не соответствует общему числу страниц.");
            if (!fingerprints.Add(Hash(Encoding.UTF8.GetBytes(page.Fragment))))
                throw new InvalidDataException("Сайт повторил ранее загруженный текст; книга не создана.");
            characters += page.Fragment.Length;
            if (characters > MaxStoryCharacters) throw new InvalidDataException("Текст превышает допустимый объём одного рассказа.");
            pages.Add(page);
            if (!wasCached) await WriteCacheAsync(cache, page, cancellationToken).ConfigureAwait(false);
            progress?.Report(new DownloadProgress(number, expectedTotal,
                $"Страница {number}{(expectedTotal is { } count ? " из " + count : "")}: {(wasCached ? "из кеша" : "загружена")}"));
            next = page.NextUrl;
        }
        if (pages.Count == 0 || expectedTotal is null || pages.Count != expectedTotal)
            throw new InvalidDataException("Не удалось подтвердить последнюю страницу. Неполный текст не экспортирован.");
        cancellationToken.ThrowIfCancellationRequested();
        var paragraphs = JoinParagraphs(pages);
        if (paragraphs.Count == 0) throw new InvalidDataException("В рассказе не найден текст.");
        progress?.Report(new DownloadProgress(pages.Count, expectedTotal, "Все страницы проверены; подготовка книги"));
        return new BookDocument(pages[0].Title, pages[0].Author, first, paragraphs, pages.Count);
    }

    public static IReadOnlyList<string> JoinParagraphs(IEnumerable<StoryPage> pages)
    {
        // No separator here: a page can break inside a word or before punctuation.
        var text = string.Concat(pages.Select(p => p.Fragment.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')));
        return Regex.Split(text, @"\n+").Select(SefanSource.CleanText).Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
    }

    private static void ValidatePage(StoryPage page, Uri first, Uri requested, int expectedNumber)
    {
        if (page is null || page.Url is null || page.Number != expectedNumber || string.IsNullOrWhiteSpace(page.Title) ||
            string.IsNullOrWhiteSpace(SefanSource.CleanText(page.Fragment ?? "")))
            throw new InvalidDataException("Недопустимый номер или пустая страница рассказа.");
        var actual = SefanSource.SameStory(page.Url, first);
        var desired = SefanSource.SameStory(requested, first);
        if (SefanSource.PageNumber(actual) != expectedNumber || SefanSource.PageNumber(desired) != expectedNumber)
            throw new InvalidDataException("Пропущена страница рассказа.");
        if (page.NextUrl is not null && SefanSource.PageNumber(SefanSource.SameStory(page.NextUrl, first)) != expectedNumber + 1)
            throw new InvalidDataException("Следующая страница нарушает порядок рассказа.");
        if (page.TotalPages is { } total && (total < expectedNumber || (page.NextUrl is null && total > expectedNumber) ||
            (page.NextUrl is not null && total == expectedNumber)))
            throw new InvalidDataException("Число страниц не соответствует навигации.");
    }

    private static async Task<StoryPage?> ReadCacheAsync(string directory, int number, Uri first, Uri requested, CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, $"{number:D5}.json");
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxCacheBytes) return null;
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (bytes.Length > MaxCacheBytes) return null;
            var record = JsonSerializer.Deserialize<CachedPage>(bytes, JsonOptions);
            if (record?.Version != 1 || record.Page is null || string.IsNullOrWhiteSpace(record.Hash)) return null;
            var encoded = JsonSerializer.SerializeToUtf8Bytes(record.Page, JsonOptions);
            if (!record.Hash.Equals(Hash(encoded), StringComparison.Ordinal)) return null;
            ValidatePage(record.Page, first, requested, number);
            return record.Page;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            // A cache is replaceable. A corrupt record is fetched again, not treated as completed work.
            return null;
        }
    }

    private static async Task WriteCacheAsync(string directory, StoryPage page, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{page.Number:D5}.json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var encoded = JsonSerializer.SerializeToUtf8Bytes(page, JsonOptions);
        var record = new CachedPage(1, Hash(encoded), page);
        try
        {
            await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private sealed record CachedPage(int Version, string Hash, StoryPage Page);
}
