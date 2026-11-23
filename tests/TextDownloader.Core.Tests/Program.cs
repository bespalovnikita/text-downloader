using System.Diagnostics;
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
            ("Style and genre aliases preserve complete story identity and exact text", PresentationAliases),
            ("HTTP redirects and age forms allow only same-story page aliases", AliasTransportScope),
            ("Sefan HTTP 307 preserves the location and the age-confirmation POST", TemporaryRedirectAsync),
            ("A non-reading redirect leaves the verified prefix reusable", RedirectOutsideReadingAsync),
            ("Scope fallback retries neutral genre once without mixing partial text", NeutralGenreFallback),
            ("Neutral genre fallback preserves adult permission and non-scope failures", FallbackScopeAndPermission),
            ("HTTP page spacing is configurable and cancellable", PageSpacing),
            ("HTTP journal records requests, statuses and retries", HttpJournal),
            ("HTML text, navigation, source whitespace and age gate", HtmlParsing),
            ("Age confirmation is sent only after explicit permission", AgeConfirmation),
            ("Complete chain joins words and punctuation without page headings", CompleteStory),
            ("Missing, skipped, duplicated and inconsistent pages fail", IncompleteStories),
            ("Cancellation and corrupt cache cannot produce false success", CancellationAndCache),
            ("Resuming reuses exactly the validated prefix after a page failure", ResumeAfterPageFailure),
            ("A corrected URL retries the pending page without changing the story", ManualPageRecovery),
            ("Preview does not download the entire story", Preview),
            ("A new registered source can use the common page downloader", RegisteredSource),
            ("Sefan selector collects earlier and later chapters and resumes within a chapter", SefanSeriesTests.InlineContentsAndResumeAsync),
            ("Sefan uses a separate contents page and rejects gaps", SefanSeriesTests.SeparateContentsAsync),
            ("Sefan standalone stories skip the no-contents link and retry from saved pages", SefanSeriesTests.NoContentsMarkerAndCachedRetryAsync),
            ("Bestweapon URL scope and current-post-only preview", BestweaponTests.PreviewAsync),
            ("Bestweapon HTTP 307 preserves an allowed redirect URL", BestweaponTests.TemporaryRedirectAsync),
            ("Bestweapon orders all preceding chapters and excludes future parts", BestweaponTests.OrderedSeriesAsync),
            ("Bestweapon rejects missing, ambiguous and unrelated series parts", BestweaponTests.RejectedChainsAsync),
            ("Bestweapon cache integrity, refresh and cancellation", BestweaponTests.CacheAndCancellationAsync),
            ("EPUB manifest, spine, navigation and exact Unicode text", EpubStructure),
            ("FB2 namespace, metadata and exact Unicode text", Fb2Structure),
            ("EPUB and FB2 preserve chapter order and working contents", ChapterExportTests.RunAsync),
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
        string other = PageUrl(2).AbsoluteUri.Replace("/2/drama/", "/5/drama/");
        Equal(new Uri(other), SefanSource.Parse(PageHtml("Текст.").Replace("</body>", $"<a rel='next' href='{other}'>Далее</a></body>"), Source).NextUrl);
        Equal(new Uri(other.Replace("/drama/", "/i/")), SefanSource.Parse(PageHtml("Текст.").Replace("</body>",
            $"<a rel='next' href='{other.Replace("/drama/", "/i/")}'>Далее</a></body>"), Source).NextUrl);
        Throws(() => SefanSource.Parse(PageHtml("Текст.").Replace("</body>",
            $"<a rel='next' href='{other.Replace("/drama/", "/fantasy/")}'>Далее</a></body>"), Source));
        Throws(() => SefanSource.Parse(PageHtml("Текст.").Replace("</body>",
            $"<a rel='next' href='{other.Replace("test-story", "different-story")}'>Далее</a></body>"), Source));
        return Task.CompletedTask;
    }

    private static async Task PresentationAliases()
    {
        string[] names = ["chitat_alias-story", "alias-story", "read_alias_story", "rasskaz_2026", "12345"];
        string[] genres = ["drama", "romantika", "another-category", "proza-i-istorii", "genre42"];
        for (int index = 0; index < names.Length; index++)
        {
            string name = names[index], genre = genres[index];
            // Numeric styles are presentation views. Genre changes are explicit
            // only when one side is Sefan's neutral /i/ route.
            Uri first = new($"https://sefan.ru/stories/ru/5/{genre}/{name}_7.html");
            Uri secondHead = new($"https://sefan.ru/stories/ru/2/{genre}/{name}_7-2.html");
            Uri secondBody = new($"https://sefan.ru/stories/ru/903/{genre}/{name}_7-2.html");
            Uri third = new($"https://sefan.ru/stories/ru/17/i/{name}_7-3.html");
            Uri lastAlias = new($"https://sefan.ru/stories/ru/42/i/{name}_7-3.html");
            string firstHtml = AliasPage("Первый абзац.<br/>Мы выбрали даль", secondHead, secondBody, lastAlias);
            string secondHtml = AliasPage("нюю дорогу и <em>вышли</em>\n", third, lastAlias, lastAlias);
            string lastHtml = AliasPage("к реке.<br/>Финал &amp; точка.", null, null, lastAlias);
            using var folder = new TemporaryDirectory();
            using var source = new FakeSource(new()
            {
                [first] = firstHtml,
                [secondHead] = secondHtml,
                [secondBody] = secondHtml,
                [third] = lastHtml,
                [lastAlias] = lastHtml,
            });
            BookDocument book = await new StoryDownloader(source, folder.Path)
                .DownloadAsync(first, false, false, null, CancellationToken.None);
            Equal(3, book.PageCount);
            Equal(first, book.SourceUrl);
            Equal("Синтетический рассказ", book.Title);
            Sequence(new[] { "Первый абзац.", "Мы выбрали дальнюю дорогу и вышли к реке.", "Финал & точка." }, book.Paragraphs);
            Equal(3, source.Calls.Count);
            Equal(first, source.Calls[0]);
            Check(new[] { secondHead, secondBody }.Contains(source.Calls[1]), "Second-page aliases were fetched twice or skipped.");
            Check(new[] { third, lastAlias }.Contains(source.Calls[2]), "Third-page aliases were fetched twice or skipped.");

            string[] badNexts =
            [
                secondHead.AbsoluteUri.Replace("/ru/", "/uk/"),
                secondHead.AbsoluteUri.Replace(name + "_7-2", "different-" + name + "_7-2"),
                secondHead.AbsoluteUri.Replace("_7-2.html", "_8-2.html"),
                secondHead.AbsoluteUri.Replace("sefan.ru/", "sefan.ru.example.org/"),
                secondHead.AbsoluteUri.Replace("_7-2.html", "_7-3.html"),
                secondHead.AbsoluteUri.Replace(name + "_7-2", (name.StartsWith("chitat_", StringComparison.Ordinal) ? name[7..] : "chitat_" + name) + "_7-2"),
            ];
            foreach (string bad in badNexts)
                Throws(() => SefanSource.Parse(AliasPage("Текст.", new Uri(bad), new Uri(bad), lastAlias), first));
            // A valid head link must not mask a contradictory body link, whether
            // it points at a different page or changes the exact story filename.
            Throws(() => SefanSource.Parse(AliasPage("Текст.", secondHead, third, lastAlias), first));
            Throws(() => SefanSource.Parse(AliasPage("Текст.", secondHead,
                new Uri(secondBody.AbsoluteUri.Replace(name + "_7-2", "other-story_7-2")), lastAlias), first));
            if (name != name.ToUpperInvariant())
                Throws(() => SefanSource.Parse(AliasPage("Текст.",
                    new Uri(secondHead.AbsoluteUri.Replace(name + "_7-2", name.ToUpperInvariant() + "_7-2")), null, lastAlias), first));
        }
    }

    private static string AliasPage(string fragment, Uri? headNext, Uri? bodyNext, Uri last)
    {
        string head = headNext is null ? "" : $"<link rel='next' href='{headNext}'>";
        string inlineNext = bodyNext is null ? "" : $"<a title='Следующая страница' href='{bodyNext}'>•••</a>";
        string body = bodyNext is null ? "" : $"<a rel='next' href='{bodyNext}'>Далее</a>";
        return $"<html><head>{head}</head><body><h1>Синтетический рассказ</h1>" +
            $"<div id='rsz'>{fragment}{inlineNext}</div>{body}" +
            $"<a rel='last' title='Страница 3 из 3' href='{last}'>Последняя</a></body></html>";
    }

    private static async Task AliasTransportScope()
    {
        Uri original = new("https://sefan.ru/stories/ru/5/drama/chitat_transport_1.html");
        Uri redirected = new("https://sefan.ru/stories/ru/2/drama/chitat_transport_1.html");
        Uri ageAction = new("https://sefan.ru/stories/ru/99/i/chitat_transport_1.html");
        string gate = $"<form action='{ageAction}'><input name='freed' value='1'>" +
            "<input type='hidden' name='nonce' value='alias-token'><input name='fkay' value='Подтверждаю'></form>";
        const string complete = "<h1>Синтетический рассказ</h1><div id='rsz'>Целый рассказ.</div><span class='but'>Далее</span>";
        using (var handler = new SequenceHandler(
            new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = redirected } },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(gate, Encoding.UTF8, "text/html") },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(complete, Encoding.UTF8, "text/html") }))
        using (var http = new HttpClient(handler))
        using (var source = new SefanSource(http))
        {
            StoryPage page = await source.GetPageAsync(original, true, CancellationToken.None);
            Equal(1, page.TotalPages);
            Equal(ageAction, page.Url);
            Sequence(new[] { original, redirected, ageAction }, handler.RequestUris);
            Sequence(new[] { HttpMethod.Get, HttpMethod.Get, HttpMethod.Post }, handler.Requests.Select(request => request.Method));
            Check(handler.Requests[2].Body.Contains("freed=1") && handler.Requests[2].Body.Contains("nonce=alias-token"), "Aliased age form was not posted correctly.");
        }

        using (var handler = new SequenceHandler(new HttpResponseMessage(HttpStatusCode.Redirect)
            { Headers = { Location = new Uri(redirected.AbsoluteUri.Replace("sefan.ru", "example.org")) } }))
        using (var http = new HttpClient(handler))
        using (var source = new SefanSource(http))
        {
            await ThrowsAsync(() => source.GetPageAsync(original, true, CancellationToken.None));
            Equal(1, handler.Requests.Count);
        }
        using (var handler = new SequenceHandler(gate.Replace("_1.html", "_1-2.html")))
        using (var http = new HttpClient(handler))
        using (var source = new SefanSource(http))
        {
            await ThrowsAsync(() => source.GetPageAsync(original, true, CancellationToken.None));
            Equal(1, handler.Requests.Count);
            Equal(HttpMethod.Get, handler.Requests[0].Method);
        }
    }

    private static async Task TemporaryRedirectAsync()
    {
        var alias = new Uri("https://www.sefan.ru" + Source.AbsolutePath + "?flow=synthetic");
        var action = new Uri(alias, "?age=synthetic");
        var accepted = new Uri(alias, "?accepted=synthetic");
        var log = new CaptureLog();
        const string gate = "<form action='?age=synthetic'><input name='freed' value='1'>" +
            "<input type='hidden' name='nonce' value='synthetic-token'></form>";
        using (var handler = new SequenceHandler(
            new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Headers = { Location = alias } },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(gate) },
            new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Headers = { Location = new Uri("?accepted=synthetic#text", UriKind.Relative) } },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(PageHtml("Рассказ после подтверждения.", total: 1)) }))
        using (var http = new HttpClient(handler))
        using (var source = new SefanSource(http, TimeSpan.Zero, log))
        {
            var page = await source.GetPageAsync(Source, true, CancellationToken.None);
            Equal(Source, page.Url);
            Sequence(new[] { Source, alias, action, accepted }, handler.RequestUris);
            Sequence(new[] { HttpMethod.Get, HttpMethod.Get, HttpMethod.Post, HttpMethod.Post }, handler.Requests.Select(request => request.Method));
            Check(handler.Requests[2].Body.Contains("nonce=synthetic-token"), "The age confirmation form was not posted.");
            Equal(handler.Requests[2].Body, handler.Requests[3].Body);
            Check(log.Events.Any(entry => entry.Message.Contains("HTTP 307")), "The HTTP 307 response was not logged.");
            Check(log.Events.All(entry => !entry.Message.Contains("=synthetic")), "A redirect query appeared in the journal.");
        }

        using (var handler = new SequenceHandler(new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)))
        using (var http = new HttpClient(handler))
        using (var source = new SefanSource(http, TimeSpan.Zero))
        {
            try
            {
                await source.GetPageAsync(Source, false, CancellationToken.None);
                throw new Exception("A 307 without Location was accepted.");
            }
            catch (InvalidDataException error)
            {
                Check(error.Message.Contains("307") && error.Message.Contains("Location"), "The missing Location was not explained.");
            }
            Equal(1, handler.Requests.Count);
        }

        using (var handler = new SequenceHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(gate) },
            new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Headers = { Location = new Uri("https://example.org" + Source.AbsolutePath) } }))
        using (var http = new HttpClient(handler))
        using (var source = new SefanSource(http, TimeSpan.Zero))
        {
            await ThrowsAsync<InvalidDataException>(() => source.GetPageAsync(Source, true, CancellationToken.None));
            Equal(2, handler.Requests.Count);
        }
    }

    private static async Task RedirectOutsideReadingAsync()
    {
        using var folder = new TemporaryDirectory();
        var log = new CaptureLog();
        const int total = 21;
        var responses = Enumerable.Range(1, total - 1).Select(number =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(PageHtml($"Тестовая страница {number}.<br>", next: number + 1, total: total))
            }).Append(new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
            {
                Headers = { Location = new Uri("/notice.html?nonce=synthetic-private", UriKind.Relative) }
            }).ToArray();

        DownloadCheckpoint? checkpoint = null;
        using (var handler = new SequenceHandler(responses))
        using (var http = new HttpClient(handler))
        using (var source = new SefanSource(http, TimeSpan.Zero, log))
        {
            var downloader = new StoryDownloader(source, folder.Path);
            try
            {
                await downloader.DownloadAsync(Source, false, false,
                    new InlineProgress(progress => checkpoint = progress.Checkpoint ?? checkpoint), CancellationToken.None);
                throw new Exception("A non-reading redirect produced a book.");
            }
            catch (InvalidDataException error)
            {
                Check(error.Message.Contains("HTTP 307") && error.Message.Contains("https://sefan.ru/notice.html"),
                    "The failure did not report the redirect destination.");
                Check(!error.Message.Contains("synthetic-private"), "The redirect parameter appeared in the error.");
            }
            Sequence(Enumerable.Range(1, total).Select(PageUrl), handler.RequestUris);
            Equal(PageUrl(20), checkpoint?.LastSuccessfulPageUrl);
            Equal(PageUrl(21), checkpoint?.PendingPageUrl);
            Check(log.Events.Any(entry => entry.Message.Contains("Location: https://sefan.ru/notice.html")),
                "The refused redirect did not record its Location.");
            Check(log.Events.All(entry => !entry.Message.Contains("synthetic-private")),
                "The redirect parameter appeared in the journal.");
        }

        using var retryHandler = new SequenceHandler(PageHtml("Тестовая страница 21.", total: total));
        using var retryHttp = new HttpClient(retryHandler);
        using var retrySource = new SefanSource(retryHttp, TimeSpan.Zero);
        var result = await new StoryDownloader(retrySource, folder.Path).DownloadAsync(Source, false, false, null,
            CancellationToken.None, new DownloadRecovery(checkpoint, null));
        Sequence(new[] { PageUrl(21) }, retryHandler.RequestUris);
        Equal(total, result.PageCount);
    }

    private static async Task NeutralGenreFallback()
    {
        foreach (string name in new[] { "chitat_fallback-story", "fallback-story" })
        foreach (bool wrongFilename in new[] { false, true })
        {
            Uri original = new($"https://sefan.ru/stories/ru/5/another-genre/{name}_3.html");
            Uri originalSecond = new($"https://sefan.ru/stories/ru/2/another-genre/{name}_3-2.html");
            Uri originalThird = new($"https://sefan.ru/stories/ru/2/another-genre/{name}_3-3.html");
            Uri rejectedNext = new(wrongFilename
                ? originalThird.AbsoluteUri.Replace(name + "_3-3", "different-story_3-3")
                : originalThird.AbsoluteUri.Replace("/another-genre/", "/drama/"));
            Uri neutral = new(original.AbsoluteUri.Replace("/another-genre/", "/i/"));
            Uri neutralSecond = new(originalSecond.AbsoluteUri.Replace("/another-genre/", "/i/"));
            using var folder = new TemporaryDirectory();
            using var source = new FakeSource(new()
            {
                [original] = ChainPage("Промежуточный текст нельзя смешивать. ", originalSecond, originalThird, 3),
                [originalSecond] = ChainPage("Чужой промежуточный хвост.", rejectedNext, originalThird, 3),
                [neutral] = ChainPage("Полный текст ", neutralSecond, neutralSecond, 2),
                [neutralSecond] = ChainPage("правильного маршрута.<br>Конец.", null, neutralSecond, 2),
            });
            BookDocument book = await new StoryDownloader(source, folder.Path)
                .DownloadAsync(original, true, false, null, CancellationToken.None);
            Equal(neutral, book.SourceUrl);
            Equal(2, book.PageCount);
            Sequence(new[] { "Полный текст правильного маршрута.", "Конец." }, book.Paragraphs);
            Sequence(new[] { original, originalSecond, neutral, neutralSecond }, source.Calls);
            Check(source.Confirmations.All(value => value), "Fallback lost explicit adult permission.");
            // The discarded first attempt did persist a page, so this also
            // proves /i/ did not reuse that original-genre cache record.
            Check(Directory.GetFiles(folder.Path, "*.json", SearchOption.AllDirectories).Length >= 3, "Expected distinct original and /i/ cache records.");
        }

        Uri first = new("https://sefan.ru/stories/ru/5/drama/chitat_failed-fallback_1.html");
        Uri neutralFirst = new(first.AbsoluteUri.Replace("/drama/", "/i/"));
        Uri wrongNext = new("https://sefan.ru/stories/ru/2/drama/chitat_wrong-story_1-2.html");
        string invalid = ChainPage("Не тот маршрут.", wrongNext, wrongNext, 2);
        using (var folder = new TemporaryDirectory())
        using (var source = new FakeSource(new() { [first] = invalid, [neutralFirst] = invalid }))
        {
            await ThrowsAsync(() => new StoryDownloader(source, folder.Path).DownloadAsync(first, false, false, null, CancellationToken.None));
            Sequence(new[] { first, neutralFirst }, source.Calls);
            Check(source.Confirmations.All(value => !value), "Fallback granted adult permission implicitly.");
        }
        using (var folder = new TemporaryDirectory())
        using (var source = new FakeSource(new() { [neutralFirst] = invalid }))
        {
            await ThrowsAsync(() => new StoryDownloader(source, folder.Path).DownloadAsync(neutralFirst, false, false, null, CancellationToken.None));
            Sequence(new[] { neutralFirst }, source.Calls);
        }
    }

    private static string ChainPage(string fragment, Uri? next, Uri? last, int? total)
    {
        string forward = next is null ? "" : $"<a rel='next' href='{next}'>Далее</a>";
        string ending = last is null || total is null ? "" : $"<a rel='last' title='Страница {total} из {total}' href='{last}'>Последняя</a>";
        return $"<h1>Рассказ для проверки маршрута</h1><div id='rsz'>{fragment}</div>{forward}{ending}";
    }

    private static async Task FallbackScopeAndPermission()
    {
        Uri original = new("https://sefan.ru/stories/ru/5/drama/read_permission-story_1.html");
        Uri neutral = new(original.AbsoluteUri.Replace("/drama/", "/i/"));
        Uri neutralSecond = new(neutral.AbsoluteUri.Replace("_1.html", "_1-2.html"));
        Uri wrongGenreNext = new(original.AbsoluteUri.Replace("/drama/", "/romantika/").Replace("_1.html", "_1-2.html"));
        string mismatch = ChainPage("Исходный текст.", wrongGenreNext, wrongGenreNext, 2);
        string gate = $"<form action='{neutral}'><input name='freed' value='1'>" +
            "<input type='hidden' name='nonce' value='neutral-token'><input name='fkay' value='Подтверждаю'></form>";
        string complete = ChainPage("Начало правильного текста.", neutralSecond, neutralSecond, 2);
        using (var folder = new TemporaryDirectory())
        using (var handler = new SequenceHandler(mismatch, gate))
        using (var http = new HttpClient(handler))
        using (var source = new SefanSource(http))
        {
            await ThrowsAsync<AgeConfirmationRequiredException>(() => new StoryDownloader(source, folder.Path)
                .PreviewAsync(new Uri(original.AbsoluteUri.Replace("_1.html", "_1-12.html")), false, CancellationToken.None));
            Sequence(new[] { original, neutral }, handler.RequestUris);
            Check(handler.Requests.All(request => request.Method == HttpMethod.Get), "Fallback posted age consent without permission.");
        }
        using (var folder = new TemporaryDirectory())
        using (var handler = new SequenceHandler(mismatch, gate, complete))
        using (var http = new HttpClient(handler))
        using (var source = new SefanSource(http))
        {
            StoryPage preview = await new StoryDownloader(source, folder.Path)
                .PreviewAsync(new Uri(original.AbsoluteUri.Replace("_1.html", "_1-12.html")), true, CancellationToken.None);
            Equal(neutral, preview.Url);
            Equal(2, preview.TotalPages);
            Sequence(new[] { original, neutral, neutral }, handler.RequestUris);
            Sequence(new[] { HttpMethod.Get, HttpMethod.Get, HttpMethod.Post }, handler.Requests.Select(request => request.Method));
            Check(handler.Requests[2].Body.Contains("freed=1") && handler.Requests[2].Body.Contains("nonce=neutral-token"), "Explicit adult permission was not preserved on fallback.");
        }

        foreach (string invalid in new[]
        {
            "<h1>Нет блока чтения</h1>",
            ChainPage("Пропуск номера.", new Uri(original.AbsoluteUri.Replace("_1.html", "_1-3.html")), null, null),
            ChainPage("Нельзя подтвердить конец.", null, null, null),
            ChainPage("Внешняя ссылка.", new Uri("https://example.org/stories/ru/5/drama/read_permission-story_1-2.html"), null, null),
            "<form><input name='freed' value='1'></form>",
        })
        {
            using var folder = new TemporaryDirectory();
            using var source = new FakeSource(new() { [original] = invalid });
            await ThrowsAsync(() => new StoryDownloader(source, folder.Path).DownloadAsync(original, false, false, null, CancellationToken.None));
            Sequence(new[] { original }, source.Calls);
        }
        foreach (Exception error in new Exception[] { new IOException("Synthetic network failure."), new OperationCanceledException(new CancellationToken(true)) })
        {
            using var folder = new TemporaryDirectory();
            using var source = new FailingSource(error);
            await ThrowsAsync(() => new StoryDownloader(source, folder.Path).DownloadAsync(original, false, false, null, CancellationToken.None));
            Sequence(new[] { original }, source.Calls);
        }
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

    private static async Task PageSpacing()
    {
        string html = PageHtml("Проверка задержки.", total: 1);
        using var handler = new SequenceHandler(html, html);
        using var http = new HttpClient(handler);
        using var source = new SefanSource(http, TimeSpan.FromMilliseconds(100));
        await source.GetPageAsync(Source, false, CancellationToken.None);
        long beforeSecond = Stopwatch.GetTimestamp();
        await source.GetPageAsync(Source, false, CancellationToken.None);
        Check(Stopwatch.GetElapsedTime(beforeSecond) >= TimeSpan.FromMilliseconds(75), "Second request skipped configured spacing.");

        using var slowHandler = new SequenceHandler(html);
        using var slowHttp = new HttpClient(slowHandler);
        using var cancellable = new SefanSource(slowHttp, TimeSpan.FromSeconds(5));
        await cancellable.GetPageAsync(Source, false, CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));
        await ThrowsAsync<OperationCanceledException>(() => cancellable.GetPageAsync(Source, false, cancellation.Token));
        Equal(1, slowHandler.Requests.Count);
    }

    private static async Task HttpJournal()
    {
        var retry = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        retry.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
        using var handler = new SequenceHandler(retry, new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(PageHtml("Тест журнала.", total: 1), Encoding.UTF8, "text/html")
        });
        var log = new CaptureLog();
        using var http = new HttpClient(handler);
        using var source = new SefanSource(http, TimeSpan.Zero, log);
        await source.GetPageAsync(Source, false, CancellationToken.None);
        Equal(2, log.Events.Count(entry => entry.Level == DownloadLogLevel.Detail && entry.Message == $"GET {Source}"));
        Check(log.Events.Any(entry => entry.Level == DownloadLogLevel.Warning && entry.Message.Contains("HTTP 429")), "Retry warning missing.");
        Check(log.Events.Any(entry => entry.Level == DownloadLogLevel.Detail && entry.Message.Contains("HTTP 200")), "Response status missing.");
        Check(log.Events.All(entry => !entry.Message.Contains("Тест журнала.")), "Response body leaked into the journal.");
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

    private static async Task ResumeAfterPageFailure()
    {
        using var folder = new TemporaryDirectory();
        const int total = 100;
        var pages = Enumerable.Range(1, total).ToDictionary(PageUrl, number =>
            PageHtml($"Страница {number}.<br>", next: number < total ? number + 1 : null, total: total));
        pages[PageUrl(21)] = PageHtml("Неполная страница.");
        using (var first = new FakeSource(pages))
        {
            await ThrowsAsync(() => new StoryDownloader(first, folder.Path)
                .DownloadAsync(Source, false, true, null, CancellationToken.None));
            Sequence(Enumerable.Range(1, 21).Select(PageUrl), first.Calls);
            Equal(20, Directory.GetFiles(folder.Path, "*.json", SearchOption.AllDirectories).Length);
        }

        pages[PageUrl(21)] = PageHtml("Страница 21.<br>", next: 22, total: total);
        using var resumed = new FakeSource(pages);
        var book = await new StoryDownloader(resumed, folder.Path)
            .DownloadAsync(Source, false, false, null, CancellationToken.None);
        Sequence(Enumerable.Range(21, total - 20).Select(PageUrl), resumed.Calls);
        Sequence(Enumerable.Range(1, total).Select(number => $"Страница {number}."), book.Paragraphs);
    }

    private static async Task ManualPageRecovery()
    {
        using var folder = new TemporaryDirectory();
        Uri pending = PageUrl(2);
        Uri replacement = new(pending.AbsoluteUri.Replace("/2/drama/", "/5/i/"));
        using var source = new FakeSource(new()
        {
            [Source] = ChainPage("Начало. ", pending, pending, 2),
            [replacement] = ChainPage("Конец.", null, replacement, 2),
        });
        var downloader = new StoryDownloader(source, folder.Path);
        DownloadCheckpoint? checkpoint = null;
        await ThrowsAsync<IOException>(() => downloader.DownloadAsync(Source, false, false,
            new InlineProgress(progress => checkpoint = progress.Checkpoint ?? checkpoint), CancellationToken.None));
        Equal(new DownloadCheckpoint(Source, Source, pending), checkpoint);
        Throws<ArgumentException>(() => StoryDownloader.ValidateReplacementPage(pending, PageUrl(3)));
        Throws<SefanStoryScopeMismatchException>(() => StoryDownloader.ValidateReplacementPage(pending,
            new Uri(replacement.AbsoluteUri.Replace("test-story", "unrelated"))));
        source.Calls.Clear();
        var book = await downloader.DownloadAsync(Source, false, false, null, CancellationToken.None,
            new DownloadRecovery(checkpoint, StoryDownloader.ValidateReplacementPage(pending, replacement)));
        Sequence(new[] { replacement }, source.Calls);
        Sequence(new[] { "Начало. Конец." }, book.Paragraphs);
    }

    private static async Task Preview()
    {
        using var folder = new TemporaryDirectory();
        using var source = new FakeSource(new() { [Source] = PageHtml("Первая страница.", next: 2, total: 2) });
        await new StoryDownloader(source, folder.Path).PreviewAsync(PageUrl(2), false, CancellationToken.None);
        Sequence(new[] { Source }, source.Calls);
    }

    private static async Task RegisteredSource()
    {
        using var folder = new TemporaryDirectory();
        var adapter = new SyntheticNovelSource();
        using var sources = new SupportedStorySource(new IStorySource[] { new SefanSource(), adapter });
        var downloader = new StoryDownloader(sources, folder.Path);
        Equal("глав", sources.Describe(SyntheticNovelSource.Second).CountLabel);
        Equal(SyntheticNovelSource.First, sources.NormalizeToFirstPage(SyntheticNovelSource.Second));
        var preview = await downloader.PreviewAsync(SyntheticNovelSource.Second, false, CancellationToken.None);
        Equal(SyntheticNovelSource.First, preview.Url);
        var book = await downloader.DownloadAsync(SyntheticNovelSource.Second, false, false, null, CancellationToken.None);
        Equal(2, book.PageCount);
        Sequence(new[] { "Искусственное начало.", "Окончание произведения." }, book.Paragraphs);
        Sequence(new[] { SyntheticNovelSource.First, SyntheticNovelSource.First, SyntheticNovelSource.Second }, adapter.Calls);
    }

    private sealed class SyntheticNovelSource : IStorySource
    {
        public static readonly Uri First = new("https://example.invalid/novels/example");
        public static readonly Uri Second = new("https://example.invalid/novels/example?cursor=synthetic");
        public List<Uri> Calls { get; } = [];
        public bool CanHandle(Uri uri) => uri == First || uri == Second;
        public Uri NormalizeToFirstPage(Uri uri) => CanHandle(uri) ? First : throw new ArgumentException();
        public StorySourceInfo Describe(Uri uri) => new("Тестовый каталог", "глав");
        public Task<StoryPage> GetPageAsync(Uri uri, bool confirmAdult, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls.Add(uri);
            return Task.FromResult(uri == First
                ? new StoryPage(uri, 1, "Тестовое произведение", null, "Искусственное начало.\n", Second, 2)
                : new StoryPage(uri, 2, "Тестовое произведение", null, "Окончание произведения.", null, 2));
        }
        public void Dispose() { }
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

    private abstract class SefanTestSource : IStorySource
    {
        private readonly SefanSource _routes = new();
        public bool CanHandle(Uri uri) => _routes.CanHandle(uri);
        public Uri NormalizeToFirstPage(Uri uri) => _routes.NormalizeToFirstPage(uri);
        public int? GetPageNumber(Uri uri) => _routes.GetPageNumber(uri);
        public Uri ValidatePageRoute(Uri first, Uri candidate, int? number) => _routes.ValidatePageRoute(first, candidate, number);
        public Uri? GetAlternateStart(Uri first, Exception? error = null) => _routes.GetAlternateStart(first, error);
        public abstract Task<StoryPage> GetPageAsync(Uri uri, bool confirmAdult, CancellationToken token);
        public void Dispose() => _routes.Dispose();
    }

    private sealed class FakeSource(Dictionary<Uri, string> pages) : SefanTestSource
    {
        public List<Uri> Calls { get; } = [];
        public List<bool> Confirmations { get; } = [];
        public override Task<StoryPage> GetPageAsync(Uri uri, bool confirmAdult, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls.Add(uri); Confirmations.Add(confirmAdult);
            if (!pages.TryGetValue(uri, out string? html)) throw new IOException("Synthetic page unavailable: " + uri);
            return Task.FromResult(SefanSource.Parse(html, uri));
        }
    }

    private sealed class FailingSource(Exception error) : SefanTestSource
    {
        public List<Uri> Calls { get; } = [];
        public override Task<StoryPage> GetPageAsync(Uri uri, bool confirmAdult, CancellationToken token)
        {
            Calls.Add(uri);
            return Task.FromException<StoryPage>(error);
        }
    }

    private sealed class InlineProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }

    private sealed class CaptureLog : IProgress<DownloadLogEvent>
    {
        public List<DownloadLogEvent> Events { get; } = [];
        public void Report(DownloadLogEvent value) => Events.Add(value);
    }

    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage>[] replies;
        public List<(HttpMethod Method, string Body)> Requests { get; } = [];
        public List<Uri> RequestUris { get; } = [];
        public SequenceHandler(params string[] bodies) => replies = bodies.Select<string, Func<HttpResponseMessage>>(body => () =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/html") }).ToArray();
        public SequenceHandler(params HttpResponseMessage[] responses) => replies = responses.Select<HttpResponseMessage, Func<HttpResponseMessage>>(response => () => response).ToArray();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(token);
            Requests.Add((request.Method, body));
            RequestUris.Add(request.RequestUri ?? throw new IOException("Synthetic request has no URI."));
            if (Requests.Count > replies.Length) throw new IOException("Unexpected synthetic HTTP request.");
            HttpResponseMessage response = replies[Requests.Count - 1]();
            response.RequestMessage = request;
            return response;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TextDownloader.Tests." + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
