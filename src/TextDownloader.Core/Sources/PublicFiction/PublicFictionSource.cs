using System.Globalization;
using System.Net;
using System.Text;
using HtmlAgilityPack;

namespace TextDownloader.Core;

internal sealed class PublicFictionSource : IBookStorySource
{
    private const int MaxChapters = 10000;
    private const int MaxHtmlBytes = 8 * 1024 * 1024;
    private static readonly string[] ParagraphTags =
        ["blockquote", "div", "h2", "h3", "h4", "li", "p", "pre", "section"];
    private static readonly string[] IgnoredTags =
        ["aside", "button", "form", "iframe", "nav", "noscript", "rp", "rt", "script", "style", "template"];

    private readonly PublicFictionSite _site;
    private readonly Lazy<HttpClient> _http;
    private readonly bool _ownsClient;
    private readonly TimeSpan _interval;
    private readonly PageRequestThrottle _throttle;
    private readonly IProgress<DownloadLogEvent>? _log;

    static PublicFictionSource() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public PublicFictionSource(PublicFictionSite site, TimeSpan interval, PageRequestThrottle throttle,
        IProgress<DownloadLogEvent>? log = null, SiteAuthorization? rule = null, HttpClient? http = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(interval, TimeSpan.Zero);
        _site = site;
        _interval = interval;
        _throttle = throttle;
        _log = log;
        _ownsClient = http is null;
        _http = new Lazy<HttpClient>(() =>
        {
            if (http is not null) return http;
            var client = StoryHttpAuthorization.CreateClient(rule);
            client.MaxResponseContentBufferSize = MaxHtmlBytes;
            return client;
        });
    }

    public bool CanHandle(Uri url) => _site.Owns(url) && (_site.Work(url).Success || _site.Chapter(url).Success);

    public Uri NormalizeToFirstPage(Uri url) => CanHandle(url) ? _site.Normalize(url)
        : throw new ArgumentException("Нужна ссылка на произведение или главу " + _site.Name + ".");

    public Uri ValidateReplacementPage(Uri expected, Uri candidate)
    {
        var replacement = NormalizeToFirstPage(candidate);
        if (NormalizeToFirstPage(expected) == replacement) return replacement;
        throw new ArgumentException("Укажите адрес той же главы.");
    }

    public Uri ValidatePageRoute(Uri first, Uri candidate, int? expectedNumber)
    {
        if (CanHandle(first) && CanHandle(candidate) && WorkKey(first) == WorkKey(candidate))
            return _site.Normalize(candidate);
        throw new InvalidDataException("Ссылка ведёт за пределы произведения " + _site.Name + ".");
    }

    public StorySourceInfo Describe(Uri uri) => new(_site.Name, "глав");
    public string DescribePreview(StoryPage page) => "Предпросмотр главы. Загрузка произведения начинается с первой главы.";

    public async Task<StoryPage> GetPageAsync(Uri uri, bool confirmAdult, CancellationToken cancellationToken)
    {
        var url = NormalizeToFirstPage(uri);
        if (!_site.Work(url).Success)
            return ParseChapter(await ReadStoryAsync(url, cancellationToken).ConfigureAwait(false), url, 1);

        var index = await ReadIndexAsync(url, cancellationToken).ConfigureAwait(false);
        return index.Start == url ? ParseChapter(index.Html, url, 1, index.Title)
            : ParseChapter(await ReadStoryAsync(index.Start, cancellationToken).ConfigureAwait(false), index.Start, 1, index.Title);
    }

