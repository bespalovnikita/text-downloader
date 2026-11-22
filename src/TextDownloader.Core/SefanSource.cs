using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace TextDownloader.Core;

/// <summary>Reader for numbered pages of one story in Sefan's stories section.</summary>
public sealed partial class SefanSource : IStorySource
{
    private const int MaxResponseBytes = 4 * 1024 * 1024;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly TimeSpan _requestInterval;
    private readonly PageRequestThrottle _requestThrottle;
    private readonly IProgress<DownloadLogEvent>? _log;
    private static readonly Regex PathPattern = new(
        @"^(?<directory>/stories/(?<language>[a-z]{2})/\d+/(?<genre>[^/]+)/)(?<slug>[^/]+?)_(?<part>\d+)(?:-(?<page>\d+))?\.html$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly HashSet<string> SkippedTags = new(StringComparer.OrdinalIgnoreCase)
        { "script", "style", "noscript", "form", "nav", "iframe", "button" };
    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
        { "p", "div", "section", "blockquote", "li", "h1", "h2", "h3", "h4", "pre" };

    static SefanSource() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public SefanSource(HttpClient? client = null, TimeSpan? requestInterval = null,
        IProgress<DownloadLogEvent>? log = null)
        : this(client, requestInterval ?? TimeSpan.FromSeconds(1), new PageRequestThrottle(), log) { }

    internal SefanSource(HttpClient? client, TimeSpan requestInterval, PageRequestThrottle requestThrottle,
        IProgress<DownloadLogEvent>? log)
    {
        if (requestInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(requestInterval));
        _requestInterval = requestInterval;
        _requestThrottle = requestThrottle;
        _log = log;
        _ownsClient = client is null;
        _client = client ?? new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            AutomaticDecompression = DecompressionMethods.All,
        }) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public bool CanHandle(Uri uri)
    {
        try { Normalize(uri); return true; }
        catch (ArgumentException) { return false; }
    }

    public Uri NormalizeToFirstPage(Uri uri)
    {
        uri = Normalize(uri);
        var match = PathPattern.Match(uri.AbsolutePath);
        return new Uri("https://sefan.ru" + match.Groups["directory"].Value +
            match.Groups["slug"].Value + "_" + match.Groups["part"].Value + ".html");
    }

    public async Task<StoryPage> GetPageAsync(Uri uri, bool confirmAdult, CancellationToken cancellationToken)
    {
        var original = Normalize(uri);
        var (document, actual) = await GetDocumentAsync(original, confirmAdult, cancellationToken, candidate =>
        {
            var normalized = SameStory(candidate, original);
            if (PageNumber(normalized) != PageNumber(original))
                throw new InvalidDataException("Сайт перенаправил на другую страницу рассказа.");
            return normalized;
        }).ConfigureAwait(false);
        return Parse(document, actual);
    }

