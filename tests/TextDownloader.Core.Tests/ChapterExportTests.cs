using System.IO.Compression;
using System.Xml.Linq;
using TextDownloader.Core;

internal static class ChapterExportTests
{
    private static readonly XNamespace Html = "http://www.w3.org/1999/xhtml";
    private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Epub = "http://www.idpf.org/2007/ops";
    private static readonly XNamespace Ncx = "http://www.daisy.org/z3986/2005/ncx/";
    private static readonly XNamespace Fb = "http://www.gribuser.ru/xml/fictionbook/2.0";
    private static readonly XNamespace Link = "http://www.w3.org/1999/xlink";

    public static async Task RunAsync()
    {
        await OrderedChaptersAsync();
        await ExplicitSingleChapterAsync();
        await LegacyContinuousTextAsync();
        await InvalidChapterModelsAsync();
    }

    private static BookDocument Book()
    {
        BookChapter[] chapters =
        [
            new("Одинаковая глава <&> / «Путь»", new Uri("https://example.invalid/post/200?x=1&y=2"),
                ["Первый <не тег> & абзац.", "Строки\nи эмодзи 😀."]),
            new("Глава / 2: & <Середина>", new Uri("https://example.invalid/post/100"),
                ["Середина — порядок задаёт список глав, а не URL."]),
            new("Одинаковая глава <&> / «Путь»", new Uri("https://example.invalid/post/200?x=1&y=2"),
                ["Финал. Одинаковые названия не объединяются."])
        ];
        return new BookDocument("Синтетический цикл <&>", "Автор & Соавтор", new Uri("https://example.invalid/series/fixture"),
            chapters.SelectMany(chapter => chapter.Paragraphs).ToArray(), 8) { Chapters = chapters };
    }

    private static async Task OrderedChaptersAsync()
    {
        using var folder = new Folder();
        BookDocument book = Book();
        IReadOnlyList<string> files = await BookExporter.ExportAsync(book, folder.Path, BookFormat.Both, CancellationToken.None);
        Check(files.Count == 2, "Both formats must be published.");
        Check(!Directory.GetDirectories(folder.Path).Any(), "Chapter export left staging folders.");
        Check(!Directory.GetFiles(folder.Path).Any(path => Path.GetExtension(path) is not (".epub" or ".fb2")), "Chapter XHTML leaked outside the EPUB.");
        VerifyEpub(files.Single(path => path.EndsWith(".epub", StringComparison.Ordinal)), book);
        VerifyFb2(files.Single(path => path.EndsWith(".fb2", StringComparison.Ordinal)), book);

        // Chapter mode uses the same non-overwriting publication transaction.
        var before = files.ToDictionary(path => path, File.ReadAllBytes);
        var repeated = await BookExporter.ExportAsync(book, folder.Path, BookFormat.Both, CancellationToken.None);
        Check(!files.Intersect(repeated, StringComparer.OrdinalIgnoreCase).Any(), "Chapter export overwrote an existing book.");
        Check(Path.GetFileNameWithoutExtension(repeated[0]) == Path.GetFileNameWithoutExtension(repeated[1]), "EPUB/FB2 output stems diverged.");
        foreach (var saved in before) Equal(saved.Value, File.ReadAllBytes(saved.Key), "Existing book changed.");
    }

