using System.IO.Compression;
using System.Net;
using System.Text;
using System.Xml.Linq;
using TextDownloader.Core;

internal static class Program
{
    private static readonly Uri Source = new("https://sefan.ru/stories/ru/2/drama/chitat_test-story_1.html");
    private static readonly XNamespace Html = "http://www.w3.org/1999/xhtml";
    private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Fb2 = "http://www.gribuser.ru/xml/fictionbook/2.0";

    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--update-crash") return UpdateTests.RunCrashChild(args);
        if (args.Length > 0)
        {
            Console.Error.WriteLine("These tests run offline and accept no arguments.");
            return 2;
        }
        (string Name, Func<Task> Run)[] tests =
        [
            ("URL scope and start from any page", UrlScope),
            ("Stories with arbitrary filename prefixes download every page", FlexibleStoryNames),
            ("HTML text, navigation, source whitespace and age gate", HtmlParsing),
            ("Age confirmation is sent only after explicit permission", AgeConfirmation),
            ("Complete chain joins words and punctuation without page headings", CompleteStory),
            ("Missing, skipped, duplicated and inconsistent pages fail", IncompleteStories),
            ("Cancellation and corrupt cache cannot produce false success", CancellationAndCache),
            ("Preview does not download the entire story", Preview),
            ("EPUB manifest, spine, navigation and exact Unicode text", EpubStructure),
            ("FB2 namespace, metadata and exact Unicode text", Fb2Structure),
            ("Publication uses unique names and preserves third-party files", UniquePublication),
            ("Invalid or cancelled export leaves no partial publication", FailedPublication),
            ("Application package validation, atomic upgrade, rollback and crash recovery", UpdateTests.RunAsync),
        ];
        int failed = 0;
        foreach (var test in tests)
        {
            try { await test.Run(); Console.WriteLine("PASS " + test.Name); }
            catch (Exception error) { failed++; Console.Error.WriteLine("FAIL " + test.Name + "\n" + error); }
        }
        Console.WriteLine($"{tests.Length - failed}/{tests.Length} tests passed.");
        return failed == 0 ? 0 : 1;
    }

    private static Uri PageUrl(int number) => number == 1 ? Source : new(Source.AbsoluteUri.Replace("_1.html", $"_1-{number}.html"));
    private static string PageHtml(string fragment, int? next = null, int? total = null)
    {
        string links = next is int n ? $"<a rel='next' href='{PageUrl(n)}'>Далее</a>" : "";
        if (total is int t) links += $"<a rel='last' title='Страница {t} из {t}' href='{PageUrl(t)}'>Последняя</a>";
        return $"<html><head><meta name='author' content='Автор &amp; Соавтор'></head><body><h1>Тестовый рассказ</h1><div id='rsz'>{fragment}</div>{links}</body></html>";
    }

    private static Task UrlScope()
    {
        using var source = new SefanSource();
        Equal(Source, source.NormalizeToFirstPage(new Uri(PageUrl(12) + "?tracking=1#text")));
        foreach (string bad in new[] {
            Source.AbsoluteUri.Replace("sefan.ru", "sefan.ru.example.org"),
            Source.AbsoluteUri.Replace("https://", "https://user:password@"),
            Source.AbsoluteUri.Replace("_1.html", "_0.html"),
            "https://sefan.ru/stories/ru/2/drama/_1.html",
            "https://sefan.ru/stories/ru/2/drama/vse-chasti/chitat_test-story.html" })
            Check(!source.CanHandle(new Uri(bad)), "Rejected source was accepted: " + bad);
        string other = PageUrl(2).AbsoluteUri.Replace("/2/drama/", "/5/fantasy/");
        Throws(() => SefanSource.Parse(PageHtml("Текст.").Replace("</body>", $"<a rel='next' href='{other}'>Далее</a></body>"), Source));
        return Task.CompletedTask;
    }

    private static async Task FlexibleStoryNames()
    {
        using var parser = new SefanSource();
        foreach (string name in new[] { "test-story", "read_test-story", "rasskaz_2026", "12345" })
        {
            var first = new Uri($"https://sefan.ru/stories/ru/2/drama/{name}_1.html");
            var later = new Uri($"https://sefan.ru/stories/ru/2/drama/{name}_1-12.html");
            Check(parser.CanHandle(first) && parser.CanHandle(later), "A filename without chitat_ was rejected: " + name);
            Equal(first, parser.NormalizeToFirstPage(later));
        }

        var unprefixed = new Uri(Source.AbsoluteUri.Replace("chitat_test-story", "test-story"));
        var second = new Uri(unprefixed.AbsoluteUri.Replace("_1.html", "_1-2.html"));
        string firstHtml = PageHtml("Начало предло", next: 2, total: 2).Replace("chitat_test-story", "test-story");
        string lastHtml = PageHtml("жения.<br>Конец.", total: 2).Replace("chitat_test-story", "test-story");
        using var folder = new TemporaryDirectory();
        using var source = new FakeSource(new Dictionary<Uri, string>
        {
            [unprefixed] = firstHtml,
            [second] = lastHtml,
        });
        var book = await new StoryDownloader(source, folder.Path)
            .DownloadAsync(second, false, false, null, CancellationToken.None);
        Equal(unprefixed, book.SourceUrl);
        Equal(2, book.PageCount);
        Sequence(new[] { "Начало предложения.", "Конец." }, book.Paragraphs);
        Sequence(new[] { unprefixed, second }, source.Calls);

        // Optional naming conventions do not make different filenames aliases.
        Throws(() => SefanSource.Parse(PageHtml("Текст", next: 2, total: 2), unprefixed));
        Throws(() => SefanSource.Parse(firstHtml.Replace("test-story_1-2", "other-story_1-2"), unprefixed));
    }

    private static Task HtmlParsing()
    {
        var first = SefanSource.Parse(PageHtml("Абзац &amp; &lt;текст&gt;.<br>Они дошли до <em>реки</em>\n", next: 2, total: 2), Source);
        Check(first.Fragment.Contains("Абзац & <текст>."), "HTML entities were not decoded.");
        Check(!first.Fragment.Contains("Далее"), "Navigation leaked into story text.");
        Equal(PageUrl(2), first.NextUrl);
        Equal(2, first.TotalPages);
        Equal("Автор & Соавтор", first.Author);
        var terminal = SefanSource.Parse(PageHtml("Целый рассказ.") + "<span class='but'>Далее</span>", Source);
        Equal(1, terminal.TotalPages);
        Throws(() => SefanSource.Parse("<h1>Служебная страница</h1>", Source));
        Throws(() => SefanSource.Parse(PageHtml("Раз", next: 3, total: 3), Source));
        Throws(() => SefanSource.Parse(PageHtml("Раз", next: 2, total: 3) + $"<a rel='next' href='{PageUrl(3)}'>Далее</a>", Source));
        Throws<AgeConfirmationRequiredException>(() => SefanSource.Parse("<form><input name='freed' value='1'><input name='fkay' value='Подтверждаю'></form>", Source));
        return Task.CompletedTask;
    }

    private static async Task CompleteStory()
    {
        using var folder = new TemporaryDirectory();
        using var source = new FakeSource(new Dictionary<Uri, string>
        {
            [PageUrl(1)] = PageHtml("Первый абзац.<br>Они пошли на дру", next: 2, total: 3),
            [PageUrl(2)] = PageHtml("гой берег, где <em>росли</em>\n", next: 3, total: 3),
            [PageUrl(3)] = PageHtml("деревья.<br>Финал &amp; конец.", total: 3),
        });
        var downloader = new StoryDownloader(source, folder.Path);
        var book = await downloader.DownloadAsync(PageUrl(3), false, false, null, CancellationToken.None);
        Equal(3, book.PageCount);
        Equal(Source, book.SourceUrl);
        Equal("Тестовый рассказ", book.Title);
        Sequence(new[] { "Первый абзац.", "Они пошли на другой берег, где росли деревья.", "Финал & конец." }, book.Paragraphs);
        Sequence(new[] { PageUrl(1), PageUrl(2), PageUrl(3) }, source.Calls);
        // A page boundary is not a word boundary: do not insert a space before punctuation.
        using var punctuationSource = new FakeSource(new Dictionary<Uri, string>
        {
            [PageUrl(1)] = PageHtml("Он подумал", next: 2, total: 2),
            [PageUrl(2)] = PageHtml(", затем ответил.", total: 2),
        });
        var punctuation = await new StoryDownloader(punctuationSource, System.IO.Path.Combine(folder.Path, "punctuation"))
            .DownloadAsync(Source, false, false, null, CancellationToken.None);
        Sequence(new[] { "Он подумал, затем ответил." }, punctuation.Paragraphs);
    }

    private static async Task AgeConfirmation()
    {
        const string gate = "<form action=''><input type='hidden' name='freed' value='1'><input type='hidden' name='nonce' value='synthetic-token'><input type='submit' name='fkay' value='Подтверждаю'></form>";
        using (var handler = new SequenceHandler(gate))
        using (var http = new HttpClient(handler))
        using (var source = new SefanSource(http))
        {
            await ThrowsAsync<AgeConfirmationRequiredException>(() => source.GetPageAsync(Source, false, CancellationToken.None));
            Equal(1, handler.Requests.Count);
            Equal(HttpMethod.Get, handler.Requests[0].Method);
        }
        using (var handler = new SequenceHandler(gate, PageHtml("Рассказ после согласия.", total: 1)))
        using (var http = new HttpClient(handler))
        using (var source = new SefanSource(http))
        {
            var page = await source.GetPageAsync(Source, true, CancellationToken.None);
            Equal(1, page.TotalPages);
            Equal(2, handler.Requests.Count);
            Equal(HttpMethod.Post, handler.Requests[1].Method);
            Check(handler.Requests[1].Body.Contains("freed=1") && handler.Requests[1].Body.Contains("nonce=synthetic-token"), "Age form fields were not posted.");
        }
    }

    private static async Task IncompleteStories()
    {
        var cases = new Dictionary<Uri, string>[]
        {
            new() { [Source] = PageHtml("Неизвестная полнота.") },
            new() { [Source] = PageHtml("Раз.", next: 2, total: 3), [PageUrl(2)] = PageHtml("Нет конца.") },
            new() { [Source] = PageHtml("Раз.", next: 3, total: 3) },
            new() { [Source] = PageHtml("Раз.", next: 2, total: 2), [PageUrl(2)] = PageHtml("Два.", total: 3) },
            new() { [Source] = PageHtml("Повтор.", next: 2, total: 2), [PageUrl(2)] = PageHtml("Повтор.", total: 2) },
            new() { [Source] = PageHtml("Раз.", next: 2, total: 2) },
        };
        foreach (var pages in cases)
        {
            using var folder = new TemporaryDirectory();
            using var source = new FakeSource(pages);
            await ThrowsAsync(() => new StoryDownloader(source, folder.Path).DownloadAsync(Source, false, false, null, CancellationToken.None));
        }
        using var limitedFolder = new TemporaryDirectory();
        using var limitedSource = new FakeSource(new() { [Source] = PageHtml("Раз.", next: 2, total: 2) });
        await ThrowsAsync(() => new StoryDownloader(limitedSource, limitedFolder.Path) { MaxPages = 1 }
            .DownloadAsync(Source, false, false, null, CancellationToken.None));
    }

    private static async Task CancellationAndCache()
    {
        using var folder = new TemporaryDirectory();
        using var source = new FakeSource(new() { [Source] = PageHtml("Полный рассказ.", total: 1) });
        var downloader = new StoryDownloader(source, folder.Path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => downloader.DownloadAsync(Source, false, false, null, cancellation.Token));
        Equal(0, source.Calls.Count);
        await downloader.DownloadAsync(Source, false, false, null, CancellationToken.None);
        string[] files = Directory.GetFiles(folder.Path, "*.json", SearchOption.AllDirectories);
        Check(files.Length > 0, "Successful pages were not cached.");
        foreach (string file in files) await File.WriteAllTextAsync(file, "{not valid JSON");
        source.Calls.Clear();
        var book = await downloader.DownloadAsync(Source, false, false, null, CancellationToken.None);
        Equal(1, book.PageCount);
        Check(source.Calls.Count > 0, "Corrupt cache was trusted.");
        source.Calls.Clear();
        await downloader.DownloadAsync(Source, false, true, null, CancellationToken.None);
        Check(source.Calls.Count > 0, "Refresh reused stale cache.");
        using var activeFolder = new TemporaryDirectory();
        using var activeSource = new FakeSource(new()
        {
            [Source] = PageHtml("Сохранившееся начало. ", next: 2, total: 2),
            [PageUrl(2)] = PageHtml("Финал.", total: 2),
        });
        using var activeCancellation = new CancellationTokenSource();
        var active = new StoryDownloader(activeSource, activeFolder.Path);
        await ThrowsAsync<OperationCanceledException>(() => active.DownloadAsync(Source, false, false,
            new InlineProgress(progress => { if (progress.CompletedPages == 1) activeCancellation.Cancel(); }), activeCancellation.Token));
        Equal(1, activeSource.Calls.Count);
        var resumed = await active.DownloadAsync(Source, false, false, null, CancellationToken.None);
        Equal(2, resumed.PageCount);
    }

    private static async Task Preview()
    {
        using var folder = new TemporaryDirectory();
        using var source = new FakeSource(new() { [Source] = PageHtml("Первая страница.", next: 2, total: 2) });
        await new StoryDownloader(source, folder.Path).PreviewAsync(PageUrl(2), false, CancellationToken.None);
        Sequence(new[] { Source }, source.Calls);
    }

    private static BookDocument Book() => new("Тест <&> «Книга»", "Автор & Соавтор", Source,
        new[] { "Первый <не тег> & абзац.", "Юникод 😀 и новая\nстрока.", "Третий абзац." }, 7);

    private static async Task EpubStructure()
    {
        using var folder = new TemporaryDirectory();
        var book = Book();
        var paths = await BookExporter.ExportAsync(book, folder.Path, BookFormat.Epub, CancellationToken.None);
        Equal(1, paths.Count);
        using var archive = ZipFile.OpenRead(paths[0]);
        Equal("mimetype", archive.Entries[0].FullName);
        Equal("application/epub+zip", Read(archive.Entries[0]));
        // Stored ZIP local header compression method is zero, independent of size heuristics.
        using (var raw = File.OpenRead(paths[0]))
        {
            raw.Position = 8;
            Equal(0, raw.ReadByte()); Equal(0, raw.ReadByte());
        }
        var container = Xml(archive, "META-INF/container.xml");
        string packageName = container.Descendants().Single(x => x.Name.LocalName == "rootfile").Attribute("full-path")!.Value;
        var package = Xml(archive, packageName);
        Equal("3.0", package.Root!.Attribute("version")!.Value);
        Equal(book.Title, package.Descendants(Dc + "title").Single().Value);
        Equal(book.Author, package.Descendants(Dc + "creator").Single().Value);
        Equal("ru", package.Descendants(Dc + "language").Single().Value);
        string prefix = packageName[..(packageName.LastIndexOf('/') + 1)];
        var manifest = package.Descendants(Opf + "item").ToDictionary(x => x.Attribute("id")!.Value);
        foreach (var item in manifest.Values) Check(archive.GetEntry(prefix + item.Attribute("href")!.Value) != null, "Manifest refers to missing file.");
        var spine = package.Descendants(Opf + "itemref").Select(x => x.Attribute("idref")!.Value).ToArray();
        Check(spine.Length >= 2, "Title and continuous text are required.");
        foreach (string item in spine) Check(manifest.ContainsKey(item), "Spine refers to unknown manifest ID.");
        var navItem = manifest.Values.Single(x => x.Attribute("properties")?.Value.Split(' ').Contains("nav") == true);
        var nav = Xml(archive, prefix + navItem.Attribute("href")!.Value);
        var navigation = nav.Descendants(Html + "a").Select(x => x.Attribute("href")!.Value).ToArray();
        foreach (string href in navigation) Check(archive.GetEntry(prefix + href) != null, "Navigation refers to missing file.");
        var ncxItem = manifest.Values.Single(x => x.Attribute("media-type")!.Value == "application/x-dtbncx+xml");
        var ncx = Xml(archive, prefix + ncxItem.Attribute("href")!.Value);
        Sequence(navigation, ncx.Descendants().Where(x => x.Name.LocalName == "content").Select(x => x.Attribute("src")!.Value));
        var textDocuments = spine.Select(id => Xml(archive, prefix + manifest[id].Attribute("href")!.Value)).ToArray();
        var paragraphs = textDocuments.Skip(1).SelectMany(x => x.Descendants(Html + "p")).Select(x => x.Value);
        Sequence(book.Paragraphs, paragraphs);
        Check(textDocuments.All(x => !x.Descendants(Html + "script").Any() && !x.Descendants(Html + "img").Any()), "External active content was embedded.");
    }

    private static async Task Fb2Structure()
    {
        using var folder = new TemporaryDirectory();
        var book = Book();
        var paths = await BookExporter.ExportAsync(book, folder.Path, BookFormat.Fb2, CancellationToken.None);
        var xml = XDocument.Load(paths.Single());
        Equal(Fb2 + "FictionBook", xml.Root!.Name);
        var description = xml.Root.Element(Fb2 + "description")!;
        var info = description.Element(Fb2 + "title-info")!;
        Equal(book.Title, info.Element(Fb2 + "book-title")!.Value);
        Equal(book.Author, info.Element(Fb2 + "author")!.Element(Fb2 + "nickname")!.Value);
        Equal("unrecognised", info.Element(Fb2 + "genre")!.Value);
        Equal("ru", info.Element(Fb2 + "lang")!.Value);
        Equal(book.SourceUrl.AbsoluteUri, description.Element(Fb2 + "document-info")!.Element(Fb2 + "src-url")!.Value);
        Sequence(book.Paragraphs, xml.Root.Element(Fb2 + "body")!.Elements(Fb2 + "section").Elements(Fb2 + "p").Select(x => x.Value));
        var unknownPaths = await BookExporter.ExportAsync(book with { Author = null }, folder.Path, BookFormat.Fb2, CancellationToken.None);
        var unknown = XDocument.Load(unknownPaths.Single());
        Check(!string.IsNullOrWhiteSpace(unknown.Descendants(Fb2 + "author").First().Value), "Unknown author metadata is empty.");
    }

    private static async Task UniquePublication()
    {
        using var folder = new TemporaryDirectory();
        var original = await BookExporter.ExportAsync(Book(), folder.Path, BookFormat.Both, CancellationToken.None);
        var bytes = original.ToDictionary(path => path, File.ReadAllBytes);
        var repeated = await BookExporter.ExportAsync(Book(), folder.Path, BookFormat.Both, CancellationToken.None);
        Equal(2, repeated.Count);
        Check(!original.Intersect(repeated, StringComparer.OrdinalIgnoreCase).Any(), "A previous file was overwritten.");
        Equal(System.IO.Path.GetFileNameWithoutExtension(repeated[0]), System.IO.Path.GetFileNameWithoutExtension(repeated[1]));
        foreach (var file in bytes) Sequence(file.Value, File.ReadAllBytes(file.Key));
        Equal(4, Directory.GetFiles(folder.Path).Length);
        Check(!Directory.GetDirectories(folder.Path).Any(), "Owned staging was left behind.");
    }

    private static async Task FailedPublication()
    {
        using var folder = new TemporaryDirectory();
        string sentinel = System.IO.Path.Combine(folder.Path, "unrelated.txt");
        await File.WriteAllTextAsync(sentinel, "Keep this file.");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => BookExporter.ExportAsync(Book(), folder.Path, BookFormat.Both, cancellation.Token));
        await ThrowsAsync(() => BookExporter.ExportAsync(Book() with { Paragraphs = Array.Empty<string>() }, folder.Path, BookFormat.Both, CancellationToken.None));
        await ThrowsAsync(() => BookExporter.ExportAsync(Book() with { Paragraphs = new[] { "Invalid\0XML" } }, folder.Path, BookFormat.Both, CancellationToken.None));
        await ThrowsAsync(() => BookExporter.ExportAsync(Book(), sentinel, BookFormat.Both, CancellationToken.None));
        Equal("Keep this file.", await File.ReadAllTextAsync(sentinel));
        Sequence(new[] { sentinel }, Directory.GetFiles(folder.Path));
        Check(!Directory.GetDirectories(folder.Path).Any(), "Failed export left staging directories.");
    }

    private static XDocument Xml(ZipArchive archive, string path)
    {
        using var stream = archive.GetEntry(path)!.Open(); return XDocument.Load(stream);
    }
    private static string Read(ZipArchiveEntry entry) { using var reader = new StreamReader(entry.Open(), Encoding.UTF8); return reader.ReadToEnd(); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected [{expected}], got [{actual}]."); }
    private static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual) { if (!expected.SequenceEqual(actual)) throw new InvalidOperationException("Sequences differ: expected [" + string.Join(" | ", expected) + "], actual [" + string.Join(" | ", actual) + "]."); }
    private static void Throws(Action action) => Throws<Exception>(action);
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected exception " + typeof(T).Name);
    }
    private static Task ThrowsAsync(Func<Task> action) => ThrowsAsync<Exception>(action);
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected exception " + typeof(T).Name);
    }

    private sealed class FakeSource(Dictionary<Uri, string> pages) : IStorySource
    {
        public List<Uri> Calls { get; } = [];
        public bool CanHandle(Uri uri) { using var source = new SefanSource(); return source.CanHandle(uri); }
        public Uri NormalizeToFirstPage(Uri uri) { using var source = new SefanSource(); return source.NormalizeToFirstPage(uri); }
        public Task<StoryPage> GetPageAsync(Uri uri, bool confirmAdult, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls.Add(uri);
            if (!pages.TryGetValue(uri, out string? html)) throw new IOException("Synthetic page unavailable: " + uri);
            return Task.FromResult(SefanSource.Parse(html, uri));
        }
        public void Dispose() { }
    }

    private sealed class InlineProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }

    private sealed class SequenceHandler(params string[] bodies) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Body)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(token);
            Requests.Add((request.Method, body));
            if (Requests.Count > bodies.Length) throw new IOException("Unexpected synthetic HTTP request.");
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request,
                Content = new StringContent(bodies[Requests.Count - 1], Encoding.UTF8, "text/html") };
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TextDownloader.Tests." + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
