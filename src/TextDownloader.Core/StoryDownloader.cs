using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TextDownloader.Core;

/// <summary>Downloads and verifies all pages, retaining only replaceable text in its resume cache.</summary>
public sealed partial class StoryDownloader
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

    public static Uri ValidateReplacementPage(Uri expected, Uri candidate)
    {
        if (!expected.IsAbsoluteUri || !candidate.IsAbsoluteUri)
            throw new ArgumentException("Введите полный адрес страницы.");
        if (expected.Host is "sefan.ru" or "www.sefan.ru")
            return SefanSource.ValidateReplacementUrl(expected, candidate);
        var replacement = BestweaponSource.Normalize(candidate);
        if (BestweaponSource.Normalize(expected) == replacement) return replacement;
        throw new ArgumentException("Укажите адрес той же страницы, на которой остановилась загрузка.");
    }

    public async Task<BookDocument> DownloadAsync(Uri url, bool confirmAdult, bool refresh,
        IProgress<DownloadProgress>? progress, CancellationToken cancellationToken, DownloadRecovery? recovery = null)
    {
        if (MaxPages < 1) throw new ArgumentOutOfRangeException(nameof(MaxPages));
        if (_source is IBookStorySource bookSource)
            return await bookSource.DownloadBookAsync(url, confirmAdult, refresh, _cacheDirectory,
                progress, cancellationToken, recovery).ConfigureAwait(false);
        var first = _source.NormalizeToFirstPage(url);
        return _source is SefanSource sefan
            ? await DownloadSefanBookAsync(sefan, first, confirmAdult, refresh, progress, cancellationToken, recovery).ConfigureAwait(false)
            : await DownloadPartAsync(first, confirmAdult, refresh, progress, cancellationToken, recovery).ConfigureAwait(false);
    }

    private async Task<BookDocument> DownloadPartAsync(Uri first, bool confirmAdult, bool refresh,
        IProgress<DownloadProgress>? progress, CancellationToken cancellationToken, DownloadRecovery? recovery, StoryPage? firstPage = null,
        Action<StoryPage>? validateStart = null)
    {
        first = GetAttemptStart(first, refresh, recovery);
        try { return await DownloadAttemptAsync(first, confirmAdult, refresh, progress, cancellationToken, recovery, firstPage, validateStart).ConfigureAwait(false); }
        catch (SefanStoryScopeMismatchException) when (!SefanSource.IsNeutralGenre(first))
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new DownloadProgress(0, null,
                "Сайт изменил маршрут рассказа. Повторяем загрузку с первой страницы через /i/.")
                { LogLevel = DownloadLogLevel.Warning });
            // A separate attempt has its own cache key and empty in-memory page
            // list. Its scope error, if any, is deliberately not retried again.
            return await DownloadAttemptAsync(SefanSource.WithNeutralGenre(first), confirmAdult, refresh,
                progress, cancellationToken, recovery, validateStart: validateStart).ConfigureAwait(false);
        }
    }

    private async Task<BookDocument> DownloadAttemptAsync(Uri first, bool confirmAdult, bool refresh,
        IProgress<DownloadProgress>? progress, CancellationToken cancellationToken, DownloadRecovery? recovery, StoryPage? firstPage = null,
        Action<StoryPage>? validateStart = null)
    {
        var cache = PartCacheDirectory(first);
        var pages = new List<StoryPage>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        int? expectedTotal = null;
        long characters = 0;
        Uri? next = first;
        Uri? replacement = GetReplacement(first, recovery);
        int? replacementNumber = replacement is not null ? SefanSource.PageNumber(replacement) : null;
        while (next is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(next.AbsoluteUri) || pages.Count >= MaxPages)
                throw new InvalidDataException("Повтор страницы или превышен предел страниц; книга не создана.");
            var number = pages.Count + 1;
            var page = number == 1 ? firstPage : null;
            page ??= refresh ? null : await ReadCacheAsync(cache, number, first, next, cancellationToken).ConfigureAwait(false);
            string? fingerprint = null;
            if (page is not null)
            {
                try
                {
                    if (number == 1) validateStart?.Invoke(page);
                    fingerprint = ValidateChainPage(page, expectedTotal, fingerprints);
                }
                catch (InvalidDataException) { page = null; }
            }
            var wasCached = page is not null;
            if (page is null)
            {
                // Once a gap is found, only the validated prefix belongs to this run.
                refresh = true;
                Uri requested = number == replacementNumber ? replacement! : next;
                progress?.Report(new DownloadProgress(pages.Count, expectedTotal, $"Получаем страницу {number}…")
                {
                    Checkpoint = new DownloadCheckpoint(first, pages.LastOrDefault()?.Url, requested)
                });
                page = await _source.GetPageAsync(requested, confirmAdult, cancellationToken).ConfigureAwait(false);
                ValidatePage(page, first, requested, number);
                if (number == 1) validateStart?.Invoke(page);
                fingerprint = ValidateChainPage(page, expectedTotal, fingerprints);
            }
            characters += page.Fragment.Length;
            if (characters > MaxStoryCharacters) throw new InvalidDataException("Текст превышает допустимый объём одного рассказа.");
            if (!wasCached) await WriteCacheAsync(cache, page, cancellationToken).ConfigureAwait(false);
            expectedTotal ??= page.TotalPages;
            fingerprints.Add(fingerprint!);
            pages.Add(page);
            progress?.Report(new DownloadProgress(number, expectedTotal,
                $"Страница {number}{(expectedTotal is { } count ? " из " + count : "")}: {(wasCached ? "из кеша" : "загружена")}")
            {
                Checkpoint = new DownloadCheckpoint(first, page.Url, page.NextUrl)
            });
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

    private string PartCacheDirectory(Uri first) =>
        Path.Combine(_cacheDirectory, Hash(Encoding.UTF8.GetBytes(first.AbsoluteUri)).ToLowerInvariant());

    private static Uri GetAttemptStart(Uri first, bool refresh, DownloadRecovery? recovery)
    {
        if (refresh || recovery?.Checkpoint?.AttemptStartUrl is not { } saved) return first;
        Uri neutral = SefanSource.WithNeutralGenre(first);
        return neutral == saved ? neutral : first;
    }

    private static Uri? GetReplacement(Uri first, DownloadRecovery? recovery) =>
        recovery?.ReplacementPageUrl is { } url && SefanSource.IsSameReadingStory(url, first) ? SefanSource.Normalize(url) : null;

    public static IReadOnlyList<string> JoinParagraphs(IEnumerable<StoryPage> pages)
    {
        // No separator here: a page can break inside a word or before punctuation.
        var text = string.Concat(pages.Select(p => p.Fragment.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')));
        return Regex.Split(text, @"\n+").Select(SefanSource.CleanText).Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
    }

    private static string ValidateChainPage(StoryPage page, int? expectedTotal, HashSet<string> fingerprints)
    {
        if (expectedTotal is not null && page.TotalPages is not null && page.TotalPages != expectedTotal)
            throw new InvalidDataException("Число страниц изменилось. Включите загрузку заново без кеша.");
        int? total = expectedTotal ?? page.TotalPages;
        if (page.NextUrl is null ? total != page.Number : total is not null && page.Number >= total)
            throw new InvalidDataException("Не удалось подтвердить полноту: число страниц не соответствует навигации.");
        string fingerprint = Hash(Encoding.UTF8.GetBytes(page.Fragment));
        if (fingerprints.Contains(fingerprint))
            throw new InvalidDataException("Сайт повторил ранее загруженный текст; книга не создана.");
        return fingerprint;
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
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
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
