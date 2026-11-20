using System.Net;
using System.Text;
using TextDownloader.Core;

internal static class BestweaponTests
{
    // IDs deliberately do not sort into chapter order: only the site's index
    // establishes the sequence. All article text below is artificial.
    private static readonly int[] Ids = [801, 402, 710, 119, 930, 63, 509, 220];
    private const string SeriesTitle = "Синтетический цикл";
    private const string Author = "Тестовый автор";

    public static async Task PreviewAsync()
    {
        using var handler = new PagesHandler(Pages(8));
        using var http = new HttpClient(handler);
        using var source = new BestweaponSource(http, TimeSpan.Zero);
        foreach (string bad in new[]
        {
            "https://bestweapon.cc.example.org/post_801", "https://user:password@bestweapon.cc/post_801",
            "https://bestweapon.cc:444/post_801", "ftp://bestweapon.cc/post_801",
            "https://bestweapon.cc/post_0", "https://bestweapon.cc/post_-1", "https://bestweapon.cc/post_word",
            "https://bestweapon.cc/path/post_801", "https://bestweapon.cc/post_801/comment", "https://bestweapon.cc/",
        }) Check(!source.CanHandle(new Uri(bad)), "An unsupported Bestweapon URL was accepted: " + bad);
        Check(source.CanHandle(Url(8)), "The selected post URL was rejected.");
        Equal(Url(8), source.NormalizeToFirstPage(new Uri("http://bestweapon.cc/post_220?tracking=1#comments")));
        using (var router = new SupportedStorySource())
        {
            var sefanFirst = new Uri("https://sefan.ru/stories/ru/2/any-genre/plain-story_1.html");
            Check(router.CanHandle(sefanFirst) && router.CanHandle(Url(8)), "Adding Bestweapon removed an existing supported source.");
            Equal(sefanFirst, router.NormalizeToFirstPage(new Uri(sefanFirst.AbsoluteUri.Replace("_1.html", "_1-3.html"))));
            Equal(Url(8), router.NormalizeToFirstPage(Url(8)));
        }
        using var folder = new TemporaryDirectory();
        StoryPage preview = await new StoryDownloader(source, folder.Path)
            .PreviewAsync(Url(8), false, CancellationToken.None);
        Equal(Title(8), preview.Title);
        Equal(Author, preview.Author);
        Check(preview.Fragment.Contains(Paragraphs(8)[0], StringComparison.Ordinal), "Preview changed article text.");
        Check(!preview.Fragment.Contains("OUTSIDE-ARTICLE", StringComparison.Ordinal), "Preview includes recommendations, tags or advertising.");
        Sequence(new[] { Url(8) }, handler.Requests);
    }

    public static async Task OrderedSeriesAsync()
    {
        foreach (int selected in new[] { 8, 7 })
        {
            using var folder = new TemporaryDirectory();
            using var handler = new PagesHandler(Pages(8));
            using var http = new HttpClient(handler);
            using var source = new SupportedStorySource(bestweapon: new BestweaponSource(http, TimeSpan.Zero));
            BookDocument book = await new StoryDownloader(source, folder.Path)
                .DownloadAsync(Url(selected), false, false, null, CancellationToken.None);
            Equal(SeriesTitle, book.Title);
            Equal(Author, book.Author);
            Equal(selected, book.PageCount);
            var chapters = book.Chapters ?? throw new InvalidOperationException("The series was flattened without chapter metadata.");
            Equal(selected, chapters.Count);
            Sequence(Enumerable.Range(1, selected).Select(Title), chapters.Select(chapter => chapter.Title));
            Sequence(Enumerable.Range(1, selected).Select(Url), chapters.Select(chapter => chapter.SourceUrl));
            for (int part = 1; part <= selected; part++) Sequence(Paragraphs(part), chapters[part - 1].Paragraphs);
            Sequence(Enumerable.Range(1, selected).SelectMany(part => Paragraphs(part)), book.Paragraphs);
            Equal(selected, handler.Requests.Count);
            Equal(selected, handler.Requests.Distinct().Count());
            Equal(Url(selected), handler.Requests[0]);
            Check(handler.Requests.All(uri => Enumerable.Range(1, selected).Select(Url).Contains(uri)), "The downloader fetched an unrelated or future post.");
            if (selected == 7) Check(!handler.Requests.Contains(Url(8)), "Selecting part 7 also fetched future part 8.");
        }

        // A normal standalone article can be exported, while a numbered later
        // part without a verified predecessor index must not become a full book.
        using (var folder = new TemporaryDirectory())
        using (var handler = new PagesHandler(new() { [Url(1)] = Page(1, 1, includeIndex: false, title: "Отдельный искусственный рассказ") }))
        using (var http = new HttpClient(handler))
        using (var source = new BestweaponSource(http, TimeSpan.Zero))
        {
            BookDocument standalone = await new StoryDownloader(source, folder.Path)
                .DownloadAsync(Url(1), false, false, null, CancellationToken.None);
            Equal(1, standalone.PageCount);
            Equal("Отдельный искусственный рассказ", standalone.Title);
            Sequence(Paragraphs(1), standalone.Paragraphs);
            Equal(1, standalone.Chapters?.Count);
        }
        await RejectAsync(Pages(3, currentOverride: Page(3, 3, includeIndex: false)));
    }