    public async Task<BookDocument> DownloadBookAsync(Uri url, bool confirmAdult, bool refresh, string cacheDirectory,
        IProgress<DownloadProgress>? progress, CancellationToken cancellationToken, DownloadRecovery? recovery = null)
    {
        var requested = NormalizeToFirstPage(url);
        var chapter = _site.Chapter(requested);
        var work = _site.Work(requested).Success ? requested : _site.WorkFromChapter is { } path
            ? ValidatePageRoute(requested, new Uri(requested, chapter.Result(path)), null)
            : throw new ArgumentException("Для загрузки всей книги укажите адрес её оглавления.");
        if (!_site.Work(work).Success) throw new InvalidDataException("Не удалось подтвердить адрес оглавления.");
        progress?.Report(new DownloadProgress(0, null, "Читаем оглавление " + _site.Name + "…")
            { Checkpoint = new DownloadCheckpoint(work, null, work) });
        var index = await ReadIndexAsync(work, cancellationToken).ConfigureAwait(false);
        var cacheReader = new StoryDownloader(this, cacheDirectory);
        var cache = cacheReader.PartCacheDirectory(work);
        var chapters = new List<BookChapter>();
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        long characters = 0;
        Uri? next = index.Start;
        Uri? last = null;
        int? total = index.Chapters?.Count;

        while (next is { } current)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int number = chapters.Count + 1;
            if (number > MaxChapters || !visited.Add(ChapterKey(current)))
                throw new InvalidDataException("Повтор главы или превышен предел глав.");
            Uri? plannedNext = index.Chapters is { } plan && number < plan.Count ? plan[number] : null;
            var page = refresh ? null : await cacheReader.ReadCacheAsync(cache, number, work, current, cancellationToken)
                .ConfigureAwait(false);
            if (page is not null && (index.Chapters is not null && (page.NextUrl != plannedNext || page.TotalPages != total) ||
                HasFollowLink && page.NextUrl is null))
                page = null; // The last chapter can gain a next link since the previous run.
            string? fingerprint = null;
            if (page is not null)
            {
                try { fingerprint = StoryDownloader.ValidateChainPage(page, total, fingerprints); }
                catch (InvalidDataException) { page = null; }
            }
            if (page is null)
            {
                refresh = true; // Only the checked prefix is reusable.
                progress?.Report(new DownloadProgress(number - 1, total, $"Получаем главу {number}…")
                    { Checkpoint = new DownloadCheckpoint(work, last, current) });
                var html = current == work ? index.Html : await ReadStoryAsync(current, cancellationToken).ConfigureAwait(false);
                page = ParseChapter(html, current, number, index.Title);
                var subsequent = index.Chapters is not null ? plannedNext : ReadNext(html, work, current);
                page = page with { NextUrl = subsequent, TotalPages = total ?? (subsequent is null ? number : null) };
                cacheReader.ValidatePage(page, work, current, number);
                fingerprint = StoryDownloader.ValidateChainPage(page, total, fingerprints);
                await StoryDownloader.WriteCacheAsync(cache, page, cancellationToken).ConfigureAwait(false);
            }
            fingerprints.Add(fingerprint!);
            characters += page.Fragment.Length;
            if (characters > StoryDownloader.MaxStoryCharacters)
                throw new InvalidDataException("Текст превышает допустимый объём одной книги.");
            chapters.Add(new BookChapter(page.Title, page.Url, StoryDownloader.JoinParagraphs([page])));
            last = page.Url;
            next = page.NextUrl;
            total ??= page.TotalPages;
            progress?.Report(new DownloadProgress(number, total, $"Глава {number} проверена.")
                { Checkpoint = new DownloadCheckpoint(work, last, next) });
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (chapters.Count != total) throw new InvalidDataException("Не удалось подтвердить полноту произведения.");
        var paragraphs = chapters.SelectMany(chapter => chapter.Paragraphs).ToArray();
        return new BookDocument(index.Title, index.Author, work, paragraphs, chapters.Count)
            { Chapters = chapters.AsReadOnly() };
    }

    // Catalogs are used by the opt-in integration tests. Reading URLs still follow the normal router.
    internal async Task<IReadOnlyList<Uri>> ReadCatalogAsync(CancellationToken token)
    {
        var catalog = _site.Catalog;
        var html = await ReadHtmlAsync(catalog, target => PublicFictionSite.IsWeb(target) &&
            target.Scheme == Uri.UriSchemeHttps && catalog.IdnHost == target.IdnHost &&
            catalog.AbsolutePath == target.AbsolutePath, token).ConfigureAwait(false);
        var works = new Dictionary<string, Uri>(StringComparer.Ordinal);
        foreach (var node in Nodes(html.DocumentNode, _site.CatalogLinks))
        {
            if (TryLink(catalog, node, out var link) && _site.Owns(link) && _site.Work(link).Success)
                works.TryAdd(WorkKey(link), _site.Normalize(link));
        }
        return works.Values.ToArray();
    }

    private bool HasFollowLink => _site.NextChapter is not null;

