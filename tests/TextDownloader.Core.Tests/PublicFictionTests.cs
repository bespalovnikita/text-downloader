using System.Globalization;
using System.Net;
using System.Xml.Linq;
using TextDownloader.Core;

internal static class PublicFictionTests
{
    // The archive's title listing is intermittently unavailable from hosted CI.
    // Each run still selects a public work and fetches its live chapter over HTTP.
    private static readonly int[] PsychficPublicWorks = [3413, 4238, 4376];

    private static readonly PublicFictionSite Example = new("Example Fiction", "https://fiction.example/library",
        "fiction.example", @"/book/(?<work>[0-9]+)", @"/book/(?<work>[0-9]+)/read/(?<part>[0-9]+)", "//*[@id='prose']")
    {
        Contents = "//ol[@id='contents']/li/a",
        ChapterTitle = "//h2",
        WorkFromChapter = "/book/${work}"
    };

    public static async Task VerifyBookAndCacheAsync()
    {
        using var temp = new TempDirectory();
        using var http = new HttpClient(new FixtureHandler(new Dictionary<string, string>
        {
            ["/book/7"] = "<h1>Синтетическая книга</h1><ol id='contents'><li><a href='/book/7/read/4'>Один</a></li>" +
                "<li><a href='/book/7/read/56'>Два</a></li></ol><div id='prose'>Аннотация</div>",
            ["/book/7/read/4"] = "<h2>Первая глава</h2><div id='prose'><p>Это <b>первый</b> абзац.</p>" +
                "<aside>Служебный текст.</aside><p>Второй&nbsp;абзац.</p></div>",
            ["/book/7/read/56"] = "<h2>Вторая глава</h2><div id='prose'><p>Последняя строка.</p></div>"
        }));
        using var router = new SupportedStorySource([new PublicFictionSource(Example, TimeSpan.Zero, new PageRequestThrottle(), http: http)]);
        var downloader = new StoryDownloader(router, temp.Path);
        var supplied = new Uri("https://fiction.example/book/7/read/56?from=home#read");
        var preview = await downloader.PreviewAsync(supplied, false, default);
        Require(StoryDownloader.JoinParagraphs([preview]).SequenceEqual(["Последняя строка."]), "Chapter preview");
        var book = await downloader.DownloadAsync(supplied, false, false, null, default);
        Require(book.Title == "Синтетическая книга" && book.PageCount == 2, "Complete book and work title");
        Require(book.Chapters!.Select(x => x.Title).SequenceEqual(["Первая глава", "Вторая глава"]), "Chapter order");
        Require(book.Paragraphs.SequenceEqual(["Это первый абзац.", "Второй абзац.", "Последняя строка."]), "Only chapter prose");

        using var indexOnlyHttp = new HttpClient(new FixtureHandler(new Dictionary<string, string>
        {
            ["/book/7"] = "<h1>Синтетическая книга</h1><ol id='contents'><li><a href='/book/7/read/4'>Один</a></li>" +
                "<li><a href='/book/7/read/56'>Два</a></li></ol>"
        }));
        using var cachedRouter = new SupportedStorySource(
            [new PublicFictionSource(Example, TimeSpan.Zero, new PageRequestThrottle(), http: indexOnlyHttp)]);
        var cached = await new StoryDownloader(cachedRouter, temp.Path).DownloadAsync(supplied, false, false, null, default);
        Require(cached.Paragraphs.SequenceEqual(book.Paragraphs), "Verified chapters reused from cache");
        Require(!router.CanHandle(new Uri("https://fiction.example.evil/book/7")), "Host suffix rejected");
        Require(!router.CanHandle(new Uri("https://user@fiction.example/book/7")), "Userinfo rejected");
        Require(!router.CanHandle(new Uri("https://fiction.example:9443/book/7")), "Unexpected port rejected");
    }

