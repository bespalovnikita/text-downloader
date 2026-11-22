using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace TextDownloader.Core;

public sealed record BestweaponPart(int Number, string Title, Uri Url);
public sealed record BestweaponPost(Uri Url, string Title, string? Author, string SeriesTitle, int PartNumber,
    IReadOnlyList<string> Paragraphs, IReadOnlyList<BestweaponPart> Parts);

/// <summary>Reads only explicit Bestweapon posts and their authoritative ordered story list.</summary>
public sealed class BestweaponSource : IBookStorySource
{
    private const int MaxResponseBytes = 4 * 1024 * 1024;
    private const int MaxCacheBytes = 32 * 1024 * 1024;
    private const int MaxParts = 10000;
    private const long MaxStoryCharacters = 50_000_000;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly TimeSpan _requestInterval;
    private readonly PageRequestThrottle _requestThrottle;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Regex PostPath = new(@"^/post_(?<id>[1-9][0-9]*)/?$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex NumberedTitle = new(
        @"^(?<series>.+?)\s*(?:[.\-–—,:]\s*)?(?:\(\s*)?(?:часть|глава)\s*(?:№\s*)?(?<part>[1-9][0-9]*)\s*\.?\s*\)?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly HashSet<string> SkipTags = new(StringComparer.OrdinalIgnoreCase)
        { "script", "style", "noscript", "form", "nav", "aside", "iframe", "button" };
    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
        { "p", "div", "section", "blockquote", "li", "h1", "h2", "h3", "h4", "pre" };

    static BestweaponSource() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public BestweaponSource(HttpClient? client = null, TimeSpan? requestInterval = null)
        : this(client, requestInterval ?? TimeSpan.FromSeconds(1), new PageRequestThrottle()) { }

    internal BestweaponSource(HttpClient? client, TimeSpan requestInterval, PageRequestThrottle requestThrottle)
    {
        if (requestInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(requestInterval));
        _requestInterval = requestInterval;
        _requestThrottle = requestThrottle;
        _ownsClient = client is null;
        _client = client ?? new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
        }) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public bool CanHandle(Uri uri)
    {
        try { Normalize(uri); return true; }
        catch (ArgumentException) { return false; }
    }

    // Finding earlier parts requires the actual post's authoritative TOC and is asynchronous.
    public Uri NormalizeToFirstPage(Uri uri) => Normalize(uri);

    public async Task<StoryPage> GetPageAsync(Uri uri, bool confirmAdult, CancellationToken cancellationToken)
    {
        // This source never submits login or age forms, regardless of the Sefan-specific option.
        uri = Normalize(uri);
        var post = ParsePost(await ReadHtmlAsync(uri, cancellationToken).ConfigureAwait(false), uri);
        return new StoryPage(post.Url, post.PartNumber, post.Title, post.Author,
            string.Join("\n\n", post.Paragraphs), null, post.Parts.Count > 0 ? post.Parts.Count : null);
    }

