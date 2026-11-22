using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace TextDownloader.Core;

/// <summary>Creates validated books, then publishes a complete, non-overwriting result.</summary>
public static class BookExporter
{
    private static readonly XNamespace Html = "http://www.w3.org/1999/xhtml";
    private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Epub = "http://www.idpf.org/2007/ops";
    private static readonly XNamespace Ncx = "http://www.daisy.org/z3986/2005/ncx/";
    private static readonly XNamespace Fb = "http://www.gribuser.ru/xml/fictionbook/2.0";
    private static readonly XNamespace Link = "http://www.w3.org/1999/xlink";
    private static readonly XNamespace Container = "urn:oasis:names:tc:opendocument:xmlns:container";
    private static readonly SemaphoreSlim PublicationGate = new(1, 1);
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static Task<IReadOnlyList<string>> ExportAsync(BookDocument book, string outputDirectory,
        BookFormat format, CancellationToken token)
    {
        // XML creation, validation, ZIP compression and disk access never run on the UI thread.
        return Task.Run<IReadOnlyList<string>>(() => Export(book, outputDirectory, format, token), token);
    }

    private static IReadOnlyList<string> Export(BookDocument book, string outputDirectory,
        BookFormat format, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(book);
        if (format is not (BookFormat.Epub or BookFormat.Fb2 or BookFormat.Both))
            throw new ArgumentOutOfRangeException(nameof(format));
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new ArgumentException("Не выбрана папка сохранения.", nameof(outputDirectory));
        book = Snapshot(book, token);
        string directory = Path.GetFullPath(outputDirectory);
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        string stage = Path.Combine(directory, ".text-downloader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var files = new List<StagedFile>();
        try
        {
            if (format is BookFormat.Epub or BookFormat.Both)
            {
                string path = Path.Combine(stage, "book.epub");
                WriteEpub(path, book, token);
                ValidateEpub(path, book, token);
                files.Add(StagedFile.Create(path, ".epub", token));
            }
            if (format is BookFormat.Fb2 or BookFormat.Both)
            {
                string path = Path.Combine(stage, "book.fb2");
                WriteFb2(path, book, token);
                ValidateFb2(path, book, token);
                files.Add(StagedFile.Create(path, ".fb2", token));
            }
            token.ThrowIfCancellationRequested();
            PublicationGate.Wait(token);
            try { return Publish(files, directory, SafeName(book.Title), token); }
            finally { PublicationGate.Release(); }
        }
        finally
        {
            // This randomly named directory was created by this invocation only.
            // Never recursively remove directories or unrelated output files.
            foreach (string name in new[] { "book.epub", "book.fb2" })
            {
                try { File.Delete(Path.Combine(stage, name)); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            try { Directory.Delete(stage); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static BookDocument Snapshot(BookDocument book, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(book.Title) || book.PageCount < 1 || book.Paragraphs is null ||
            book.Paragraphs.Count == 0 || !book.Paragraphs.Any(p => !string.IsNullOrWhiteSpace(p)))
            throw new InvalidDataException("Нельзя экспортировать пустой или незавершённый рассказ.");
        VerifySource(book.SourceUrl);
        var paragraphs = book.Paragraphs.ToArray();
        VerifyText(paragraphs.Prepend(book.Title).Prepend(book.Author ?? ""), token);
        BookChapter[]? chapters = null;
        if (book.Chapters is not null)
        {
            if (book.Chapters.Count == 0) throw new InvalidDataException("В книге не указаны главы.");
            chapters = book.Chapters.Select(chapter =>
            {
                token.ThrowIfCancellationRequested();
                if (chapter is null || string.IsNullOrWhiteSpace(chapter.Title) || chapter.Paragraphs is null ||
                    chapter.Paragraphs.Count == 0 || !chapter.Paragraphs.Any(p => !string.IsNullOrWhiteSpace(p)))
                    throw new InvalidDataException("Глава должна содержать название и текст.");
                VerifySource(chapter.SourceUrl);
                var text = chapter.Paragraphs.ToArray();
                VerifyText(text.Prepend(chapter.Title), token);
                return chapter with { Paragraphs = Array.AsReadOnly(text) };
            }).ToArray();
            if (!paragraphs.SequenceEqual(chapters.SelectMany(chapter => chapter.Paragraphs), StringComparer.Ordinal))
                throw new InvalidDataException("Общий текст книги не соответствует тексту и порядку её глав.");
        }
        return book with { Paragraphs = Array.AsReadOnly(paragraphs), Chapters = chapters is null ? null : Array.AsReadOnly(chapters) };
    }

    private static void VerifySource(Uri? source)
    {
        if (source is null || !source.IsAbsoluteUri || source.Scheme is not ("http" or "https"))
            throw new InvalidDataException("Источник должен быть HTTP- или HTTPS-ссылкой.");
        XmlConvert.VerifyXmlChars(source.AbsoluteUri);
    }

    private static void VerifyText(IEnumerable<string?> values, CancellationToken token)
    {
        foreach (string? value in values)
        {
            token.ThrowIfCancellationRequested();
            if (value is null) throw new InvalidDataException("В тексте обнаружен пустой элемент.");
            XmlConvert.VerifyXmlChars(value);
        }
    }

    private sealed record ContentSection(string Id, string Href, string Title, Uri? SourceUrl, IReadOnlyList<string> Paragraphs);

    private static ContentSection[] ContentSections(BookDocument book) => book.Chapters is null
        ? [new("text", "text.xhtml", "Текст рассказа", null, book.Paragraphs)]
        : book.Chapters.Select((chapter, index) => new ContentSection(
            "chapter" + (index + 1).ToString("D4", CultureInfo.InvariantCulture),
            "chapter" + (index + 1).ToString("D4", CultureInfo.InvariantCulture) + ".xhtml",
            chapter.Title, chapter.SourceUrl, chapter.Paragraphs)).ToArray();

    private sealed record StagedFile(string Path, string Extension, long Length, string Hash)
    {
        public static StagedFile Create(string path, string extension, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using var stream = File.OpenRead(path);
            return new(path, extension, stream.Length, Convert.ToHexString(SHA256.HashData(stream)));
        }
    }

    private static IReadOnlyList<string> Publish(List<StagedFile> files, string directory, string name,
        CancellationToken token)
    {
        for (int suffix = 1; suffix <= 10000; suffix++)
        {
            token.ThrowIfCancellationRequested();
            string stem = suffix == 1 ? name : $"{name} ({suffix})";
            string[] targets = files.Select(file => Path.Combine(directory, stem + file.Extension)).ToArray();
            if (targets.Any(path => File.Exists(path) || Directory.Exists(path))) continue;
            var moved = new List<(StagedFile File, string Target)>();
            try
            {
                for (int index = 0; index < files.Count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    // File.Move's exclusive destination check also covers external collision races.
                    File.Move(files[index].Path, targets[index], overwrite: false);
                    moved.Add((files[index], targets[index]));
                }
                token.ThrowIfCancellationRequested();
                return Array.AsReadOnly(targets);
            }
            catch (Exception error)
            {
                bool collision = error is IOException && targets.Except(moved.Select(item => item.Target))
                    .Any(path => File.Exists(path) || Directory.Exists(path));
                try { RollBack(moved); }
                catch (Exception rollback)
                {
                    throw new AggregateException("Не удалось полностью отменить публикацию. Проверьте файлы в папке результата.", error, rollback);
                }
                if (!collision) throw;
            }
        }
        throw new IOException("Не удалось подобрать свободное имя книги.");
    }

    private static void RollBack(List<(StagedFile File, string Target)> moved)
    {
        List<Exception>? errors = null;
        foreach (var item in moved.AsEnumerable().Reverse())
        {
            try
            {
                // A changed file is no longer ours to remove. Preserve it and report the problem.
                using (var stream = File.OpenRead(item.Target))
                    if (stream.Length != item.File.Length || Convert.ToHexString(SHA256.HashData(stream)) != item.File.Hash)
                        throw new IOException("Опубликованный файл изменён другим процессом: " + item.Target);
                File.Move(item.Target, item.File.Path, overwrite: false);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (Exception error) { (errors ??= []).Add(error); }
        }
        if (errors is not null) throw new AggregateException(errors);
    }

    private static string SafeName(string title)
    {
        char[] invalid = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];
        string value = new(title.Select(c => c < 32 || invalid.Contains(c) ? '_' : c).ToArray());
        value = value.Trim().TrimEnd('.', ' ');
        if (value.Length > 100) value = value[..(char.IsHighSurrogate(value[99]) ? 99 : 100)].TrimEnd('.', ' ');
        if (value.Length == 0) value = "Рассказ";
        string first = value.Split('.')[0].ToUpperInvariant();
        if (first is "CON" or "PRN" or "AUX" or "NUL" ||
            System.Text.RegularExpressions.Regex.IsMatch(first, @"^(COM|LPT)[1-9¹²³]$")) value = "_" + value;
        return value;
    }

    private static string Identifier(BookDocument book) => "urn:sha256:" +
        Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(book.SourceUrl.AbsoluteUri + "\0" + book.Title))).ToLowerInvariant();

    private static XDocument HtmlDocument(string title, params object[] content) => new(new XDeclaration("1.0", "utf-8", null),
        new XElement(Html + "html", new XAttribute("lang", "ru"), new XAttribute(XNamespace.Xml + "lang", "ru"),
            new XElement(Html + "head", new XElement(Html + "title", title)),
            new XElement(Html + "body", content)));

    private static void WriteXml(Stream stream, XDocument document)
    {
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings
        {
            Encoding = Utf8, Indent = false, CloseOutput = false, CheckCharacters = true,
            NewLineHandling = NewLineHandling.Entitize,
        });
        document.Save(writer);
    }

    private static void WriteEpub(string path, BookDocument book, CancellationToken token)
    {
        string uid = Identifier(book);
        XElement P(string value) => new(Html + "p", value);
        var titleBody = new List<object> { new XElement(Html + "h1", book.Title) };
        if (!string.IsNullOrWhiteSpace(book.Author)) titleBody.Add(P(book.Author));
        titleBody.Add(new XElement(Html + "p", "Источник: ", new XElement(Html + "a",
            new XAttribute("href", book.SourceUrl.AbsoluteUri), book.SourceUrl.AbsoluteUri)));
        titleBody.Add(P($"Загружено страниц источника: {book.PageCount}"));
        var sections = ContentSections(book);
        if (book.Chapters is not null) titleBody.Add(P($"Глав: {sections.Length}"));
        var metadata = new XElement(Opf + "metadata", new XAttribute(XNamespace.Xmlns + "dc", Dc.NamespaceName),
            new XElement(Dc + "identifier", new XAttribute("id", "book-id"), uid),
            new XElement(Dc + "title", book.Title), new XElement(Dc + "language", "ru"),
            new XElement(Dc + "source", book.SourceUrl.AbsoluteUri));
        if (!string.IsNullOrWhiteSpace(book.Author)) metadata.Add(new XElement(Dc + "creator", book.Author));
        metadata.Add(new XElement(Opf + "meta", new XAttribute("property", "dcterms:modified"),
            DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)));
        XElement Item(string id, string href, string type, string? properties = null) => new(Opf + "item",
            new XAttribute("id", id), new XAttribute("href", href), new XAttribute("media-type", type),
            properties is null ? null : new XAttribute("properties", properties));
        var manifest = new XElement(Opf + "manifest", Item("title", "title.xhtml", "application/xhtml+xml"),
            sections.Select(section => Item(section.Id, section.Href, "application/xhtml+xml")),
            Item("nav", "nav.xhtml", "application/xhtml+xml", "nav"), Item("ncx", "toc.ncx", "application/x-dtbncx+xml"));
        var spine = new XElement(Opf + "spine", new XAttribute("toc", "ncx"),
            new XElement(Opf + "itemref", new XAttribute("idref", "title")));
        // Multi-part books expose their chapter list in ordinary reading order,
        // as well as through the reader's EPUB/NCX navigation controls.
        if (book.Chapters is not null) spine.Add(new XElement(Opf + "itemref", new XAttribute("idref", "nav")));
        spine.Add(sections.Select(section => new XElement(Opf + "itemref", new XAttribute("idref", section.Id))));
        var package = new XDocument(new XElement(Opf + "package", new XAttribute("version", "3.0"),
            new XAttribute("unique-identifier", "book-id"), metadata, manifest, spine));
        var navigationSections = book.Chapters is null
            ? new[] { new ContentSection("title", "title.xhtml", book.Title, null, Array.Empty<string>()) }.Concat(sections).ToArray()
            : sections;
        var navigation = HtmlDocument("Оглавление", new XElement(Html + "nav", new XAttribute(Epub + "type", "toc"),
            new XAttribute("id", "toc"), new XElement(Html + "h1", "Оглавление"),
            new XElement(Html + "ol", navigationSections.Select(section => new XElement(Html + "li",
                new XElement(Html + "a", new XAttribute("href", section.Href), section.Title))))));
        XElement NcxMeta(string name, string content) => new(Ncx + "meta", new XAttribute("name", name), new XAttribute("content", content));
        XElement Point(string id, string href, string label, int order) => new(Ncx + "navPoint", new XAttribute("id", id),
            new XAttribute("playOrder", order), new XElement(Ncx + "navLabel", new XElement(Ncx + "text", label)),
            new XElement(Ncx + "content", new XAttribute("src", href)));
        var ncx = new XDocument(new XElement(Ncx + "ncx", new XAttribute("version", "2005-1"),
            new XElement(Ncx + "head", NcxMeta("dtb:uid", uid), NcxMeta("dtb:depth", "1"),
                NcxMeta("dtb:totalPageCount", "0"), NcxMeta("dtb:maxPageNumber", "0")),
            new XElement(Ncx + "docTitle", new XElement(Ncx + "text", book.Title)),
            new XElement(Ncx + "navMap", navigationSections.Select((section, index) =>
                Point(section.Id, section.Href, section.Title, index + 1)))));
        var container = new XDocument(new XElement(Container + "container", new XAttribute("version", "1.0"),
            new XElement(Container + "rootfiles", new XElement(Container + "rootfile", new XAttribute("full-path", "EPUB/package.opf"),
                new XAttribute("media-type", "application/oebps-package+xml")))));
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true, Utf8))
        {
            using (var mime = zip.CreateEntry("mimetype", CompressionLevel.NoCompression).Open())
                mime.Write(Utf8.GetBytes("application/epub+zip"));
            void Add(string name, XDocument document)
            {
                token.ThrowIfCancellationRequested();
                using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                WriteXml(stream, document);
            }
            Add("META-INF/container.xml", container);
            Add("EPUB/package.opf", package);
            Add("EPUB/nav.xhtml", navigation);
            Add("EPUB/toc.ncx", ncx);
            Add("EPUB/title.xhtml", HtmlDocument(book.Title, titleBody.ToArray()));
            foreach (var section in sections)
            {
                var text = new XElement(Html + "section", new XAttribute("id", section.Id));
                var heading = new XElement(Html + "h1", section.Title);
                if (section.SourceUrl is null) text.Add(heading);
                else text.Add(new XElement(Html + "header", heading, new XElement(Html + "a",
                    new XAttribute("href", section.SourceUrl.AbsoluteUri), "Источник главы")));
                foreach (string paragraph in section.Paragraphs)
                {
                    token.ThrowIfCancellationRequested();
                    text.Add(P(paragraph));
                }
                var document = HtmlDocument(section.Title, text);
                if (section.SourceUrl is not null) document.Root!.Element(Html + "head")!.Add(new XElement(Html + "meta",
                    new XAttribute("name", "dc.source"), new XAttribute("content", section.SourceUrl.AbsoluteUri)));
                Add("EPUB/" + section.Href, document);
            }
        }
        file.Flush(flushToDisk: true);
    }

    private static void WriteFb2(string path, BookDocument book, CancellationToken token)
    {
        XElement Author(string? value) => new(Fb + "author", new XElement(Fb + "nickname",
            string.IsNullOrWhiteSpace(value) ? "Неизвестный автор" : value));
        string date = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var sections = ContentSections(book).Select(section =>
        {
            var element = new XElement(Fb + "section", new XAttribute("id", section.Id),
                new XElement(Fb + "title", new XElement(Fb + "p", section.Title)));
            if (section.SourceUrl is not null) element.Add(new XElement(Fb + "annotation", new XElement(Fb + "p",
                new XElement(Fb + "a", new XAttribute(Link + "href", section.SourceUrl.AbsoluteUri), "Источник главы"))));
            foreach (string paragraph in section.Paragraphs)
            {
                token.ThrowIfCancellationRequested();
                element.Add(new XElement(Fb + "p", paragraph));
            }
            return element;
        }).ToArray();
        var document = new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement(Fb + "FictionBook",
            book.Chapters is null ? null : new XAttribute(XNamespace.Xmlns + "l", Link.NamespaceName),
            new XElement(Fb + "description", new XElement(Fb + "title-info", new XElement(Fb + "genre", "unrecognised"),
                Author(book.Author), new XElement(Fb + "book-title", book.Title), new XElement(Fb + "lang", "ru")),
                new XElement(Fb + "document-info", Author("Text Downloader"), new XElement(Fb + "program-used", "Text Downloader"),
                    new XElement(Fb + "date", new XAttribute("value", date), date),
                    new XElement(Fb + "src-url", book.SourceUrl.AbsoluteUri),
                    book.Chapters?.Select(chapter => new XElement(Fb + "src-url", chapter.SourceUrl.AbsoluteUri)),
                    new XElement(Fb + "id", Identifier(book)),
                    new XElement(Fb + "version", "1.0"))),
            new XElement(Fb + "body", new XElement(Fb + "title", new XElement(Fb + "p", book.Title)), sections)));
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        WriteXml(stream, document);
        stream.Flush(flushToDisk: true);
    }

    private static XDocument ReadXml(Stream stream)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    private static void ValidateEpub(string path, BookDocument book, CancellationToken token)
    {
        using (var file = File.OpenRead(path))
        using (var reader = new BinaryReader(file))
        {
            if (reader.ReadUInt32() != 0x04034b50) throw new InvalidDataException("Повреждён заголовок EPUB.");
            file.Position = 8;
            if (reader.ReadUInt16() != 0) throw new InvalidDataException("EPUB mimetype должен быть без сжатия.");
            file.Position = 26;
            if (reader.ReadUInt16() != 8 || reader.ReadUInt16() != 0)
                throw new InvalidDataException("Некорректный EPUB mimetype.");
        }
        using var zip = ZipFile.OpenRead(path);
        var sections = ContentSections(book);
        var expectedFiles = new[] { "mimetype", "META-INF/container.xml", "EPUB/package.opf", "EPUB/nav.xhtml", "EPUB/toc.ncx", "EPUB/title.xhtml" }
            .Concat(sections.Select(section => "EPUB/" + section.Href)).ToHashSet(StringComparer.Ordinal);
        if (zip.Entries.Count != expectedFiles.Count || zip.Entries[0].FullName != "mimetype" ||
            !expectedFiles.SetEquals(zip.Entries.Select(entry => entry.FullName)))
            throw new InvalidDataException("Неверный состав EPUB.");
        using (var mime = new StreamReader(zip.Entries[0].Open(), Utf8))
            if (mime.ReadToEnd() != "application/epub+zip") throw new InvalidDataException("Неверный EPUB mimetype.");
        XDocument Read(string name)
        {
            token.ThrowIfCancellationRequested();
            using var stream = (zip.GetEntry(name) ?? throw new InvalidDataException("В EPUB отсутствует " + name)).Open();
            return ReadXml(stream);
        }
        if (Read("META-INF/container.xml").Descendants(Container + "rootfile").Single().Attribute("full-path")?.Value != "EPUB/package.opf")
            throw new InvalidDataException("Неверный путь EPUB package.");
        var package = Read("EPUB/package.opf");
        var manifest = package.Descendants(Opf + "item").ToDictionary(item => item.Attribute("id")!.Value);
        var expectedManifest = new Dictionary<string, string>(StringComparer.Ordinal)
            { ["title"] = "title.xhtml", ["nav"] = "nav.xhtml", ["ncx"] = "toc.ncx" };
        foreach (var section in sections) expectedManifest.Add(section.Id, section.Href);
        if (package.Root?.Attribute("version")?.Value != "3.0" || manifest.Count != expectedManifest.Count ||
            expectedManifest.Any(item => !manifest.TryGetValue(item.Key, out var actual) || actual.Attribute("href")?.Value != item.Value))
            throw new InvalidDataException("Неверный EPUB manifest.");
        foreach (var item in manifest.Values)
            if (zip.GetEntry("EPUB/" + item.Attribute("href")?.Value) is null) throw new InvalidDataException("EPUB manifest ссылается на отсутствующий файл.");
        var expectedSpine = new[] { "title" }.Concat(book.Chapters is null ? Array.Empty<string>() : new[] { "nav" })
            .Concat(sections.Select(section => section.Id));
        if (!package.Descendants(Opf + "itemref").Select(item => item.Attribute("idref")?.Value).SequenceEqual(expectedSpine) ||
            manifest["nav"].Attribute("properties")?.Value != "nav" || manifest["ncx"].Attribute("media-type")?.Value != "application/x-dtbncx+xml")
            throw new InvalidDataException("Неверный порядок EPUB spine или оглавления.");
        if (package.Descendants(Dc + "title").Single().Value != book.Title ||
            package.Descendants(Dc + "source").Single().Value != book.SourceUrl.AbsoluteUri ||
            package.Descendants(Dc + "language").Single().Value != "ru" ||
            package.Descendants(Dc + "identifier").Single().Value != Identifier(book) ||
            !package.Descendants(Dc + "creator").Select(author => author.Value).SequenceEqual(
                string.IsNullOrWhiteSpace(book.Author) ? Array.Empty<string>() : new[] { book.Author }))
            throw new InvalidDataException("EPUB не сохранил метаданные книги.");
        var expectedNavigation = book.Chapters is null
            ? new[] { new ContentSection("title", "title.xhtml", book.Title, null, Array.Empty<string>()) }.Concat(sections).ToArray()
            : sections;
        var nav = Read("EPUB/nav.xhtml");
        var links = nav.Descendants(Html + "a").ToArray();
        var toc = nav.Descendants(Html + "nav").Single();
        if (toc.Attribute(Epub + "type")?.Value != "toc" || toc.Element(Html + "ol") is null ||
            !links.Select(link => link.Attribute("href")?.Value).SequenceEqual(expectedNavigation.Select(section => section.Href)) ||
            !links.Select(link => link.Value).SequenceEqual(expectedNavigation.Select(section => section.Title)))
            throw new InvalidDataException("EPUB не сохранил порядок и названия глав в оглавлении.");
        var ncx = Read("EPUB/toc.ncx");
        var points = ncx.Descendants(Ncx + "navPoint").ToArray();
        if (!points.Select(point => point.Element(Ncx + "content")?.Attribute("src")?.Value).SequenceEqual(expectedNavigation.Select(section => section.Href)) ||
            !points.Select(point => point.Element(Ncx + "navLabel")?.Element(Ncx + "text")?.Value).SequenceEqual(expectedNavigation.Select(section => section.Title)) ||
            !points.Select(point => point.Attribute("playOrder")?.Value).SequenceEqual(Enumerable.Range(1, points.Length).Select(number => number.ToString(CultureInfo.InvariantCulture))) ||
            !points.Select(point => point.Attribute("id")?.Value).SequenceEqual(expectedNavigation.Select(section => section.Id)) ||
            ncx.Descendants(Ncx + "docTitle").Single().Element(Ncx + "text")?.Value != book.Title ||
            ncx.Descendants(Ncx + "meta").Single(meta => meta.Attribute("name")?.Value == "dtb:uid").Attribute("content")?.Value != Identifier(book))
            throw new InvalidDataException("Неверное оглавление NCX.");
        var fullText = new List<string>();
        foreach (var section in sections)
        {
            var document = Read("EPUB/" + section.Href);
            var content = document.Root!.Element(Html + "body")!.Elements(Html + "section").Single();
            var paragraphs = content.Elements(Html + "p").Select(p => p.Value).ToArray();
            if (document.Root.Element(Html + "head")?.Element(Html + "title")?.Value != section.Title ||
                content.Attribute("id")?.Value != section.Id || content.Descendants(Html + "h1").Single().Value != section.Title ||
                !paragraphs.SequenceEqual(section.Paragraphs))
                throw new InvalidDataException("EPUB не сохранил название или полный текст главы.");
            if (section.SourceUrl is not null &&
                (document.Root.Element(Html + "head")?.Elements(Html + "meta").Single(meta => meta.Attribute("name")?.Value == "dc.source").Attribute("content")?.Value != section.SourceUrl.AbsoluteUri ||
                 content.Element(Html + "header")?.Element(Html + "a")?.Attribute("href")?.Value != section.SourceUrl.AbsoluteUri))
                throw new InvalidDataException("EPUB не сохранил исходную ссылку главы.");
            fullText.AddRange(paragraphs);
        }
        if (Read("EPUB/title.xhtml").Descendants(Html + "h1").Single().Value != book.Title || !fullText.SequenceEqual(book.Paragraphs))
            throw new InvalidDataException("EPUB не сохранил полный исходный текст.");
    }

    private static void ValidateFb2(string path, BookDocument book, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var stream = File.OpenRead(path);
        var document = ReadXml(stream);
        var expected = ContentSections(book);
        var body = document.Root?.Element(Fb + "body");
        var sections = body?.Elements(Fb + "section").ToArray() ?? [];
        var sources = new[] { book.SourceUrl.AbsoluteUri }.Concat(book.Chapters?.Select(chapter => chapter.SourceUrl.AbsoluteUri) ?? Array.Empty<string>());
        if (document.Root?.Name != Fb + "FictionBook" ||
            document.Descendants(Fb + "book-title").Single().Value != book.Title ||
            document.Descendants(Fb + "lang").Single().Value != "ru" ||
            document.Descendants(Fb + "title-info").Single().Element(Fb + "author")?.Element(Fb + "nickname")?.Value !=
                (string.IsNullOrWhiteSpace(book.Author) ? "Неизвестный автор" : book.Author) ||
            document.Descendants(Fb + "id").Single().Value != Identifier(book) ||
            body?.Element(Fb + "title")?.Element(Fb + "p")?.Value != book.Title ||
            !document.Descendants(Fb + "src-url").Select(source => source.Value).SequenceEqual(sources) ||
            sections.Length != expected.Length ||
            !sections.Select(section => section.Attribute("id")?.Value).SequenceEqual(expected.Select(section => section.Id)) ||
            !sections.Select(section => section.Element(Fb + "title")?.Element(Fb + "p")?.Value).SequenceEqual(expected.Select(section => section.Title)) ||
            !sections.SelectMany(section => section.Elements(Fb + "p")).Select(p => p.Value).SequenceEqual(book.Paragraphs))
            throw new InvalidDataException("FB2 не сохранил полный исходный текст.");
        for (int index = 0; index < sections.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            if (!sections[index].Elements(Fb + "p").Select(p => p.Value).SequenceEqual(expected[index].Paragraphs) ||
                (expected[index].SourceUrl is not null && sections[index].Element(Fb + "annotation")?.Element(Fb + "p")?
                    .Element(Fb + "a")?.Attribute(Link + "href")?.Value != expected[index].SourceUrl!.AbsoluteUri))
                throw new InvalidDataException("FB2 не сохранил текст или ссылку отдельной главы.");
        }
    }
}
