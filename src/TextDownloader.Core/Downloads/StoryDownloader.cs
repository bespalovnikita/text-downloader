using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TextDownloader.Core;

/// <summary>Downloads and verifies all pages, retaining only replaceable text in its resume cache.</summary>
public sealed class StoryDownloader
{
    private const long MaxCacheBytes = 8 * 1024 * 1024;
    internal const long MaxStoryCharacters = 50_000_000;
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
        catch (Exception error) when (_source.GetAlternateStart(first, error) is { } alternate)
        {
            token.ThrowIfCancellationRequested();
            // Retry only the original story, never the rejected destination.
            return await _source.GetPageAsync(alternate, confirmAdult, token).ConfigureAwait(false);
        }
    }

    public static Uri ValidateReplacementPage(Uri expected, Uri candidate)
    {
        if (!expected.IsAbsoluteUri || !candidate.IsAbsoluteUri)
            throw new ArgumentException("Введите полный адрес страницы.");
        using var sources = new SupportedStorySource();
        return sources.ValidateReplacementPage(expected, candidate);
    }

    public async Task<StoryPage> ImportSavedPageAsync(DownloadCheckpoint checkpoint, Stream html, CancellationToken token)
    {
        if (checkpoint.PendingPageUrl is not { } pending || !_source.CanImportSavedPage(pending) ||
            _source.GetPageNumber(pending) is not { } number)
            throw new InvalidDataException("Для этой страницы импорт HTML недоступен.");
        if (number < 1 || number > MaxPages)
            throw new InvalidDataException("Номер страницы выходит за пределы загрузки.");

        Uri first = _source.NormalizeToFirstPage(checkpoint.AttemptStartUrl);
        _source.ValidatePageRoute(first, pending, number);
        var page = await _source.ReadSavedPageAsync(pending, html, token).ConfigureAwait(false);
        ValidatePage(page, first, pending, number);

        string cache = PartCacheDirectory(first);
        int? total = null;
        long characters = page.Fragment.Length;
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        Uri? next = first;
        for (int current = 1; current < number; current++)
        {
            if (next is null || await ReadCacheAsync(cache, current, first, next, token).ConfigureAwait(false) is not { } saved)
                throw new InvalidDataException("Предыдущие страницы отсутствуют в кеше. Повторите загрузку перед импортом HTML.");
            fingerprints.Add(ValidateChainPage(saved, total, fingerprints));
            total ??= saved.TotalPages;
            characters += saved.Fragment.Length;
            next = saved.NextUrl;
        }
        if (next is null)
            throw new InvalidDataException("Сохранённая навигация не ведёт к импортируемой странице.");
        _source.ValidatePageRoute(first, next, number);
        if (characters > MaxStoryCharacters)
            throw new InvalidDataException("Текст превышает допустимый объём одного рассказа.");
        ValidateChainPage(page, total, fingerprints);
        await WriteCacheAsync(cache, page, token).ConfigureAwait(false);
        return page;
    }

    public async Task<BookDocument> DownloadAsync(Uri url, bool confirmAdult, bool refresh,
        IProgress<DownloadProgress>? progress, CancellationToken cancellationToken, DownloadRecovery? recovery = null)
    {
        if (MaxPages < 1) throw new ArgumentOutOfRangeException(nameof(MaxPages));
        if (_source is IBookStorySource bookSource)
            return await bookSource.DownloadBookAsync(url, confirmAdult, refresh, _cacheDirectory,
                progress, cancellationToken, recovery).ConfigureAwait(false);
        var first = _source.NormalizeToFirstPage(url);
        return _source is IPagedBookSource series
            ? await series.DownloadSeriesAsync(this, first, confirmAdult, refresh, progress, cancellationToken, recovery).ConfigureAwait(false)
            : await DownloadPartAsync(first, confirmAdult, refresh, progress, cancellationToken, recovery).ConfigureAwait(false);
    }

    internal async Task<BookDocument> DownloadPartAsync(Uri first, bool confirmAdult, bool refresh,
        IProgress<DownloadProgress>? progress, CancellationToken cancellationToken, DownloadRecovery? recovery, StoryPage? firstPage = null,
        Action<StoryPage>? validateStart = null)
    {
        first = GetAttemptStart(first, refresh, recovery);
        try { return await DownloadAttemptAsync(first, confirmAdult, refresh, progress, cancellationToken, recovery, firstPage, validateStart).ConfigureAwait(false); }
        catch (Exception error) when (_source.GetAlternateStart(first, error) is { } alternate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new DownloadProgress(0, null,
                "Сайт изменил маршрут рассказа. Повторяем загрузку с первой страницы.")
                { LogLevel = DownloadLogLevel.Warning });
            // A separate attempt has its own cache key and empty in-memory page
            // list. Its scope error, if any, is deliberately not retried again.
            return await DownloadAttemptAsync(alternate, confirmAdult, refresh,
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
        int? replacementNumber = replacement is not null ? _source.GetPageNumber(replacement) : null;
        Uri? pendingAddress = recovery?.Checkpoint?.PendingPageUrl;
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
                Uri requested = replacement is not null && (number == replacementNumber ||
                    replacementNumber is null && next == pendingAddress) ? replacement : next;
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

    internal string PartCacheDirectory(Uri first) =>
        StoryCacheLayout.Pages(_cacheDirectory, first);

    internal Uri GetAttemptStart(Uri first, bool refresh, DownloadRecovery? recovery)
    {
        if (refresh || recovery?.Checkpoint?.AttemptStartUrl is not { } saved) return first;
        return _source.GetAlternateStart(first) == saved ? saved : first;
    }

    internal Uri? GetReplacement(Uri first, DownloadRecovery? recovery)
    {
        if (recovery?.ReplacementPageUrl is not { } url) return null;
        try { return _source.ValidatePageRoute(first, url, null); }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException) { return null; }
    }

    public static IReadOnlyList<string> JoinParagraphs(IEnumerable<StoryPage> pages)
    {
        // No separator here: a page can break inside a word or before punctuation.
        var text = string.Concat(pages.Select(p => p.Fragment.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')));
        return Regex.Split(text, @"\n+").Select(StoryText.Clean).Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
    }

    internal static string ValidateChainPage(StoryPage page, int? expectedTotal, HashSet<string> fingerprints)
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

    internal void ValidatePage(StoryPage page, Uri first, Uri requested, int expectedNumber)
    {
        if (page is null || page.Url is null || page.Number != expectedNumber || string.IsNullOrWhiteSpace(page.Title) ||
            string.IsNullOrWhiteSpace(StoryText.Clean(page.Fragment ?? "")))
            throw new InvalidDataException("Недопустимый номер или пустая страница рассказа.");
        var actual = _source.ValidatePageRoute(first, page.Url, expectedNumber);
        var desired = _source.ValidatePageRoute(first, requested, expectedNumber);
        if (_source.GetPageNumber(actual) is null && actual != desired)
            throw new InvalidDataException("Сайт вернул другую страницу произведения.");
        if (page.NextUrl is { } next) _source.ValidatePageRoute(first, next, expectedNumber + 1);
        if (page.TotalPages is { } total && (total < expectedNumber || (page.NextUrl is null && total > expectedNumber) ||
            (page.NextUrl is not null && total == expectedNumber)))
            throw new InvalidDataException("Число страниц не соответствует навигации.");
    }

    internal async Task<StoryPage?> ReadCacheAsync(string directory, int number, Uri first, Uri requested, CancellationToken cancellationToken)
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

    internal static async Task WriteCacheAsync(string directory, StoryPage page, CancellationToken cancellationToken)
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