    public static async Task RejectedChainsAsync()
    {
        string valid = Page(3, 3);
        var variants = new[]
        {
            valid.Replace(IndexItem(2, 3), "", StringComparison.Ordinal),
            valid.Replace("href='/post_402'", "href='/post_801'", StringComparison.Ordinal),
            valid.Replace("href='/post_402'", "href='https://example.org/post_402'", StringComparison.Ordinal),
            valid.Replace("<li>" + Title(3) + "</li>", "<li>" + Title(3) + "</li><li>" + Title(3) + "</li>", StringComparison.Ordinal),
            valid.Replace("<li>" + Title(3) + "</li>", "<li><a href='/post_999'>" + Title(3) + "</a></li>", StringComparison.Ordinal),
            valid.Replace("id='postmaintext'", "id='missing-article'", StringComparison.Ordinal),
        };
        foreach (string malformed in variants) await RejectAsync(Pages(3, currentOverride: malformed));

        foreach (string badPredecessor in new[]
        {
            Page(2, 3, title: "Другой синтетический цикл. Часть 2"),
            Page(2, 3, title: Title(1)),
            Page(2, 3, author: "Другой автор"),
            Page(2, 3).Replace("href='/post_801'", "href='/post_999'", StringComparison.Ordinal),
            Page(2, 3).Replace("id='postmaintext'", "id='not-an-article'", StringComparison.Ordinal),
        })
        {
            var pages = Pages(3); pages[Url(2)] = badPredecessor;
            await RejectAsync(pages);
        }

        foreach (Uri rejected in new[] { new Uri("https://example.org/post_710"), Url(1) })
        {
            using var folder = new TemporaryDirectory();
            using var handler = new PagesHandler(Pages(3)) { Redirect = rejected };
            using var http = new HttpClient(handler);
            using var source = new BestweaponSource(http, TimeSpan.Zero);
            await ThrowsAsync(() => new StoryDownloader(source, folder.Path)
                .DownloadAsync(Url(3), false, false, null, CancellationToken.None));
            Sequence(new[] { Url(3) }, handler.Requests);
        }
    }