    private static void VerifyEpub(string path, BookDocument book)
    {
        var chapters = book.Chapters!;
        string[] ids = Enumerable.Range(1, chapters.Count).Select(number => $"chapter{number:D4}").ToArray();
        string[] hrefs = ids.Select(id => id + ".xhtml").ToArray();
        using var zip = ZipFile.OpenRead(path);
        Check(zip.Entries.Count == 6 + chapters.Count && zip.GetEntry("EPUB/text.xhtml") is null, "Chapters were flattened into the legacy text file.");
        Check(zip.Entries[0].FullName == "mimetype", "EPUB mimetype is not first.");
        using (var stream = File.OpenRead(path))
        {
            stream.Position = 8;
            Check(stream.ReadByte() == 0 && stream.ReadByte() == 0, "EPUB mimetype was compressed.");
        }
        XDocument package = Read(zip, "EPUB/package.opf");
        var manifest = package.Descendants(Opf + "item").ToDictionary(item => item.Attribute("id")!.Value);
        Equal(new[] { "title", "nav" }.Concat(ids), package.Descendants(Opf + "itemref").Select(item => item.Attribute("idref")!.Value),
            "The visible TOC must follow the title and precede the ordered chapters.");
        Check(manifest["nav"].Attribute("properties")?.Value == "nav", "Reader EPUB navigation is not declared.");
        Check(package.Descendants(Opf + "spine").Single().Attribute("toc")?.Value == "ncx", "NCX fallback is missing.");
        foreach (var item in manifest.Values)
            Check(zip.GetEntry("EPUB/" + item.Attribute("href")!.Value) is not null, "Manifest target is missing.");
        Check(package.Descendants(Dc + "title").Single().Value == book.Title && package.Descendants(Dc + "creator").Single().Value == book.Author,
            "Book title/author metadata changed.");
        Check(package.Descendants(Dc + "source").Single().Value == book.SourceUrl.AbsoluteUri, "Book source metadata changed.");
        var nav = Read(zip, "EPUB/nav.xhtml").Descendants(Html + "nav").Single();
        Check(nav.Attribute(Epub + "type")?.Value == "toc", "EPUB navigation does not advertise a TOC.");
        var links = nav.Element(Html + "ol")!.Elements(Html + "li").Select(item => item.Element(Html + "a")!).ToArray();
        Equal(chapters.Select(chapter => chapter.Title), links.Select(link => link.Value), "Visible TOC reordered or merged duplicate titles.");
        Equal(hrefs, links.Select(link => link.Attribute("href")!.Value), "Visible TOC targets are incorrect.");
        var ncx = Read(zip, "EPUB/toc.ncx");
        var points = ncx.Descendants(Ncx + "navPoint").ToArray();
        Equal(hrefs, points.Select(point => point.Element(Ncx + "content")!.Attribute("src")!.Value), "NCX and EPUB navigation targets disagree.");
        Equal(chapters.Select(chapter => chapter.Title), points.Select(point => point.Element(Ncx + "navLabel")!.Element(Ncx + "text")!.Value), "NCX chapter labels disagree.");
        Equal(Enumerable.Range(1, chapters.Count).Select(number => number.ToString()), points.Select(point => point.Attribute("playOrder")!.Value), "NCX order is not sequential.");
        Check(ncx.Descendants(Ncx + "meta").Single(meta => meta.Attribute("name")?.Value == "dtb:uid").Attribute("content")?.Value ==
            package.Descendants(Dc + "identifier").Single().Value, "EPUB and NCX identifiers disagree.");
        var narrative = new List<string>();
        for (int index = 0; index < chapters.Count; index++)
        {
            Check(manifest[ids[index]].Attribute("href")?.Value == hrefs[index], "Spine ID resolves to another chapter.");
            var document = Read(zip, "EPUB/" + hrefs[index]);
            var section = document.Root!.Element(Html + "body")!.Element(Html + "section")!;
            Check(section.Attribute("id")?.Value == ids[index], "Chapter section ID is incorrect.");
            Check(section.Element(Html + "header")!.Element(Html + "h1")!.Value == chapters[index].Title, "Chapter heading changed.");
            Check(section.Element(Html + "header")!.Element(Html + "a")!.Attribute("href")?.Value == chapters[index].SourceUrl.AbsoluteUri, "Clickable chapter source is missing.");
            Check(document.Root.Element(Html + "head")!.Elements(Html + "meta").Single(meta => meta.Attribute("name")?.Value == "dc.source")
                .Attribute("content")?.Value == chapters[index].SourceUrl.AbsoluteUri, "Chapter source metadata changed.");
            var text = section.Elements(Html + "p").Select(paragraph => paragraph.Value).ToArray();
            Equal(chapters[index].Paragraphs, text, "Chapter narrative gained metadata or lost Unicode text.");
            Check(!document.Descendants(Html + "script").Any() && !document.Descendants(Html + "img").Any(), "Text was interpreted as active markup.");
            narrative.AddRange(text);
        }
        Equal(book.Paragraphs, narrative, "EPUB narrative differs from the canonical flattened book text.");
    }