    private async Task<(HtmlDocument Document, Uri Actual)> GetDocumentAsync(Uri original, bool confirmAdult,
        CancellationToken cancellationToken, Func<Uri, Uri> validate)
    {
        var (html, actual) = await RequestAsync(original, original, null, cancellationToken, validate).ConfigureAwait(false);
        var document = LoadDocument(html);
        if (FindAgeGate(document) is not { } gate) return (document, actual);
        if (!confirmAdult) throw new AgeConfirmationRequiredException();

        var action = validate(new Uri(actual, Attribute(gate, "action", actual.AbsoluteUri)));
        var fields = gate.Descendants("input")
            .Where(n => Attribute(n, "type").Equals("hidden", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(Attribute(n, "name")))
            .GroupBy(n => Attribute(n, "name"), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => Attribute(g.First(), "value"), StringComparer.Ordinal);
        fields["freed"] = "1";
        fields["fkay"] = gate.Descendants("input").FirstOrDefault(n => Attribute(n, "name") == "fkay") is { } submit
            ? Attribute(submit, "value", "Мне исполнилось 18 лет") : "Мне исполнилось 18 лет";
        (html, actual) = await RequestAsync(action, original, fields, cancellationToken, validate).ConfigureAwait(false);
        document = LoadDocument(html);
        if (FindAgeGate(document) is not null) throw new AgeConfirmationRequiredException();
        return (document, actual);
    }

    /// <summary>Parse a numbered page without performing any network operation.</summary>
    public static StoryPage Parse(string html, Uri url)
    {
        var document = LoadDocument(html);
        if (FindAgeGate(document) is not null) throw new AgeConfirmationRequiredException();
        return Parse(document, Normalize(url));
    }

    private static StoryPage Parse(HtmlDocument doc, Uri url)
    {
        var nodes = doc.DocumentNode.Descendants().ToArray();
        var content = doc.GetElementbyId("rsz") ?? throw new InvalidDataException(
            "Не найден блок текста #rsz. Возможно, сайт изменился или вернул служебную страницу.");
        var heading = nodes.FirstOrDefault(n => n.Name == "h1");
        var title = heading is null ? "" : CleanText(Render(heading, false));
        if (string.IsNullOrWhiteSpace(title)) throw new InvalidDataException("Не найдено название рассказа.");
        var authorNode = nodes.FirstOrDefault(n => n.Name == "meta" && Attribute(n, "name").Equals("author", StringComparison.OrdinalIgnoreCase));
        var author = authorNode is null ? null : CleanText(Attribute(authorNode, "content"));

        var excluded = new HashSet<HtmlNode>();
        var pageContents = ReadPageContents(doc, url, excluded);
        var contents = PageNumber(url) == 1 ? pageContents : null;
        var fragment = string.Concat(content.ChildNodes.Select(n => Render(n, true, excluded)));
        if (string.IsNullOrWhiteSpace(CleanText(fragment))) throw new InvalidDataException("Страница содержит пустой текст.");

        var navigation = nodes.Where(node => !excluded.Contains(node) && !node.Ancestors().Any(excluded.Contains)).ToArray();
        var candidates = navigation.Where(n => (n.Name is "a" or "link") && Tokens(n, "rel").Contains("next") && !string.IsNullOrWhiteSpace(Attribute(n, "href")))
            .Select(n => Attribute(n, "href")).ToArray();
        if (candidates.Length == 0)
            candidates = navigation.Where(n => n.Name == "a" && Attribute(n, "title").Equals("Следующая страница", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(Attribute(n, "href"))).Select(n => Attribute(n, "href")).ToArray();
        // Sefan can emit different display styles or an admitted neutral-route
        // alias in its head/body links for the same ordered story page.
        var nexts = candidates.Select(href => SameStory(new Uri(url, href), url)).DistinctBy(PageNumber).ToArray();
        if (nexts.Length > 1) throw new InvalidDataException("Противоречивые ссылки «Далее» на странице рассказа.");
        var next = nexts.FirstOrDefault();
        var current = PageNumber(url);
        if (next is not null && PageNumber(next) != current + 1)
            throw new InvalidDataException("Нарушена последовательность страниц рассказа.");

        var totals = new HashSet<int>();
        foreach (var node in navigation.Where(n => n.Name is "a" or "link"))
        {
            var match = Regex.Match(Attribute(node, "title"), @"Страница\s+\d+\s+из\s+(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var count)) totals.Add(count);
            if (Tokens(node, "rel").Contains("last") && !string.IsNullOrWhiteSpace(Attribute(node, "href")))
                totals.Add(PageNumber(SameStory(new Uri(url, Attribute(node, "href")), url)));
        }
        if (totals.Count > 1) throw new InvalidDataException("Сайт сообщил разное общее количество страниц.");
        int? total = totals.Count == 0 ? null : totals.Single();
        if (total is null && next is null && navigation.Any(n => (n.Name is "span" or "b") && Tokens(n, "class").Contains("but") &&
            Regex.IsMatch(Render(n, false), @"\bДалее\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))) total = current;
        if (total is not null && (total < current || (next is null && total > current) || (next is not null && current >= total)))
            throw new InvalidDataException("Не удалось подтвердить полноту: число страниц не соответствует навигации.");
        return new StoryPage(url, current, title, string.IsNullOrWhiteSpace(author) ? null : author, fragment, next, total) { Contents = contents };
    }

    internal static Uri Normalize(Uri uri)
    {
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https") ||
            uri.Host is not ("sefan.ru" or "www.sefan.ru") || !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.Port is not (80 or 443)) throw new ArgumentException("Нужна HTTP/HTTPS-ссылка чтения на sefan.ru.");
        var match = PathPattern.Match(uri.AbsolutePath);
        if (!match.Success || !int.TryParse(match.Groups["part"].Value, out var part) || part < 1 ||
            (match.Groups["page"].Success && (!int.TryParse(match.Groups["page"].Value, out var page) || page < 1)))
            throw new ArgumentException("Нужна нумерованная страница чтения Sefan: название_1.html или название_1-2.html. Префикс имени не ограничен.");
        return new Uri("https://sefan.ru" + uri.AbsolutePath);
    }

    internal static Uri SameStory(Uri uri, Uri source)
    {
        uri = Normalize(uri);
        source = Normalize(source);
        var actual = PathPattern.Match(uri.AbsolutePath);
        var expected = PathPattern.Match(source.AbsolutePath);
        // Numeric display styles can differ between the document's head/body.
        // The neutral /i/ route is the only admitted cross-genre alias.
        foreach (var group in new[] { "language", "slug", "part" })
            if (!actual.Groups[group].Value.Equals(expected.Groups[group].Value, StringComparison.Ordinal))
                throw new SefanStoryScopeMismatchException(source, uri);
        if (!actual.Groups["genre"].Value.Equals(expected.Groups["genre"].Value, StringComparison.Ordinal) &&
            actual.Groups["genre"].Value != "i" && expected.Groups["genre"].Value != "i")
            throw new SefanStoryScopeMismatchException(source, uri);
        return uri;
    }

    internal static Uri WithNeutralGenre(Uri uri)
    {
        uri = Normalize(uri);
        var genre = PathPattern.Match(uri.AbsolutePath).Groups["genre"];
        string path = uri.AbsolutePath[..genre.Index] + "i" + uri.AbsolutePath[(genre.Index + genre.Length)..];
        return new Uri("https://sefan.ru" + path);
    }

    internal static bool IsNeutralGenre(Uri uri) =>
        PathPattern.Match(Normalize(uri).AbsolutePath).Groups["genre"].Value == "i";

    internal static int PageNumber(Uri uri)
    {
        var match = PathPattern.Match(Normalize(uri).AbsolutePath);
        return match.Groups["page"].Success ? int.Parse(match.Groups["page"].Value, CultureInfo.InvariantCulture) : 1;
    }

    internal static string CleanText(string text) => Regex.Replace(
        Regex.Replace(text, @"[\x00-\x08\x0B\x0C\x0E-\x1F]", ""), @"[^\S\n]+", " ").Trim();

    private static HtmlDocument LoadDocument(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        return doc;
    }

    private static HtmlNode? FindAgeGate(HtmlDocument doc) => doc.DocumentNode.Descendants("form")
        .FirstOrDefault(form => form.Descendants("input").Any(n => Attribute(n, "name") == "freed"));
    private static string Attribute(HtmlNode node, string name, string fallback = "") => HtmlEntity.DeEntitize(node.GetAttributeValue(name, fallback));
    private static HashSet<string> Tokens(HtmlNode node, string name) => Attribute(node, name).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string Render(HtmlNode node, bool story, HashSet<HtmlNode>? excluded = null)
    {
        if (excluded?.Contains(node) == true) return "";
        if (node.NodeType == HtmlNodeType.Text)
            return Regex.Replace(HtmlEntity.DeEntitize(((HtmlTextNode)node).Text), @"[\t\r\n\f]+", " ");
        if (node.NodeType is HtmlNodeType.Comment || SkippedTags.Contains(node.Name) ||
            (story && Attribute(node, "id").StartsWith("sfnx", StringComparison.Ordinal))) return "";
        if (story && node.Name == "a" && (Tokens(node, "rel").Overlaps(new[] { "next", "prev" }) ||
            Attribute(node, "title").Contains("следующая страница", StringComparison.OrdinalIgnoreCase) ||
            Attribute(node, "title").Contains("предыдущая страница", StringComparison.OrdinalIgnoreCase))) return "";
        if (story && node.Name is "br" or "hr") return "\n";
        var inside = string.Concat(node.ChildNodes.Select(n => Render(n, story, excluded)));
        return story && BlockTags.Contains(node.Name) ? "\n" + inside + "\n" : inside;
    }

    private async Task<(string Html, Uri Actual)> RequestAsync(Uri uri, Uri original,
        IReadOnlyDictionary<string, string>? form, CancellationToken cancellationToken, Func<Uri, Uri> validate)
    {
        for (var attempt = 0; attempt <= 2; attempt++)
        {
            try
            {
                var current = validate(uri);
                var usePost = form is not null;
                for (var redirect = 0; redirect <= 5; redirect++)
                {
                    await _requestThrottle.EnterAsync(_requestInterval, cancellationToken).ConfigureAwait(false);
                    try
                    {
                        using var request = new HttpRequestMessage(usePost ? HttpMethod.Post : HttpMethod.Get, current);
                        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (compatible; TextDownloader/0.1)");
                        request.Headers.Referrer = original;
                        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
                        if (usePost) request.Content = new FormUrlEncodedContent(form!);
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        timeout.CancelAfter(TimeSpan.FromSeconds(30));
                        _log?.Report(new(DownloadLogLevel.Detail, $"{request.Method} {current.AbsoluteUri}"));
                        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                        _log?.Report(new(DownloadLogLevel.Detail, $"HTTP {(int)response.StatusCode} · {current.AbsoluteUri}"));
                        var actual = validate(response.RequestMessage?.RequestUri ?? current);
                        if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                        {
                            if (redirect == 5 || response.Headers.Location is null) throw new InvalidDataException("Слишком много перенаправлений сайта.");
                            current = validate(new Uri(actual, response.Headers.Location));
                            if ((int)response.StatusCode is 301 or 302 or 303) usePost = false;
                            continue;
                        }
                        if ((int)response.StatusCode is 429 or 500 or 502 or 503 or 504)
                        {
                            var wait = response.Headers.RetryAfter?.Delta ??
                                (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                            throw new TransientResponseException((int)response.StatusCode, wait);
                        }
                        if (!response.IsSuccessStatusCode) throw new InvalidDataException($"Сайт вернул HTTP {(int)response.StatusCode}.");
                        if (response.Content.Headers.ContentLength > MaxResponseBytes) throw new InvalidDataException("Страница неожиданно велика.");
                        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                        using var buffer = new MemoryStream();
                        var bytes = new byte[81920];
                        while (true)
                        {
                            var read = await stream.ReadAsync(bytes, timeout.Token).ConfigureAwait(false);
                            if (read == 0) break;
                            if (buffer.Length + read > MaxResponseBytes) throw new InvalidDataException("Страница неожиданно велика.");
                            buffer.Write(bytes, 0, read);
                        }
                        if (buffer.Length == 0) throw new HttpRequestException("Сайт вернул пустую страницу.");
                        var html = Decode(buffer.ToArray(), response.Content.Headers.ContentType?.CharSet);
                        if (string.IsNullOrWhiteSpace(html)) throw new HttpRequestException("Сайт вернул пустую страницу.");
                        return (html, actual);
                    }
                    finally { _requestThrottle.Exit(); }
                }
                throw new InvalidDataException("Не удалось завершить перенаправления сайта.");
            }
            catch (TransientResponseException error)
            {
                if (attempt == 2 || error.Delay > TimeSpan.FromSeconds(60))
                    throw new HttpRequestException($"Сайт вернул HTTP {error.Status}. Повторите загрузку позднее.");
                var delay = error.Delay < TimeSpan.Zero ? TimeSpan.Zero : error.Delay;
                _log?.Report(new(DownloadLogLevel.Warning, $"HTTP {error.Status}. Повтор запроса через {delay.TotalSeconds:0.###} с."));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is HttpRequestException || (error is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                if (attempt == 2) throw new HttpRequestException("Не удалось загрузить страницу после повторных попыток.", error);
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                string reason = error is OperationCanceledException ? "Истёк тайм-аут запроса." : error.Message;
                _log?.Report(new(DownloadLogLevel.Warning, $"{reason} Повтор запроса через {delay.TotalSeconds:0.###} с."));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
        throw new HttpRequestException("Не удалось загрузить страницу.");
    }

    private static string Decode(byte[] bytes, string? declared)
    {
        var meta = Regex.Match(Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 4096)), @"charset\s*=\s*[""']?([A-Za-z0-9_-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (var charset in new[] { declared?.Trim('"', '\''), meta.Success ? meta.Groups[1].Value : null, "utf-8", "windows-1251" })
        {
            if (string.IsNullOrWhiteSpace(charset)) continue;
            try { return Encoding.GetEncoding(charset, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(bytes); }
            catch (Exception error) when (error is ArgumentException or DecoderFallbackException) { }
        }
        throw new InvalidDataException("Не удалось определить кодировку страницы.");
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
