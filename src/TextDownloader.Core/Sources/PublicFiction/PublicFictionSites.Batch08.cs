namespace TextDownloader.Core;

internal static partial class PublicFictionSites
{
    static partial void AddBatch08(Dictionary<int, PublicFictionSite[]> batches) => batches.Add(8,
    [
        EfictionArchive("Nine Lives", "www.ninelivesarchive.com", ""),
        EfictionArchive("Midnight Whispers", "midnightwhispers.net", ""),
        EfictionArchive("Chaos", "chaos.sycophanthex.com", ""),
        new("RPG Novels", "https://rpgnovels.com/active-projects/", "rpgnovels.com",
            @"/(?<work>[a-z0-9-]+)/?", @"/(?<work>[a-z0-9-]+?)_ch(?<part>[0-9][a-z0-9.-]*)/?",
            EntryContent)
        {
            CatalogLinks = "//div[contains(@class,'entry-content')]//a[@href]",
            WorkTitle = "//h1[contains(@class,'wp-block-post-title')]",
            ChapterTitle = "//h1[contains(@class,'wp-block-post-title')]",
            FirstChapter = "(//div[contains(@class,'entry-content')]//a[contains(@href,'_ch1/') and @href])[1]",
            FirstChapterMayUseDifferentSlug = true,
            NextChapter = "//a[(normalize-space(.)='Next' or starts-with(normalize-space(.),'Next Chapter')) and @href and @href!='#']"
        },
        new("Novello", "https://novello.id/", "novello.id",
            @"/book/(?<work>[a-z0-9-]+)/?", @"/book/(?<work>[a-z0-9-]+)/chapter/(?<part>[0-9]+(?:\.[0-9]+)?)/?",
            "//div[contains(concat(' ',normalize-space(@class),' '),' reading-text ')]")
        {
            FirstChapter = "//a[normalize-space(.)='Mulai Baca' and @href]",
            NextChapter = "//a[(@rel='next' or contains(@aria-label,'Selanjutnya') or contains(normalize-space(.),'Bab Berikutnya')) and @href]",
            WorkFromChapter = "/book/${work}"
        },
        EfictionArchive("Lumos", "lumos.sycophanthex.com", ""),
        new("Sink Into Your Eyes", "https://www.siye.co.uk/", "www.siye.co.uk",
            @"/(?:siye/)?viewstory\.php\?sid=(?<work>[0-9]+)",
            @"/(?:siye/)?viewstory\.php\?sid=(?<work>[0-9]+)(?:&chapter=(?<part>[0-9]+))?(?:&textsize=[-0-9]+)?",
            "//span[@style='font-size: 100%;']")
        {
            QueryIsRoute = true,
            WorkTitle = "//h3[font/a[contains(@href,'viewuser.php')]]/text()[normalize-space()][1]",
            ChapterTitle = "//h3[font/a[contains(@href,'viewuser.php')]]/text()[normalize-space()][1]",
            Contents = "//a[contains(@href,'chapter=') and not(contains(@href,'textsize=')) and not(contains(@href,'action='))]",
            AllowStandalone = "//span[@style='font-size: 100%;']",
            WorkFromChapter = "/viewstory.php?sid=${work}"
        },
        new("EFP Fanfiction", "https://efpfanfic.net/search.php?action=recent", "efpfanfic.net",
            @"/viewstory\.php\?sid=(?<work>[0-9]+)(?:&i=1)?",
            @"/viewstory\.php\?sid=(?<work>[0-9]+)(?:&i=(?<part>[1-9][0-9]*))?",
            "//article")
        {
            QueryIsRoute = true,
            WorkTitle = "//title",
            ChapterTitle = "//title",
            Contents = "//select[@name='sid']/option[contains(@value,'viewstory.php')]",
            ContentsDefinesChapterMembership = true,
            AllowStandalone = "//article"
        },
        new("Star Trek Fan Fiction", "https://trekfanfiction.net/category/the-next-generation/", "trekfanfiction.net",
            @"/(?<work>(?!(?:category|tag|dtremel|wp-admin|wp-content)/)[a-z0-9-]+/[a-z0-9-]+)/?", "(?!)",
            "//div[contains(concat(' ',normalize-space(@class),' '),' entry-content ')]")
        {
            WorkTitle = "//h1[contains(@class,'entry-title')]",
            ChapterTitle = "//h1[contains(@class,'entry-title')]",
            AllowStandalone = "//div[contains(concat(' ',normalize-space(@class),' '),' entry-content ')]"
        },
        new("Second Life Translations", "https://secondlifetranslations.com/translations/", "secondlifetranslations.com",
            @"/novel/(?<work>[a-z0-9-]+)/?", @"/(?<work>[a-z0-9-]+)/(?<part>[a-z0-9.-]+)/?", EntryContent)
        {
            FirstChapter = "//a[@class='startreadingbtn' and @href]",
            NextChapter = "//li[@class='srznavlinext']/a[@href and @href!='#']",
            ExcludeContent = ".//ul[@class='srznavul']",
            WorkFromChapter = "/novel/${work}/"
        },
    ]);

    private static PublicFictionSite EfictionArchive(string name, string host, string directory)
    {
        string story = directory + "/viewstory.php";
        string route = directory + @"/viewstory\.php\?sid=(?<work>[0-9]+)";
        const string fiction = "//*[@id='story'] | //span[@style='font-size: 100%;']";
        return new PublicFictionSite(name, "https://" + host + directory + "/browse.php?type=titles", host,
            route, route + @"(?:&textsize=[-0-9]+)?(?:&chapter=(?<part>[1-9][0-9]*))?(?:&textsize=[-0-9]+)?",
            fiction)
        {
            QueryIsRoute = true,
            WorkTitle = "//title",
            ChapterTitle = "//title",
            Contents = "//a[contains(@href,'viewstory.php') and contains(@href,'chapter=') and not(contains(@href,'action=')) " +
                "and not(contains(@href,'textsize='))] | " +
                "//select[@name='chapter']/option[contains(@value,'viewstory.php')]",
            NextChapter = "//*[@id='next']/a[@href and contains(@href,'chapter=')] | " +
                "//a[@class='next' and contains(@href,'chapter=')]",
            AllowStandalone = fiction,
            WorkFromChapter = story + "?sid=${work}"
        };
    }
}
