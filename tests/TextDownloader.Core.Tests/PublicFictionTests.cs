using System.Net;
using System.Xml.Linq;
using TextDownloader.Core;

internal static class PublicFictionTests
{
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
        bool latestBatch = filter is ["--latest-batch"];
        var sites = latestBatch ? PublicFictionSites.Batches[^1] : PublicFictionSites.All.Where(site => filter.Length == 0 ||
            filter.Contains(site.Host, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (latestBatch ? sites.Length != 10 : sites.Length == 0 ||
            (filter.Length > 0 && sites.Length != filter.Distinct(StringComparer.OrdinalIgnoreCase).Count()))
        {
            Console.Error.WriteLine("Expected ten sites in the latest batch or a list of supported hostnames.");
            return 2;
        }

        int seed = Random.Shared.Next();
        Console.WriteLine($"Catalog selection seed {seed}.");
        var factory = SupportedStorySource.CreateFactory();
        using var router = factory(TimeSpan.FromMilliseconds(350));
        var results = new bool[sites.Length];
        await Parallel.ForEachAsync(Enumerable.Range(0, sites.Length), new ParallelOptions { MaxDegreeOfParallelism = 4 },
            async (i, token) => results[i] = await CheckLiveAsync(sites[i], router, new Random(seed + i), token));
        Console.WriteLine($"{results.Count(ok => ok)}/{sites.Length} live site checks passed.");
        return results.All(ok => ok) ? 0 : 1;
    }

    private static async Task<bool> CheckLiveAsync(PublicFictionSite site, IStorySource router, Random random, CancellationToken token)
    {
        using var source = new PublicFictionSource(site, TimeSpan.FromMilliseconds(350), new PageRequestThrottle());
        using var temp = new TempDirectory();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            var sample = (await source.ReadCatalogAsync(timeout.Token)).ToArray();
            Require(sample.Length >= 2, "The catalog must supply at least two distinct works.");
            random.Shuffle(sample);
            var failures = new List<string>();
            foreach (var work in sample.Take(6))
            {
                try
                {
                    Require(router.CanHandle(work), "The application router does not recognize the catalog entry.");
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

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("public-fiction-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