    public static async Task RejectForeignChapterAsync()
    {
        using var http = new HttpClient(new FixtureHandler(new Dictionary<string, string>
        {
            ["/book/7"] = "<h1>Книга</h1><ol id='contents'><li><a href='/book/8/read/4'>Чужая глава</a></li></ol>"
        }));
        using var source = new PublicFictionSource(Example, TimeSpan.Zero, new PageRequestThrottle(), http: http);
        try { await source.GetPageAsync(new Uri("https://fiction.example/book/7"), false, default); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("A chapter from another work was accepted.");
    }

    public static async Task<int> RunLiveAsync(string[] filter)
    {
        var registered = PublicFictionSites.All.ToArray();
        Require(registered.Select(site => site.Host).Distinct(StringComparer.OrdinalIgnoreCase).Count() == registered.Length,
            "Each registered public-fiction host must belong to only one batch.");
        var batches = PublicFictionSites.Batches;
        PublicFictionSite[] sites;
        if (filter is ["--batch", var value] &&
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) &&
            batches.TryGetValue(number, out var batch))
            sites = batch;
        else if (filter is ["--latest-batch"])
            sites = batches[batches.Keys.Max()];
        else
            sites = PublicFictionSites.All.Where(site => filter.Length == 0 ||
                filter.Contains(site.Host, StringComparer.OrdinalIgnoreCase)).ToArray();

        bool byBatch = filter.FirstOrDefault() is "--batch" or "--latest-batch";
        if (byBatch ? sites.Length != 10 : sites.Length == 0 ||
            (filter.Length > 0 && sites.Length != filter.Distinct(StringComparer.OrdinalIgnoreCase).Count()))
        {
            Console.Error.WriteLine("Expected ten sites in the requested batch or a list of supported hostnames.");
            return 2;
        }

        int seed = Random.Shared.Next();
        Console.WriteLine($"Catalog selection seed {seed}.");
        var factory = SupportedStorySource.CreateLoggedFactory();
        var results = new bool[sites.Length];
        await Parallel.ForEachAsync(Enumerable.Range(0, sites.Length), new ParallelOptions { MaxDegreeOfParallelism = 4 },
            async (i, token) => results[i] = await CheckLiveAsync(sites[i], factory, new Random(seed + i), token));
        Console.WriteLine($"{results.Count(ok => ok)}/{sites.Length} live site checks passed.");
        return results.All(ok => ok) ? 0 : 1;
    }

    private static async Task<bool> CheckLiveAsync(PublicFictionSite site,
        Func<TimeSpan, IProgress<DownloadLogEvent>?, IStorySource> factory, Random random, CancellationToken token)
    {
        var log = new MarkupLog();
        using var router = factory(TimeSpan.FromMilliseconds(350), log);
        using var source = new PublicFictionSource(site, TimeSpan.FromMilliseconds(350), new PageRequestThrottle());
        using var temp = new TempDirectory();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            Uri[] sample = site.Host switch
            {
                "www.psychfic.com" => PsychficPublicWorks.Select(id => new Uri($"https://www.psychfic.com/viewstory.php?sid={id}")).ToArray(),
                "secondlifetranslations.com" =>
                [
                    new Uri("https://secondlifetranslations.com/novel/oitv/"),
                    new Uri("https://secondlifetranslations.com/novel/abc/"),
                    new Uri("https://secondlifetranslations.com/novel/yay/")
                ],
                _ => (await source.ReadCatalogAsync(timeout.Token)).ToArray()
            };
            Require(sample.Length >= 2, "The catalog must supply at least two distinct works.");
            random.Shuffle(sample);
            var failures = new List<string>();
            foreach (var work in sample.Take(6))
            {
                try
                {
                    Require(router.CanHandle(work) && router.Describe(work).Name == site.Name,
                        "The application router did not select the adapter under test.");
                    var page = await new StoryDownloader(router, temp.Path).PreviewAsync(work, false, timeout.Token);
                    var paragraphs = StoryDownloader.JoinParagraphs([page]);
                    Require(router.CanHandle(page.Url) && page.Number == 1 && !string.IsNullOrWhiteSpace(page.Title) &&
                        paragraphs.Sum(p => p.Count(char.IsLetterOrDigit)) >= 50, "The first page has no substantial prose.");
                    var book = new BookDocument(page.Title, page.Author, page.Url, paragraphs, 1);
                    var exported = await BookExporter.ExportAsync(book, temp.Path, BookFormat.Fb2, timeout.Token);
                    var fb2 = XDocument.Load(exported.Single());
                    XNamespace ns = "http://www.gribuser.ru/xml/fictionbook/2.0";
                    Require(fb2.Root!.Element(ns + "body")!.Elements(ns + "section").Elements(ns + "p").Select(p => p.Value)
                        .SequenceEqual(paragraphs), "The exported document did not preserve chapter text.");
                    Console.WriteLine($"PASS {site.Name} | {sample.Length} catalog entries | {page.Url} | {paragraphs.Count} paragraphs");
                    return true;
                }
                catch (Exception error) when (error is HttpRequestException or InvalidDataException)
                {
                    failures.Add($"{work} {error.Message}");
                }
            }
            throw new InvalidDataException(string.Join(Environment.NewLine, failures));
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"FAIL {site.Name} | {error.Message}");
            foreach (var shape in log.Shapes.Take(3)) Console.Error.WriteLine(shape);
            return false;
        }
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidDataException(message);
    }

    private sealed class FixtureHandler(IReadOnlyDictionary<string, string> pages) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            if (!pages.TryGetValue(path, out var html)) throw new InvalidOperationException($"Unexpected request {path}");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html) });
        }
    }

    private sealed class MarkupLog : IProgress<DownloadLogEvent>
    {
        public List<string> Shapes { get; } = [];
        public void Report(DownloadLogEvent value)
        {
            if (value.Message.StartsWith("Разметка текста ", StringComparison.Ordinal))
                Shapes.Add(value.Message);
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("public-fiction-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