    private async Task<Index> ReadIndexAsync(Uri url, CancellationToken token)
    {
        var html = await ReadStoryAsync(url, token).ConfigureAwait(false);
        string title = Text(html.DocumentNode.SelectSingleNode(_site.WorkTitle));
        if (title.Length == 0) throw new InvalidDataException("Не найдено название произведения " + _site.Name + ".");
        var author = _site.Author is { } path ? Text(html.DocumentNode.SelectSingleNode(path)) : null;
        if (author?.Length == 0) author = null;
        if (_site.ChapterOptions is { } options)
        {
            var chapters = ReadOptions(html, url, options);
            return new Index(html, title, author, chapters[0], chapters);
        }
        if (_site.Contents is { } contents)
        {
            var chapters = ReadLinks(html, url, url, contents);
            if (chapters.Count > 0)
            {
                if (_site.ReverseContents) chapters.Reverse();
                return new Index(html, title, author, chapters[0], chapters);
            }
        }
        if (_site.FirstChapter is { } first)
        {
            var chapters = ReadLinks(html, url, url, first);
            if (chapters.Count == 1)
                return new Index(html, title, author, chapters[0], HasFollowLink ? null : chapters);
            if (chapters.Count > 1) throw new InvalidDataException("Найдено несколько первых глав.");
        }
        if (_site.AllowStandalone is { } standalone && html.DocumentNode.SelectSingleNode(standalone) is not null)
            return new Index(html, title, author, url, [url]);
        throw new InvalidDataException("Не найдено оглавление произведения " + _site.Name + ".");
    }

    private List<Uri> ReadOptions(HtmlDocument html, Uri work, string options)
    {
        var chapter = _site.Chapter(work);
        var links = new List<Uri>();
        foreach (var node in Nodes(html.DocumentNode, options))
        {
            if (!int.TryParse(Attribute(node, "value"), NumberStyles.None, CultureInfo.InvariantCulture, out int part) ||
                part != links.Count + 1 || part > MaxChapters)
                throw new InvalidDataException("Пропущен номер главы в оглавлении.");
            string path = chapter.Result(_site.ChapterFromOption!).Replace("{number}",
                part.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
            links.Add(ValidatePageRoute(work, part == 1 ? work : new Uri(work, path), part));
        }
        if (links.Count == 0) links.Add(work);
        return links;
    }

    private Uri? ReadNext(HtmlDocument html, Uri work, Uri current)
    {
        var next = ReadLinks(html, work, current, _site.NextChapter!);
        return next.Count switch
        {
            0 => null,
            1 => next[0],
            _ => throw new InvalidDataException("Найдено несколько следующих глав.")
        };
    }

    private List<Uri> ReadLinks(HtmlDocument html, Uri work, Uri current, string xpath)
    {
        var chapters = new List<Uri>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in Nodes(html.DocumentNode, xpath))
        {
            if (!TryLink(current, node, out var link) || !CanHandle(link) || !_site.Chapter(link).Success)
                throw new InvalidDataException("Оглавление содержит недопустимую ссылку на главу.");
            var normalized = ValidatePageRoute(work, link, null);
            if (seen.Add(ChapterKey(normalized))) chapters.Add(normalized);
            if (chapters.Count > MaxChapters) throw new InvalidDataException("Превышен предел глав.");
        }
        return chapters;
    }

    private StoryPage ParseChapter(HtmlDocument html, Uri url, int number, string? fallbackTitle = null)
    {
        var content = html.DocumentNode.SelectSingleNode(_site.Content)
            ?? throw new InvalidDataException("Не найден текст главы " + _site.Name + ".");
        string title = Text(html.DocumentNode.SelectSingleNode(_site.ChapterTitle));
        if (title.Length == 0) title = fallbackTitle ?? "";
        string? author = _site.Author is { } path ? Text(html.DocumentNode.SelectSingleNode(path)) : null;
        if (_site.ExcludeContent is { } exclude)
            foreach (var node in Nodes(content, exclude).ToArray()) node.Remove();
        var fragment = new StringBuilder();
        AppendText(content, fragment);
        string text = fragment.ToString().Trim();
        if (title.Length == 0 || !text.Any(char.IsLetterOrDigit))
            throw new InvalidDataException("Глава " + _site.Name + " не содержит названия или текста.");
        return new StoryPage(url, number, title, author, text, null, null);
    }

