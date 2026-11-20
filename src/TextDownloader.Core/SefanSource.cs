using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace TextDownloader.Core;

/// <summary>Reader for numbered pages of one story in Sefan's stories section.</summary>
public sealed class SefanSource : IStorySource
{
    private const int MaxResponseBytes = 4 * 1024 * 1024;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private long? _lastRequest;
    private static readonly Regex PathPattern = new(
        @"^(?<directory>/stories/(?<language>[a-z]{2})/\d+/[^/]+/)(?<slug>[^/]+?)_(?<part>\d+)(?:-(?<page>\d+))?\.html$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly HashSet<string> SkippedTags = new(StringComparer.OrdinalIgnoreCase)
        { "script", "style", "noscript", "form", "nav", "iframe", "button" };
    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
        { "p", "div", "section", "blockquote", "li", "h1", "h2", "h3", "h4", "pre" };

    static SefanSource() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public SefanSource(HttpClient? client = null)
    {
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
        var (html, actual) = await RequestAsync(original, original, null, cancellationToken).ConfigureAwait(false);
        try { return Parse(html, actual); }
        catch (AgeConfirmationRequiredException) when (confirmAdult)
        {
            var document = LoadDocument(html);
            var gate = FindAgeGate(document) ?? throw new InvalidDataException("Не найдена форма подтверждения возраста.");
            var action = SameStory(new Uri(actual, Attribute(gate, "action", actual.AbsoluteUri)), original);
            if (PageNumber(action) != PageNumber(original))
                throw new InvalidDataException("Форма подтверждения ведёт на другую страницу рассказа.");
            var fields = gate.Descendants("input")
                .Where(n => Attribute(n, "type").Equals("hidden", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(Attribute(n, "name")))
                .GroupBy(n => Attribute(n, "name"), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => Attribute(g.First(), "value"), StringComparer.Ordinal);
            fields["freed"] = "1";
            fields["fkay"] = gate.Descendants("input").FirstOrDefault(n => Attribute(n, "name") == "fkay") is { } submit
                ? Attribute(submit, "value", "Мне исполнилось 18 лет") : "Мне исполнилось 18 лет";
            (html, actual) = await RequestAsync(action, original, fields, cancellationToken).ConfigureAwait(false);
            return Parse(html, actual);
        }
    }

    /// <summary>Parse a numbered page without performing any network operation.</summary>
    public static StoryPage Parse(string html, Uri url)
    {
        url = Normalize(url);
        var doc = LoadDocument(html);
        if (FindAgeGate(doc) is not null) throw new AgeConfirmationRequiredException();
        var nodes = doc.DocumentNode.Descendants().ToArray();
        var content = doc.GetElementbyId("rsz") ?? throw new InvalidDataException(
            "Не найден блок текста #rsz. Возможно, сайт изменился или вернул служебную страницу.");
        var fragment = string.Concat(content.ChildNodes.Select(n => Render(n, true)));
        if (string.IsNullOrWhiteSpace(CleanText(fragment))) throw new InvalidDataException("Страница содержит пустой текст.");
        var heading = nodes.FirstOrDefault(n => n.Name == "h1");
        var title = heading is null ? "" : CleanText(Render(heading, false));
        if (string.IsNullOrWhiteSpace(title)) throw new InvalidDataException("Не найдено название рассказа.");
        var authorNode = nodes.FirstOrDefault(n => n.Name == "meta" && Attribute(n, "name").Equals("author", StringComparison.OrdinalIgnoreCase));
        var author = authorNode is null ? null : CleanText(Attribute(authorNode, "content"));

        var candidates = nodes.Where(n => (n.Name is "a" or "link") && Tokens(n, "rel").Contains("next") && !string.IsNullOrWhiteSpace(Attribute(n, "href")))
            .Select(n => Attribute(n, "href")).ToArray();
        if (candidates.Length == 0)
            candidates = nodes.Where(n => n.Name == "a" && Attribute(n, "title").Equals("Следующая страница", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(Attribute(n, "href"))).Select(n => Attribute(n, "href")).ToArray();
        var nexts = candidates.Select(href => SameStory(new Uri(url, href), url)).DistinctBy(u => u.AbsoluteUri, StringComparer.Ordinal).ToArray();
        if (nexts.Length > 1) throw new InvalidDataException("Противоречивые ссылки «Далее» на странице рассказа.");
        var next = nexts.FirstOrDefault();
        var current = PageNumber(url);
        if (next is not null && PageNumber(next) != current + 1)
            throw new InvalidDataException("Нарушена последовательность страниц рассказа.");

        var totals = new HashSet<int>();
        foreach (var node in nodes.Where(n => n.Name is "a" or "link"))
        {
            var match = Regex.Match(Attribute(node, "title"), @"Страница\s+\d+\s+из\s+(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var count)) totals.Add(count);
            if (Tokens(node, "rel").Contains("last") && !string.IsNullOrWhiteSpace(Attribute(node, "href")))
                totals.Add(PageNumber(SameStory(new Uri(url, Attribute(node, "href")), url)));
        }
        if (totals.Count > 1) throw new InvalidDataException("Сайт сообщил разное общее количество страниц.");
        int? total = totals.Count == 0 ? null : totals.Single();
        if (total is null && next is null && nodes.Any(n => (n.Name is "span" or "b") && Tokens(n, "class").Contains("but") &&
            Regex.IsMatch(Render(n, false), @"\bДалее\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))) total = current;
        if (total is not null && (total < current || (next is null && total > current) || (next is not null && current >= total)))
            throw new InvalidDataException("Не удалось подтвердить полноту: число страниц не соответствует навигации.");
        return new StoryPage(url, current, title, string.IsNullOrWhiteSpace(author) ? null : author, fragment, next, total);
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
        foreach (var group in new[] { "directory", "slug", "part" })
            if (!actual.Groups[group].Value.Equals(expected.Groups[group].Value, StringComparison.Ordinal))
                throw new InvalidDataException("Ссылка ведёт на другой рассказ; загрузка остановлена.");
        return uri;
    }

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

    private static string Render(HtmlNode node, bool story)
    {
        if (node.NodeType == HtmlNodeType.Text)
            return Regex.Replace(HtmlEntity.DeEntitize(((HtmlTextNode)node).Text), @"[\t\r\n\f]+", " ");
        if (node.NodeType is HtmlNodeType.Comment || SkippedTags.Contains(node.Name) ||
            (story && Attribute(node, "id").StartsWith("sfnx", StringComparison.Ordinal))) return "";
        if (story && node.Name == "a" && (Tokens(node, "rel").Overlaps(new[] { "next", "prev" }) ||
            Attribute(node, "title").Contains("следующая страница", StringComparison.OrdinalIgnoreCase) ||
            Attribute(node, "title").Contains("предыдущая страница", StringComparison.OrdinalIgnoreCase))) return "";
        if (story && node.Name is "br" or "hr") return "\n";
        var inside = string.Concat(node.ChildNodes.Select(n => Render(n, story)));
        return story && BlockTags.Contains(node.Name) ? "\n" + inside + "\n" : inside;
    }

    private async Task<(string Html, Uri Actual)> RequestAsync(Uri uri, Uri original,
        IReadOnlyDictionary<string, string>? form, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt <= 2; attempt++)
        {
            try
            {
                var current = SameStory(uri, original);
                var usePost = form is not null;
                for (var redirect = 0; redirect <= 5; redirect++)
                {
                    await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        if (_lastRequest is { } last)
                        {
                            var delay = TimeSpan.FromSeconds(1) - Stopwatch.GetElapsedTime(last);
                            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                        }
                        _lastRequest = Stopwatch.GetTimestamp();
                        using var request = new HttpRequestMessage(usePost ? HttpMethod.Post : HttpMethod.Get, current);
                        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (compatible; TextDownloader/0.1)");
                        request.Headers.Referrer = original;
                        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
                        if (usePost) request.Content = new FormUrlEncodedContent(form!);
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        timeout.CancelAfter(TimeSpan.FromSeconds(30));
                        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                        var actual = SameStory(response.RequestMessage?.RequestUri ?? current, original);
                        if (PageNumber(actual) != PageNumber(original)) throw new InvalidDataException("Сайт перенаправил на другую страницу рассказа.");
                        if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                        {
                            if (redirect == 5 || response.Headers.Location is null) throw new InvalidDataException("Слишком много перенаправлений сайта.");
                            current = SameStory(new Uri(actual, response.Headers.Location), original);
                            if (PageNumber(current) != PageNumber(original)) throw new InvalidDataException("Перенаправление ведёт на другую страницу.");
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
                    finally { _requestGate.Release(); }
                }
                throw new InvalidDataException("Не удалось завершить перенаправления сайта.");
            }
            catch (TransientResponseException error)
            {
                if (attempt == 2 || error.Delay > TimeSpan.FromSeconds(60))
                    throw new HttpRequestException($"Сайт вернул HTTP {error.Status}. Повторите загрузку позднее.");
                await Task.Delay(error.Delay < TimeSpan.Zero ? TimeSpan.Zero : error.Delay, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is HttpRequestException || (error is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                if (attempt == 2) throw new HttpRequestException("Не удалось загрузить страницу после повторных попыток.", error);
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)), cancellationToken).ConfigureAwait(false);
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
        _requestGate.Dispose();
    }
}
