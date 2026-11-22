using System.IO.Compression;
using System.Net;
using System.Text;
using System.Xml.Linq;
using TextDownloader.Core;

internal static class SefanSeriesTests
{
    private const string DirectoryUrl = "https://sefan.ru/stories/ru/2/g/";
    private static readonly string[] Slugs = ["chitat_irina-chast-1-neozhidannaya-prosba",
        "chitat_irina-chast-22", "chitat_irina-chast-32", "chitat_test-future"];
    private static string Page(int chapter, int number = 1) =>
        DirectoryUrl + Slugs[chapter - 1] + (number == 1 ? "_1.html" : $"_1-{number}.html");
    private static string Index => DirectoryUrl + "vse-chasti/" + Slugs[2] + ".html";

    public static async Task InlineContentsAndResumeAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "TextDownloader.SefanSeries." + Guid.NewGuid().ToString("N"));
        try
        {
            string selector = $"""
                <form action="/stories/ru/2/g/" method="post"><div class="center">
                <a class="hud" href="{Index}" title="Все части рассказа">Оглавление</a>
                <div class="nw no-pop"><select name="s">
                <option value="{Slugs[0]}">1. Цикл, ч.1</option>
                <option value="{Slugs[1]}">2. Цикл, ч.2</option>
                <option value="{Slugs[2]}" selected="selected">3. [эта часть] Цикл, ч.3</option>
                <option value="{Slugs[3]}">4. Цикл, ч.4</option>
                </select><input type="submit" value="Читать"/></div></div></form>
                """;
            using var handler = new Pages(new()
            {
                [Page(1)] = Html(1, "Первая глава."),
                [Page(2)] = Html(2, "Середина ", next: Page(2, 2)),
                [Page(3)] = Html(3, "Третья глава.", contents: selector),
                [Page(4)] = Html(4, "Четвёртая глава."),
            });
            using var client = new HttpClient(handler);
            using var router = new SupportedStorySource(new SefanSource(client, TimeSpan.Zero));
            var downloader = new StoryDownloader(router, folder);
            var selected = new Uri(Page(3));
            var preview = await downloader.PreviewAsync(selected, false, CancellationToken.None);
            Require(preview.Contents?.Entries.Count == 4 && handler.Requests.SequenceEqual(new[] { Page(3) }), "Preview followed the index or chapters.");
            handler.Requests.Clear();

            DownloadCheckpoint? checkpoint = null;
            try
            {
                await downloader.DownloadAsync(selected, false, false,
                    new CaptureProgress(value => checkpoint = value.Checkpoint ?? checkpoint), CancellationToken.None);
                throw new Exception("The unavailable page produced a complete book.");
            }
            catch (InvalidDataException) { }
            Require(checkpoint?.LastSuccessfulPageUrl?.AbsoluteUri == Page(2) &&
                checkpoint.PendingPageUrl?.AbsoluteUri == Page(2, 2), "Wrong checkpoint within the series.");
            Require(handler.Requests.SequenceEqual(new[] { Page(3), Page(1), Page(2), Page(2, 2) }),
                "The selector did not supply the expected chapter URLs.");

            handler.Content[Page(2, 2)] = Html(2, "вторая страница.", number: 2);
            handler.Requests.Clear();
            var book = await downloader.DownloadAsync(selected, false, false, null, CancellationToken.None,
                new DownloadRecovery(checkpoint, null));
            Require(handler.Requests.SequenceEqual(new[] { Page(2, 2), Page(4) }), "Retry re-downloaded saved pages or the index.");
            Require(book.PageCount == 5 && book.Title == "Тестовый цикл" && book.Chapters?.Count == 4, "Wrong book metadata.");
            Require(book.Chapters!.Select(chapter => chapter.SourceUrl.AbsoluteUri).SequenceEqual(Enumerable.Range(1, 4).Select(n => Page(n))),
                "The selected and subsequent parts were not kept in chapter order.");
            Require(book.Chapters![1].Paragraphs.SequenceEqual(new[] { "Середина вторая страница." }), "Page seam inside a chapter was changed.");
            var files = await BookExporter.ExportAsync(book, Path.Combine(folder, "books"), BookFormat.Both, CancellationToken.None);
            Require(files.Count == 2, "Expected one file per chosen format.");
            XNamespace fb2 = "http://www.gribuser.ru/xml/fictionbook/2.0";
            var fiction = XDocument.Load(files.Single(file => file.EndsWith(".fb2", StringComparison.Ordinal)));
            Require(fiction.Root!.Element(fb2 + "body")!.Elements(fb2 + "section").Count() == 4, "FB2 lost a chapter.");
            using var archive = ZipFile.OpenRead(files.Single(file => file.EndsWith(".epub", StringComparison.Ordinal)));
            var nav = archive.Entries.Single(entry => entry.Name == "nav.xhtml");
            using var navStream = nav.Open();
            Require(XDocument.Load(navStream).Descendants().Count(element => element.Name.LocalName == "li") == 4, "EPUB lost chapter navigation.");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    public static async Task SeparateContentsAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "TextDownloader.SefanIndex." + Guid.NewGuid().ToString("N"));
        try
        {
            var contents = $"<a href='{Index}' title='Все части рассказа'>Оглавление</a>";
            string List(params int[] parts) => "<ol>" + string.Concat(parts.Select(n =>
                $"<li><a href='{Page(n)}'>Часть {n}</a></li>")) + "</ol>";
            using var handler = new Pages(new()
            {
                [Page(1)] = Html(1, "Первая глава."),
                [Page(2)] = Html(2, "Вторая глава."),
                [Page(3)] = Html(3, "Третья глава.", contents: contents),
                [Index] = List(3, 1),
            });
            using var client = new HttpClient(handler);
            using var source = new SefanSource(client, TimeSpan.Zero);
            var downloader = new StoryDownloader(source, folder);
            try
            {
                await downloader.DownloadAsync(new Uri(Page(3)), false, false, null, CancellationToken.None);
                throw new Exception("A gap in a numbered index was accepted.");
            }
            catch (InvalidDataException) { }
            Require(handler.Requests.SequenceEqual(new[] { Page(3), Index }), "Incomplete index requested chapters.");
            handler.Content[Index] = List(3, 1, 2) + $"<aside><a href='{Page(4)}'>Рекомендация</a></aside>";
            handler.Requests.Clear();
            var book = await downloader.DownloadAsync(new Uri(Page(3)), false, false, null, CancellationToken.None);
            Require(handler.Requests.SequenceEqual(new[] { Index, Page(1), Page(2) }), "Fallback did not use the selected index or included a recommendation.");
            Require(book.Chapters!.Select(chapter => chapter.SourceUrl.AbsoluteUri).SequenceEqual(Enumerable.Range(1, 3).Select(n => Page(n))),
                "The explicit chapter numbers did not control output order.");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    private static string Html(int chapter, string text, int number = 1, string? next = null, string contents = "") =>
        $"<h1>Тестовый цикл. Часть {chapter}</h1><div id='rsz'>{text}{contents}</div>" +
        (next is not null ? $"<a rel='next' href='{next}' title='Страница 2 из 2'>Далее</a>"
            : $"<span class='but'>Далее</span><a rel='last' href='{Page(chapter, number)}'>Последняя</a>");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class CaptureProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }

    private sealed class Pages(Dictionary<string, string> content) : HttpMessageHandler
    {
        public Dictionary<string, string> Content { get; } = content;
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string uri = request.RequestUri!.AbsoluteUri;
            Requests.Add(uri);
            var response = Content.TryGetValue(uri, out string? html)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