    public static async Task CacheAndCancellationAsync()
    {
        using var folder = new TemporaryDirectory();
        var original = Pages(3);
        async Task<(BookDocument Book, Uri[] Requests)> Download(Dictionary<Uri, string> pages, bool refresh = false)
        {
            using var handler = new PagesHandler(pages);
            using var http = new HttpClient(handler);
            using var source = new BestweaponSource(http, TimeSpan.Zero);
            var book = await new StoryDownloader(source, folder.Path)
                .DownloadAsync(Url(3), false, refresh, null, CancellationToken.None);
            return (book, handler.Requests.ToArray());
        }
        var first = await Download(original);
        Equal(3, first.Requests.Length);
        var resumed = await Download(original);
        Check(resumed.Requests.Length < 3, "A valid completed cache did not retain any post data.");
        Sequence(first.Book.Paragraphs, resumed.Book.Paragraphs);
        int tampered = 0;
        foreach (string path in Directory.GetFiles(folder.Path, "*.json", SearchOption.AllDirectories))
        {
            string json = await File.ReadAllTextAsync(path);
            string changed = json.Replace("CACHE-ORIGINAL", "CACHE-TAMPERED", StringComparison.Ordinal);
            if (changed == json) continue;
            await File.WriteAllTextAsync(path, changed); tampered++;
        }
        Check(tampered > 0, "The synthetic cached text could not be tampered for the integrity regression.");
        var repaired = await Download(original);
        Check(repaired.Requests.Length > 0, "Tampered cache bytes were accepted without refetching.");
        Sequence(first.Book.Paragraphs, repaired.Book.Paragraphs);
        var refreshed = await Download(Pages(3, marker: "REFRESHED"), refresh: true);
        Equal(3, refreshed.Requests.Length);
        Sequence(Enumerable.Range(1, 3).SelectMany(part => Paragraphs(part, "REFRESHED")), refreshed.Book.Paragraphs);

        using (var cancellation = new CancellationTokenSource())
        using (var cancelledFolder = new TemporaryDirectory())
        using (var handler = new PagesHandler(Pages(3)))
        using (var http = new HttpClient(handler))
        using (var source = new BestweaponSource(http, TimeSpan.Zero))
        {
            cancellation.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => new StoryDownloader(source, cancelledFolder.Path)
                .DownloadAsync(Url(3), false, false, null, cancellation.Token));
            Equal(0, handler.Requests.Count);
        }
        using (var cancellation = new CancellationTokenSource())
        using (var cancelledFolder = new TemporaryDirectory())
        using (var handler = new PagesHandler(Pages(3)) { Hold = true })
        using (var http = new HttpClient(handler))
        using (var source = new BestweaponSource(http, TimeSpan.Zero))
        {
            Task downloading = new StoryDownloader(source, cancelledFolder.Path)
                .DownloadAsync(Url(3), false, false, null, cancellation.Token);
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => downloading);
            Equal(1, handler.Requests.Count);
        }

        using (var cancellation = new CancellationTokenSource())
        using (var cancelledFolder = new TemporaryDirectory())
        {
            // The requested last part has already been cached when cancellation
            // interrupts discovery of its predecessors. A later attempt must
            // still obtain every missing part before returning a book.
            using (var handler = new PagesHandler(Pages(3))
                { OnRequest = count => { if (count == 2) cancellation.Cancel(); } })
            using (var http = new HttpClient(handler))
            using (var source = new BestweaponSource(http, TimeSpan.Zero))
            {
                await ThrowsAsync<OperationCanceledException>(() => new StoryDownloader(source, cancelledFolder.Path)
                    .DownloadAsync(Url(3), false, false, null, cancellation.Token));
                Equal(2, handler.Requests.Count);
            }
            using var resumedHandler = new PagesHandler(Pages(3));
            using var resumedHttp = new HttpClient(resumedHandler);
            using var resumedSource = new BestweaponSource(resumedHttp, TimeSpan.Zero);
            var complete = await new StoryDownloader(resumedSource, cancelledFolder.Path)
                .DownloadAsync(Url(3), false, false, null, CancellationToken.None);
            Equal(3, complete.Chapters?.Count);
            Sequence(Enumerable.Range(1, 3).SelectMany(part => Paragraphs(part)), complete.Paragraphs);
            Check(resumedHandler.Requests.Count is > 0 and < 3,
                "Resume either treated a partial cache as complete or discarded its verified requested post.");
        }
    }

    private static async Task RejectAsync(Dictionary<Uri, string> pages)
    {
        using var folder = new TemporaryDirectory();
        using var handler = new PagesHandler(pages);
        using var http = new HttpClient(handler);
        using var source = new BestweaponSource(http, TimeSpan.Zero);
        await ThrowsAsync(() => new StoryDownloader(source, folder.Path)
            .DownloadAsync(Url(3), false, false, null, CancellationToken.None));
        Check(handler.Requests.All(uri => uri.Host == "bestweapon.cc" && pages.ContainsKey(uri)), "An invalid series sent a request to an unverified destination.");
    }

    private static Uri Url(int part) => new("https://bestweapon.cc/post_" + Ids[part - 1]);
    private static string Title(int part) => SeriesTitle + ". Часть " + part;
    private static string[] Paragraphs(int part, string marker = "CACHE-ORIGINAL") =>
        [marker + "-" + part + " слово & «номер " + part + "».", "Финальный абзац части " + part + "."];
    private static string IndexItem(int part, int current) => part == current
        ? "<li>" + Title(part) + "</li>"
        : "<li><a href='/post_" + Ids[part - 1] + "'>" + Title(part) + "</a></li>";

    private static Dictionary<Uri, string> Pages(int count, string? currentOverride = null, string marker = "CACHE-ORIGINAL")
    {
        var result = Enumerable.Range(1, count).ToDictionary(Url, part => Page(part, count, marker: marker));
        if (currentOverride is not null) result[Url(count)] = currentOverride;
        return result;
    }

    private static string Page(int current, int count, bool includeIndex = true, string? title = null, string author = Author,
        string marker = "CACHE-ORIGINAL")
    {
        title ??= Title(current);
        string index = includeIndex ? "<ol id='storylist'>" + string.Concat(Enumerable.Range(1, count).Select(part => IndexItem(part, current))) + "</ol>" : "";
        return "<html><head><meta name='author' content='" + WebUtility.HtmlEncode(author) + "'>" +
            "<meta property='og:title' content='" + WebUtility.HtmlEncode(title) + "'><title>" + WebUtility.HtmlEncode(title) + "</title></head><body>" +
            "<div id='main_post_title' class='post_title2'>" + WebUtility.HtmlEncode(title) + "</div>" + index +
            "<div class='maintext style2'><img class='preview_image' src='/OUTSIDE-ARTICLE.jpg'>" +
            "<span id='postmaintext'><p>" + marker + "-" + current + " <em>слово</em> &amp; «номер " + current + "».</p>" +
            "<p>Финальный абзац части " + current + ".</p></span><hr>" +
            "<div class='post_view2'>OUTSIDE-ARTICLE<a href='/post_777'>Рекомендация</a></div>" +
            "<p class='post_tags'>OUTSIDE-ARTICLE</p></div></body></html>";
    }

    private sealed class PagesHandler(Dictionary<Uri, string> pages) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public Uri? Redirect { get; init; }
        public bool Hold { get; init; }
        public Action<int>? OnRequest { get; init; }
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Uri uri = request.RequestUri ?? throw new InvalidOperationException("Synthetic request is missing its URL.");
            Requests.Add(uri); Started.TrySetResult(true);
            OnRequest?.Invoke(Requests.Count);
            token.ThrowIfCancellationRequested();
            if (Hold) await Task.Delay(Timeout.Infinite, token);
            if (Redirect is not null)
                return new HttpResponseMessage(HttpStatusCode.Redirect) { RequestMessage = request, Headers = { Location = Redirect } };
            if (!pages.TryGetValue(uri, out string? html)) throw new InvalidOperationException("Unexpected synthetic HTTP request: " + uri);
            return new HttpResponseMessage(HttpStatusCode.OK)
            { RequestMessage = request, Content = new StringContent(html, Encoding.UTF8, "text/html") };
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TextDownloader-bestweapon-tests-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected [{expected}], got [{actual}]."); }
    private static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    { if (!expected.SequenceEqual(actual)) throw new InvalidOperationException("Synthetic sequence differs from the expected order/content."); }
    private static Task ThrowsAsync(Func<Task> action) => ThrowsAsync<Exception>(action);
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected exception " + typeof(T).Name);
    }
}