    public async Task<BookDocument> DownloadBookAsync(Uri url, bool confirmAdult, bool refresh, string cacheDirectory,
        IProgress<DownloadProgress>? progress, CancellationToken cancellationToken, DownloadRecovery? recovery = null)
    {
        var requested = Normalize(url);
        var cache = Path.Combine(Path.GetFullPath(cacheDirectory), "bestweapon", Hash(Encoding.UTF8.GetBytes(requested.AbsoluteUri)).ToLowerInvariant());
        progress?.Report(new DownloadProgress(0, null, "Читаем оглавление выбранной части Bestweapon…")
        {
            Checkpoint = new DownloadCheckpoint(requested, null, requested)
        });
        var selected = await GetPostAsync(requested, cache, refresh, ValidateSelected, cancellationToken).ConfigureAwait(false);
        var plan = selected.Parts.Take(selected.PartNumber).ToArray();
        progress?.Report(new DownloadProgress(0, plan.Length, $"Найдено частей до выбранной включительно: {plan.Length}."));
        var chapters = new List<BookChapter>(plan.Length);
        long characters = 0;
        Uri lastSuccessfulUrl = selected.Url;
        foreach (var part in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new DownloadProgress(chapters.Count, plan.Length, $"Проверяем часть {part.Number} из {plan.Length}…")
            {
                Checkpoint = new DownloadCheckpoint(requested, lastSuccessfulUrl, part.Url)
            });
            var post = part.Url == selected.Url ? selected : await GetPostAsync(part.Url, cache, refresh,
                candidate => ValidateAgainstPlan(candidate, selected, part, plan), cancellationToken).ConfigureAwait(false);
            characters += post.Paragraphs.Sum(p => (long)p.Length);
            if (characters > MaxStoryCharacters) throw new InvalidDataException("Текст превышает допустимый объём одной книги.");
            chapters.Add(new BookChapter(part.Title, post.Url, post.Paragraphs.ToArray()));
            if (part.Url != selected.Url) lastSuccessfulUrl = post.Url;
            Uri? next = chapters.Count < plan.Length ? plan[chapters.Count].Url : null;
            progress?.Report(new DownloadProgress(chapters.Count, plan.Length, $"Часть {part.Number} из {plan.Length} проверена: {part.Title}")
            {
                Checkpoint = new DownloadCheckpoint(requested, lastSuccessfulUrl, next)
            });
        }
        cancellationToken.ThrowIfCancellationRequested();
        var paragraphs = chapters.SelectMany(chapter => chapter.Paragraphs).ToArray();
        if (paragraphs.Length == 0) throw new InvalidDataException("В частях рассказа не найден текст.");
        return new BookDocument(selected.SeriesTitle, selected.Author, requested, paragraphs, chapters.Count)
            { Chapters = chapters.AsReadOnly() };
    }

    /// <summary>Parses a single post; no linked post is requested by this operation.</summary>
    public static BestweaponPost ParsePost(string html, Uri url)
    {
        url = Normalize(url);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var content = doc.GetElementbyId("postmaintext") ?? throw new InvalidDataException(
            "Не найден текст Bestweapon #postmaintext. Возможно, публикация недоступна или сайт вернул служебную страницу.");
        var titleNode = doc.GetElementbyId("main_post_title");
        string title = titleNode is null ? "" : CleanText(Render(titleNode, false));
        if (title.Length == 0)
        {
            title = Meta(doc, "property", "og:title");
            if (title.Length == 0) title = CleanText(doc.DocumentNode.Descendants("title").FirstOrDefault()?.InnerText ?? "");
            title = Regex.Replace(title, @"\s+[—–-]\s+(?:порно\s+рассказ|эротический\s+рассказ|Bestweapon(?:\.cc)?)\s*$", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
        }
        if (title.Length == 0) throw new InvalidDataException("Не найдено название публикации Bestweapon.");
        string author = Meta(doc, "name", "author");
        if (author.Length == 0)
        {
            var authorLink = doc.DocumentNode.Descendants("p").Where(n => Tokens(n, "class").Contains("post_add"))
                .SelectMany(n => n.Descendants("a")).FirstOrDefault(n =>
                {
                    if (!Uri.TryCreate(url, Attribute(n, "href"), out var link)) return false;
                    return link.Host is "bestweapon.cc" or "www.bestweapon.cc" && link.AbsolutePath == "/author.php";
                });
            if (authorLink is not null) author = CleanText(Render(authorLink, false));
        }
        var paragraphs = Regex.Split(string.Concat(content.ChildNodes.Select(n => Render(n, true))), @"\n+")
            .Select(CleanText).Where(text => text.Length > 0).ToArray();
        if (paragraphs.Length == 0) throw new InvalidDataException("Публикация Bestweapon содержит пустой текст.");
        bool numbered = TryNumberedTitle(title, out var series, out var titlePart);
        var list = doc.GetElementbyId("storylist");
        IReadOnlyList<BestweaponPart> parts;
        int partNumber;
        if (list is null)
        {
            partNumber = numbered ? titlePart : 1;
            // A numbered later part remains previewable, but cannot become a complete book.
            parts = partNumber == 1 ? new[] { new BestweaponPart(1, title, url) } : Array.Empty<BestweaponPart>();
        }
        else
        {
            if (list.Name != "ol" || Attribute(list, "start", "1") != "1")
                throw new InvalidDataException("Не удалось подтвердить начало оглавления Bestweapon с первой части.");
            var items = list.ChildNodes.Where(n => n.Name == "li").ToArray();
            if (items.Length == 0 || items.Length > MaxParts) throw new InvalidDataException("Пустое или слишком большое оглавление Bestweapon.");
            if (items.Length > 1 && !numbered) throw new InvalidDataException("Не удалось проверить номер текущей части Bestweapon.");
            var entries = new List<BestweaponPart>(items.Length);
            var urls = new HashSet<string>(StringComparer.Ordinal);
            int currentCount = 0;
            partNumber = 0;
            for (int index = 0; index < items.Length; index++)
            {
                var item = items[index];
                int number = index + 1;
                if (Attribute(item, "value", number.ToString(CultureInfo.InvariantCulture)) != number.ToString(CultureInfo.InvariantCulture))
                    throw new InvalidDataException("Оглавление Bestweapon пропускает номера частей.");
                string label = CleanText(Render(item, false));
                if (label.Length == 0) throw new InvalidDataException("В оглавлении отсутствует название части.");
                if (items.Length > 1 || numbered)
                {
                    if (!TryNumberedTitle(label, out var entrySeries, out var entryNumber) || entryNumber != number || !SameText(entrySeries, series))
                        throw new InvalidDataException("В оглавлении Bestweapon смешаны серии или нарушены номера частей.");
                }
                var anchors = item.Descendants("a").Where(n => Attribute(n, "href").Length > 0).ToArray();
                if (anchors.Length > 1) throw new InvalidDataException("Неоднозначная ссылка на часть в оглавлении Bestweapon.");
                Uri entryUrl;
                try { entryUrl = anchors.Length == 0 ? url : Normalize(new Uri(url, Attribute(anchors[0], "href"))); }
                catch (ArgumentException error) { throw new InvalidDataException("Оглавление Bestweapon содержит посторонний адрес.", error); }
                if (!urls.Add(entryUrl.AbsoluteUri)) throw new InvalidDataException("Оглавление Bestweapon повторяет публикацию.");
                if (entryUrl == url)
                {
                    currentCount++;
                    partNumber = number;
                    if (!SameText(label, title)) throw new InvalidDataException("Название текущей части не соответствует оглавлению Bestweapon.");
                }
                entries.Add(new BestweaponPart(number, label, entryUrl));
            }
            if (currentCount != 1 || (numbered && titlePart != partNumber))
                throw new InvalidDataException("Не удалось однозначно найти текущую часть в оглавлении Bestweapon.");
            parts = entries.AsReadOnly();
        }
        var post = new BestweaponPost(url, title, author.Length == 0 ? null : author, series, partNumber, paragraphs, parts);
        ValidatePost(post);
        return post;
    }

    internal static Uri Normalize(Uri uri)
    {
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https") ||
            uri.Host is not ("bestweapon.cc" or "www.bestweapon.cc") || !string.IsNullOrEmpty(uri.UserInfo) || uri.Port is not (80 or 443))
            throw new ArgumentException("Нужна HTTP/HTTPS-ссылка bestweapon.cc/post_номер.");
        var match = PostPath.Match(uri.AbsolutePath);
        if (!match.Success || !long.TryParse(match.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            throw new ArgumentException("Нужна ссылка на публикацию Bestweapon вида https://bestweapon.cc/post_124145.");
        return new Uri("https://bestweapon.cc/post_" + match.Groups["id"].Value);
    }

    private static bool TryNumberedTitle(string title, out string series, out int number)
    {
        var match = NumberedTitle.Match(title);
        if (!match.Success) { series = title; number = 1; return false; }
        if (!int.TryParse(match.Groups["part"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out number) || number > MaxParts)
            throw new InvalidDataException("Недопустимый номер части Bestweapon.");
        series = CleanText(match.Groups["series"].Value).TrimEnd('.', '-', '–', '—', ',', ':', ' ');
        if (series.Length == 0) throw new InvalidDataException("Не найдено название серии Bestweapon.");
        return true;
    }

    private static void ValidatePost(BestweaponPost post)
    {
        if (post is null || post.Url is null || string.IsNullOrWhiteSpace(post.Title) || string.IsNullOrWhiteSpace(post.SeriesTitle) ||
            post.PartNumber is < 1 or > MaxParts || post.Paragraphs is null || post.Paragraphs.Count == 0 ||
            post.Paragraphs.Any(p => string.IsNullOrWhiteSpace(p)) || post.Parts is null || post.Parts.Count > MaxParts)
            throw new InvalidDataException("Недопустимые данные публикации Bestweapon.");
        if (Normalize(post.Url) != post.Url) throw new InvalidDataException("Неканонический адрес в данных Bestweapon.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < post.Parts.Count; index++)
        {
            var part = post.Parts[index];
            if (part is null || part.Number != index + 1 || string.IsNullOrWhiteSpace(part.Title) || part.Url is null ||
                Normalize(part.Url) != part.Url || !seen.Add(part.Url.AbsoluteUri))
                throw new InvalidDataException("Повреждённое оглавление Bestweapon.");
            if (post.Parts.Count > 1 && (!TryNumberedTitle(part.Title, out var series, out var number) ||
                number != part.Number || !SameText(series, post.SeriesTitle)))
                throw new InvalidDataException("Нарушены название серии или номера частей Bestweapon.");
        }
        if (post.Parts.Count > 0 && (post.PartNumber > post.Parts.Count ||
            post.Parts[post.PartNumber - 1].Url != post.Url || !SameText(post.Parts[post.PartNumber - 1].Title, post.Title)))
            throw new InvalidDataException("Текущая публикация не соответствует своему оглавлению Bestweapon.");
    }

    private static void ValidateAgainstPlan(BestweaponPost post, BestweaponPost selected, BestweaponPart expected,
        IReadOnlyList<BestweaponPart> plan)
    {
        ValidatePost(post);
        if (post.Url != expected.Url || post.PartNumber != expected.Number || !SameText(post.Title, expected.Title) ||
            !SameText(post.SeriesTitle, selected.SeriesTitle) ||
            !string.Equals(post.Author?.Trim(), selected.Author?.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Часть Bestweapon принадлежит другой серии, автору или позиции в оглавлении.");
        if (post.Parts.Count < plan.Count) throw new InvalidDataException("Оглавления частей Bestweapon неполны или противоречат друг другу.");
        for (int index = 0; index < plan.Count; index++)
            if (post.Parts[index].Number != plan[index].Number || post.Parts[index].Url != plan[index].Url ||
                !SameText(post.Parts[index].Title, plan[index].Title))
                throw new InvalidDataException("Оглавления частей Bestweapon противоречат друг другу.");
    }

    private static void ValidateSelected(BestweaponPost selected)
    {
        if (selected.Parts.Count == 0 || selected.PartNumber > selected.Parts.Count)
            throw new InvalidDataException("Не найдено полное оглавление Bestweapon: невозможно подтвердить все предыдущие части.");
        if (selected.PartNumber > MaxParts)
            throw new InvalidDataException("Недопустимое количество частей рассказа.");
        if (selected.PartNumber > 1 && string.IsNullOrWhiteSpace(selected.Author))
            throw new InvalidDataException("Не указан автор серии; нельзя подтвердить принадлежность всех частей.");
        ValidateAgainstPlan(selected, selected, selected.Parts[selected.PartNumber - 1], selected.Parts);
    }

    private async Task<BestweaponPost> GetPostAsync(Uri url, string cache, bool refresh, Action<BestweaponPost> validate, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string path = Path.Combine(cache, "post_" + PostPath.Match(url.AbsolutePath).Groups["id"].Value + ".json");
        if (!refresh)
        {
            var cached = await ReadCacheAsync(path, url, token).ConfigureAwait(false);
            if (cached is not null)
            {
                try
                {
                    validate(cached);
                    return cached;
                }
                catch (InvalidDataException) { }
            }
        }
        var post = ParsePost(await ReadHtmlAsync(url, token).ConfigureAwait(false), url);
        validate(post);
        await WriteCacheAsync(path, post, token).ConfigureAwait(false);
        return post;
    }

    private sealed record CacheRecord(int Schema, string Sha256, BestweaponPost Post);
    private static async Task<BestweaponPost?> ReadCacheAsync(string path, Uri expected, CancellationToken token)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxCacheBytes) return null;
            byte[] bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
            if (bytes.Length > MaxCacheBytes) return null;
            var record = JsonSerializer.Deserialize<CacheRecord>(bytes, Json);
            if (record?.Schema != 1 || record.Post is null || string.IsNullOrEmpty(record.Sha256) ||
                Hash(JsonSerializer.SerializeToUtf8Bytes(record.Post, Json)) != record.Sha256) return null;
            ValidatePost(record.Post);
            return record.Post.Url == expected ? record.Post : null;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        { return null; }
    }

    private static async Task WriteCacheAsync(string path, BestweaponPost post, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var record = new CacheRecord(1, Hash(JsonSerializer.SerializeToUtf8Bytes(post, Json)), post);
        try
        {
            await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(record, Json), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static bool SameText(string left, string right) => CleanText(left).Normalize().Equals(CleanText(right).Normalize(), StringComparison.Ordinal);
    private static string CleanText(string value) => Regex.Replace(Regex.Replace(value,
        @"[\x00-\x08\x0B\x0C\x0E-\x1F]", ""), @"[^\S\n]+", " ").Trim();
    private static string Attribute(HtmlNode node, string name, string fallback = "") => HtmlEntity.DeEntitize(node.GetAttributeValue(name, fallback));
    private static HashSet<string> Tokens(HtmlNode node, string name) => Attribute(node, name)
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
    private static string Meta(HtmlDocument doc, string key, string value) => CleanText(doc.DocumentNode.Descendants("meta")
        .Where(n => Attribute(n, key).Equals(value, StringComparison.OrdinalIgnoreCase)).Select(n => Attribute(n, "content")).FirstOrDefault() ?? "");

    private static string Render(HtmlNode node, bool story, int depth = 0)
    {
        if (depth > 256) throw new InvalidDataException("Слишком глубоко вложенная разметка Bestweapon.");
        if (node.NodeType == HtmlNodeType.Text)
            return Regex.Replace(HtmlEntity.DeEntitize(((HtmlTextNode)node).Text), @"[\t\r\n\f]+", " ");
        if (node.NodeType == HtmlNodeType.Comment || SkipTags.Contains(node.Name) ||
            (story && (Attribute(node, "id") == "storylist" || Tokens(node, "class").Overlaps(new[] { "border-blog", "post_tags", "post_view2" })))) return "";
        if (story && node.Name is "br" or "hr") return "\n";
        string inside = string.Concat(node.ChildNodes.Select(n => Render(n, story, depth + 1)));
        return story && BlockTags.Contains(node.Name) ? "\n" + inside + "\n" : inside;
    }

    private async Task<string> ReadHtmlAsync(Uri requested, CancellationToken token)
    {
        requested = Normalize(requested);
        for (int attempt = 0; attempt <= 2; attempt++)
        {
            try
            {
                var current = requested;
                for (int redirect = 0; redirect <= 5; redirect++)
                {
                    await _requestThrottle.EnterAsync(_requestInterval, token).ConfigureAwait(false);
                    try
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Get, current);
                        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (compatible; TextDownloader/0.1)");
                        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                        timeout.CancelAfter(TimeSpan.FromSeconds(30));
                        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                        var actual = Normalize(response.RequestMessage?.RequestUri ?? current);
                        if (actual != requested) throw new InvalidDataException("Bestweapon перенаправил на другую публикацию.");
                        if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                        {
                            if (redirect == 5 || response.Headers.Location is null) throw new InvalidDataException("Слишком много перенаправлений Bestweapon.");
                            current = Normalize(new Uri(actual, response.Headers.Location));
                            if (current != requested) throw new InvalidDataException("Bestweapon перенаправил на другую публикацию.");
                            continue;
                        }
                        if ((int)response.StatusCode is 429 or 500 or 502 or 503 or 504)
                        {
                            var wait = response.Headers.RetryAfter?.Delta ??
                                (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                            throw new TransientResponseException((int)response.StatusCode, wait);
                        }
                        if (!response.IsSuccessStatusCode) throw new InvalidDataException($"Bestweapon вернул HTTP {(int)response.StatusCode}.");
                        if (response.Content.Headers.ContentLength > MaxResponseBytes) throw new InvalidDataException("Публикация Bestweapon слишком велика.");
                        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                        using var output = new MemoryStream();
                        byte[] buffer = new byte[81920];
                        while (true)
                        {
                            int count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                            if (count == 0) break;
                            if (output.Length + count > MaxResponseBytes) throw new InvalidDataException("Публикация Bestweapon слишком велика.");
                            output.Write(buffer, 0, count);
                        }
                        if (output.Length == 0) throw new HttpRequestException("Bestweapon вернул пустую страницу.");
                        string html = Decode(output.ToArray(), response.Content.Headers.ContentType?.CharSet);
                        if (string.IsNullOrWhiteSpace(html)) throw new HttpRequestException("Bestweapon вернул пустую страницу.");
                        return html;
                    }
                    finally { _requestThrottle.Exit(); }
                }
                throw new InvalidDataException("Не удалось завершить перенаправления Bestweapon.");
            }
            catch (TransientResponseException error)
            {
                if (attempt == 2 || error.Delay > TimeSpan.FromSeconds(60))
                    throw new HttpRequestException($"Bestweapon вернул HTTP {error.Status}. Повторите загрузку позднее.");
                await Task.Delay(error.Delay < TimeSpan.Zero ? TimeSpan.Zero : error.Delay, token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is HttpRequestException || (error is OperationCanceledException && !token.IsCancellationRequested))
            {
                if (attempt == 2) throw new HttpRequestException("Не удалось загрузить публикацию Bestweapon после повторных попыток.", error);
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)), token).ConfigureAwait(false);
            }
        }
        throw new HttpRequestException("Не удалось загрузить публикацию Bestweapon.");
    }

    private static string Decode(byte[] bytes, string? declared)
    {
        var meta = Regex.Match(Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 4096)), @"charset\s*=\s*[""']?([A-Za-z0-9_-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (var charset in new[] { declared?.Trim('"', '\''), meta.Success ? meta.Groups[1].Value : null, "utf-8", "windows-1251" })
        {
            if (string.IsNullOrWhiteSpace(charset)) continue;
            try { return Encoding.GetEncoding(charset, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(bytes); }
            catch (ArgumentException) { }
        }
        throw new InvalidDataException("Не удалось определить кодировку Bestweapon.");
    }

    private sealed class TransientResponseException(int status, TimeSpan delay) : Exception
    {
        public int Status { get; } = status;
        public TimeSpan Delay { get; } = delay;
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }
}