    private static void VerifyFb2(string path, BookDocument book)
    {
        XDocument document = XDocument.Load(path);
        var chapters = book.Chapters!;
        Check(document.Root!.Name == Fb + "FictionBook", "Wrong FB2 namespace.");
        var sections = document.Root.Element(Fb + "body")!.Elements(Fb + "section").ToArray();
        Equal(chapters.Select(chapter => chapter.Title), sections.Select(section => section.Element(Fb + "title")!.Element(Fb + "p")!.Value),
            "FB2 reader TOC does not contain ordered chapter titles.");
        Check(sections.Select(section => section.Attribute("id")!.Value).Distinct(StringComparer.Ordinal).Count() == chapters.Count,
            "FB2 sections have duplicate IDs.");
        var sources = document.Descendants(Fb + "document-info").Single().Elements(Fb + "src-url").Select(source => source.Value);
        Equal(new[] { book.SourceUrl.AbsoluteUri }.Concat(chapters.Select(chapter => chapter.SourceUrl.AbsoluteUri)), sources, "FB2 source metadata order changed.");
        var narrative = new List<string>();
        for (int index = 0; index < chapters.Count; index++)
        {
            var section = sections[index];
            Check(section.Elements().First().Name == Fb + "title" && section.Elements().Skip(1).First().Name == Fb + "annotation",
                "FB2 metadata is not in schema order.");
            Check(section.Element(Fb + "annotation")!.Element(Fb + "p")!.Element(Fb + "a")!.Attribute(Link + "href")?.Value ==
                chapters[index].SourceUrl.AbsoluteUri, "FB2 clickable chapter source is missing.");
            var text = section.Elements(Fb + "p").Select(paragraph => paragraph.Value).ToArray();
            Equal(chapters[index].Paragraphs, text, "FB2 narrative includes metadata or changed text.");
            narrative.AddRange(text);
        }
        Equal(book.Paragraphs, narrative, "FB2 flattened narrative differs from EPUB/book text.");
    }

    private static async Task ExplicitSingleChapterAsync()
    {
        using var folder = new Folder();
        BookChapter chapter = new("Одна логическая глава", new Uri("https://example.invalid/one"), ["Текст одной главы."]);
        var book = new BookDocument("Одна часть", null, chapter.SourceUrl, chapter.Paragraphs, 1) { Chapters = [chapter] };
        var files = await BookExporter.ExportAsync(book, folder.Path, BookFormat.Both, CancellationToken.None);
        // Unknown author is permitted. Use a metadata expectation for this helper.
        using (var zip = ZipFile.OpenRead(files.Single(file => file.EndsWith(".epub", StringComparison.Ordinal))))
        {
            Check(zip.GetEntry("EPUB/chapter0001.xhtml") is not null && zip.GetEntry("EPUB/text.xhtml") is null, "An explicit single chapter lost its logical heading.");
            Equal(new[] { "title", "nav", "chapter0001" }, Read(zip, "EPUB/package.opf").Descendants(Opf + "itemref").Select(item => item.Attribute("idref")!.Value), "Single chapter TOC is not visible in reading order.");
        }
        VerifyFb2(files.Single(file => file.EndsWith(".fb2", StringComparison.Ordinal)), book);
    }