    private async Task<HtmlDocument> ReadStoryAsync(Uri url, CancellationToken token)
    {
        bool Matches(Uri target) => CanHandle(target) && target.Scheme == Uri.UriSchemeHttps &&
            ChapterKey(target) == ChapterKey(url);
        var html = await ReadHtmlAsync(url, Matches, token).ConfigureAwait(false);
        var canonical = html.DocumentNode.SelectSingleNode("//link[@rel='canonical' and @href]");
        bool CanonicalMatches(Uri target) => Matches(target) || _site.CanonicalMayIdentifyWork &&
            _site.Owns(target) && target.Scheme == Uri.UriSchemeHttps && _site.Work(target).Success &&
            WorkKey(target) == WorkKey(url);
        if (canonical is not null && (!TryLink(url, canonical, out var target) || !CanonicalMatches(target)))
            throw new InvalidDataException("Канонический адрес страницы ведёт к другому произведению или главе.");
        return html;
    }

    private async Task<HtmlDocument> ReadHtmlAsync(Uri initial, Func<Uri, bool> allowed, CancellationToken token)
    {
        Uri current = initial;
        for (int redirects = 0; redirects <= 4; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.ParseAdd("TextDownloader/1.0");
            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml");
            await _throttle.EnterAsync(_interval, token).ConfigureAwait(false);
            HttpResponseMessage response;
            try
            {
                _log?.Report(new DownloadLogEvent(DownloadLogLevel.Detail, $"GET {current.GetLeftPart(UriPartial.Path)}"));
                response = await _http.Value.SendAsync(request, token).ConfigureAwait(false);
            }
            finally { _throttle.Exit(); }
            using (response)
            {
                _log?.Report(new DownloadLogEvent(DownloadLogLevel.Detail, $"HTTP {(int)response.StatusCode}"));
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or
                    HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    if (response.Headers.Location is not { } location || !Uri.TryCreate(current, location, out var target) ||
                        !allowed(target))
                        throw new InvalidDataException("Сайт перенаправил запрос за пределы выбранной страницы.");
                    current = target;
                    continue;
                }
                response.EnsureSuccessStatusCode();
                var bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
                if (bytes.Length > MaxHtmlBytes) throw new InvalidDataException("HTML страницы слишком велик.");
                var html = new HtmlDocument();
                var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"');
                Encoding encoding = charset is null ? Encoding.UTF8 : Encoding.GetEncoding(charset);
                using var stream = new MemoryStream(bytes, writable: false);
                html.Load(stream, encoding);
                if (charset is null && html.DeclaredEncoding is { } declared && declared.CodePage != encoding.CodePage)
                {
                    stream.Position = 0;
                    html.Load(stream, declared);
                }
                return html;
            }
        }
        throw new InvalidDataException("Сайт превысил предел перенаправлений.");
    }

    private string WorkKey(Uri url)
    {
        var work = _site.Work(url);
        return PublicFictionSite.Key(work.Success ? work : _site.Chapter(url));
    }

    private string ChapterKey(Uri url)
    {
        var chapter = _site.Chapter(url);
        return WorkKey(url) + "/" + PublicFictionSite.Part(chapter);
    }

    private static bool TryLink(Uri url, HtmlNode node, out Uri link) =>
        Uri.TryCreate(url, Attribute(node, node.Name == "option" ? "value" : "href"), out link!);

    private static string Attribute(HtmlNode node, string name) =>
        HtmlEntity.DeEntitize(node.GetAttributeValue(name, "")).Trim();

    private static string Text(HtmlNode? node) =>
        StoryText.Clean(HtmlEntity.DeEntitize(node?.Name == "meta" ? node.GetAttributeValue("content", "") :
            node?.InnerText ?? "").Replace('\u00a0', ' '));

    private static IEnumerable<HtmlNode> Nodes(HtmlNode root, string xpath) =>
        root.SelectNodes(xpath) ?? Enumerable.Empty<HtmlNode>();

    private static void AppendText(HtmlNode node, StringBuilder text)
    {
        if (node.NodeType == HtmlNodeType.Text)
        {
            text.Append(HtmlEntity.DeEntitize(node.InnerText).Replace('\u00a0', ' ')
                .Replace('\r', ' ').Replace('\n', ' '));
            return;
        }
        if (node.NodeType != HtmlNodeType.Element || IgnoredTags.Contains(node.Name)) return;
        if (node.Name == "br") { text.Append('\n'); return; }
        bool block = ParagraphTags.Contains(node.Name);
        if (block) text.Append('\n');
        foreach (var child in node.ChildNodes) AppendText(child, text);
        if (block) text.Append('\n');
    }

    public void Dispose()
    {
        if (_ownsClient && _http.IsValueCreated) _http.Value.Dispose();
    }

    private sealed record Index(HtmlDocument Html, string Title, string? Author, Uri Start, IReadOnlyList<Uri>? Chapters);
}
