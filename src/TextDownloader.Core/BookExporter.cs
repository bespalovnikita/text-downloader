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
        if (string.IsNullOrWhiteSpace(book.Title) || book.PageCount < 1 || book.Paragraphs is null ||
            book.Paragraphs.Count == 0 || !book.Paragraphs.Any(p => !string.IsNullOrWhiteSpace(p)))
            throw new InvalidDataException("Нельзя экспортировать пустой или незавершённый рассказ.");
        if (book.SourceUrl is null || !book.SourceUrl.IsAbsoluteUri || book.SourceUrl.Scheme is not ("http" or "https"))
            throw new InvalidDataException("Источник должен быть HTTP- или HTTPS-ссылкой.");
        var paragraphs = book.Paragraphs.ToArray();
        foreach (string? value in paragraphs.Prepend(book.Title).Prepend(book.Author ?? ""))
        {
            token.ThrowIfCancellationRequested();
            if (value is null) throw new InvalidDataException("В тексте обнаружен пустой элемент.");
            XmlConvert.VerifyXmlChars(value);
        }
        book = book with { Paragraphs = paragraphs };
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
        var text = new XElement(Html + "section", new XAttribute("id", "text"), new XElement(Html + "h1", "Текст рассказа"));
        foreach (string paragraph in book.Paragraphs)
        {
            token.ThrowIfCancellationRequested();
            text.Add(P(paragraph));
        }
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
        var package = new XDocument(new XElement(Opf + "package", new XAttribute("version", "3.0"),
            new XAttribute("unique-identifier", "book-id"), metadata,
            new XElement(Opf + "manifest", Item("title", "title.xhtml", "application/xhtml+xml"),
                Item("text", "text.xhtml", "application/xhtml+xml"), Item("nav", "nav.xhtml", "application/xhtml+xml", "nav"),
                Item("ncx", "toc.ncx", "application/x-dtbncx+xml")),
            new XElement(Opf + "spine", new XAttribute("toc", "ncx"),
                new XElement(Opf + "itemref", new XAttribute("idref", "title")),
                new XElement(Opf + "itemref", new XAttribute("idref", "text")))));
        var navigation = HtmlDocument("Оглавление", new XElement(Html + "nav", new XAttribute(Epub + "type", "toc"),
            new XAttribute("id", "toc"), new XElement(Html + "h1", "Оглавление"),
            new XElement(Html + "ol", new XElement(Html + "li", new XElement(Html + "a", new XAttribute("href", "title.xhtml"), book.Title)),
                new XElement(Html + "li", new XElement(Html + "a", new XAttribute("href", "text.xhtml"), "Текст рассказа")))));
        XElement NcxMeta(string name, string content) => new(Ncx + "meta", new XAttribute("name", name), new XAttribute("content", content));
        XElement Point(string id, string href, string label, int order) => new(Ncx + "navPoint", new XAttribute("id", id),
            new XAttribute("playOrder", order), new XElement(Ncx + "navLabel", new XElement(Ncx + "text", label)),
            new XElement(Ncx + "content", new XAttribute("src", href)));
        var ncx = new XDocument(new XElement(Ncx + "ncx", new XAttribute("version", "2005-1"),
            new XElement(Ncx + "head", NcxMeta("dtb:uid", uid), NcxMeta("dtb:depth", "1"),
                NcxMeta("dtb:totalPageCount", "0"), NcxMeta("dtb:maxPageNumber", "0")),
            new XElement(Ncx + "docTitle", new XElement(Ncx + "text", book.Title)),
            new XElement(Ncx + "navMap", Point("title", "title.xhtml", book.Title, 1), Point("text", "text.xhtml", "Текст рассказа", 2))));
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
            Add("EPUB/text.xhtml", HtmlDocument("Текст рассказа", text));
        }
        file.Flush(flushToDisk: true);
    }

    private static void WriteFb2(string path, BookDocument book, CancellationToken token)
    {
        XElement Author(string? value) => new(Fb + "author", new XElement(Fb + "nickname",
            string.IsNullOrWhiteSpace(value) ? "Неизвестный автор" : value));
        string date = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var section = new XElement(Fb + "section", new XAttribute("id", "text"),
            new XElement(Fb + "title", new XElement(Fb + "p", "Текст рассказа")));
        foreach (string paragraph in book.Paragraphs)
        {
            token.ThrowIfCancellationRequested();
            section.Add(new XElement(Fb + "p", paragraph));
        }
        var document = new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement(Fb + "FictionBook",
            new XElement(Fb + "description", new XElement(Fb + "title-info", new XElement(Fb + "genre", "unrecognised"),
                Author(book.Author), new XElement(Fb + "book-title", book.Title), new XElement(Fb + "lang", "ru")),
                new XElement(Fb + "document-info", Author("Text Downloader"), new XElement(Fb + "program-used", "Text Downloader"),
                    new XElement(Fb + "date", new XAttribute("value", date), date),
                    new XElement(Fb + "src-url", book.SourceUrl.AbsoluteUri), new XElement(Fb + "id", Identifier(book)),
                    new XElement(Fb + "version", "1.0"))),
            new XElement(Fb + "body", new XElement(Fb + "title", new XElement(Fb + "p", book.Title)), section)));
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
        if (zip.Entries.Count != 7 || zip.Entries[0].FullName != "mimetype")
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
        foreach (var item in manifest.Values)
            if (zip.GetEntry("EPUB/" + item.Attribute("href")?.Value) is null) throw new InvalidDataException("EPUB manifest ссылается на отсутствующий файл.");
        foreach (var item in package.Descendants(Opf + "itemref"))
            if (!manifest.ContainsKey(item.Attribute("idref")?.Value ?? "")) throw new InvalidDataException("Неверный EPUB spine.");
        foreach (var link in Read("EPUB/nav.xhtml").Descendants(Html + "a"))
            if (zip.GetEntry("EPUB/" + link.Attribute("href")?.Value) is null) throw new InvalidDataException("Неверная ссылка в оглавлении EPUB.");
        foreach (var link in Read("EPUB/toc.ncx").Descendants(Ncx + "content"))
            if (zip.GetEntry("EPUB/" + link.Attribute("src")?.Value) is null) throw new InvalidDataException("Неверная ссылка в NCX.");
        if (Read("EPUB/title.xhtml").Descendants(Html + "h1").Single().Value != book.Title ||
            !Read("EPUB/text.xhtml").Descendants(Html + "p").Select(p => p.Value).SequenceEqual(book.Paragraphs))
            throw new InvalidDataException("EPUB не сохранил полный исходный текст.");
    }

    private static void ValidateFb2(string path, BookDocument book, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var stream = File.OpenRead(path);
        var document = ReadXml(stream);
        if (document.Root?.Name != Fb + "FictionBook" ||
            document.Descendants(Fb + "book-title").Single().Value != book.Title ||
            document.Descendants(Fb + "src-url").Single().Value != book.SourceUrl.AbsoluteUri ||
            !document.Descendants(Fb + "section").Single().Elements(Fb + "p").Select(p => p.Value).SequenceEqual(book.Paragraphs))
            throw new InvalidDataException("FB2 не сохранил полный исходный текст.");
    }
}