    private static async Task LegacyContinuousTextAsync()
    {
        using var folder = new Folder();
        var book = new BookDocument("Непрерывный рассказ", null, new Uri("https://example.invalid/continuous"), ["Начало и конец одного текста."], 3);
        var files = await BookExporter.ExportAsync(book, folder.Path, BookFormat.Both, CancellationToken.None);
        using (var zip = ZipFile.OpenRead(files.Single(file => file.EndsWith(".epub", StringComparison.Ordinal))))
        {
            Check(zip.Entries.Count == 7 && zip.GetEntry("EPUB/text.xhtml") is not null && !zip.Entries.Any(entry => entry.FullName.Contains("chapter", StringComparison.Ordinal)), "Legacy Sefan EPUB structure changed.");
            Equal(new[] { "title", "text" }, Read(zip, "EPUB/package.opf").Descendants(Opf + "itemref").Select(item => item.Attribute("idref")!.Value), "Legacy spine changed.");
            Equal(book.Paragraphs, Read(zip, "EPUB/text.xhtml").Descendants(Html + "p").Select(paragraph => paragraph.Value), "Legacy text gained metadata paragraphs.");
        }
        var fb2 = XDocument.Load(files.Single(file => file.EndsWith(".fb2", StringComparison.Ordinal)));
        var section = fb2.Root!.Element(Fb + "body")!.Elements(Fb + "section").Single();
        Check(section.Attribute("id")?.Value == "text" && section.Element(Fb + "annotation") is null, "Legacy FB2 section changed.");
        Check(fb2.Descendants(Fb + "src-url").Count() == 1, "Legacy source metadata changed.");
        Equal(book.Paragraphs, section.Elements(Fb + "p").Select(paragraph => paragraph.Value), "Legacy FB2 text changed.");
    }

    private static async Task InvalidChapterModelsAsync()
    {
        using var folder = new Folder();
        string sentinel = Path.Combine(folder.Path, "unrelated.txt");
        await File.WriteAllTextAsync(sentinel, "Preserve this file.");
        BookDocument book = Book();
        BookChapter first = book.Chapters![0];
        BookDocument[] invalid =
        [
            book with { Chapters = Array.Empty<BookChapter>() },
            book with { Chapters = [null!] },
            book with { Chapters = [first with { Title = " " }] },
            book with { Chapters = [first with { Title = "Bad\0title" }] },
            book with { Chapters = [first with { SourceUrl = new Uri("relative", UriKind.Relative) }] },
            book with { Chapters = [first with { SourceUrl = new Uri("ftp://example.invalid/post") }] },
            book with { Chapters = [first with { Paragraphs = null! }] },
            book with { Chapters = [first with { Paragraphs = Array.Empty<string>() }] },
            book with { Chapters = [first with { Paragraphs = [" "] }] },
            book with { Chapters = [first with { Paragraphs = ["Bad\0text"] }] },
            book with { Paragraphs = book.Paragraphs.Reverse().ToArray() },
            book with { Paragraphs = book.Paragraphs.Append("Unexpected duplicate or extra paragraph.").ToArray() },
        ];
        foreach (BookDocument candidate in invalid)
        {
            bool rejected = false;
            try { await BookExporter.ExportAsync(candidate, folder.Path, BookFormat.Both, CancellationToken.None); }
            catch (Exception error) when (error is InvalidDataException or System.Xml.XmlException) { rejected = true; }
            Check(rejected, "An invalid chapter model was exported.");
            Equal(new[] { sentinel }, Directory.GetFiles(folder.Path), "Invalid chapter model published a partial book.");
            Check(!Directory.GetDirectories(folder.Path).Any(), "Invalid chapter model left staging folders.");
        }
        Check(await File.ReadAllTextAsync(sentinel) == "Preserve this file.", "Invalid chapter export changed unrelated data.");
    }

    private static XDocument Read(ZipArchive zip, string name)
    {
        using var stream = (zip.GetEntry(name) ?? throw new InvalidOperationException("Missing ZIP entry: " + name)).Open();
        return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
    }

    private static void Equal<T>(IEnumerable<T> expected, IEnumerable<T> actual, string message) => Check(expected.SequenceEqual(actual), message);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class Folder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TextDownloader-chapter-tests-" + Guid.NewGuid().ToString("N"));
        public Folder() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
