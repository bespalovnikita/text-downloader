namespace TextDownloader.Core;

internal static partial class PublicFictionSites
{
    static partial void AddBatch06(Dictionary<int, PublicFictionSite[]> batches) => batches.Add(6,
    [
        new("Royal Road", "https://www.royalroad.com/fictions/best-rated", "www.royalroad.com",
            @"/fiction/(?<work>[0-9]+)(?:/[^/]+)?/?",
            @"/fiction/(?<work>[0-9]+)/(?<slug>[^/]+)/chapter/(?<part>[0-9]+)(?:/[^/]+)?/?",
            "//div[contains(concat(' ',normalize-space(@class),' '),' chapter-content ')]")
        {
            Contents = "//table[@id='chapters']//tr[contains(@class,'chapter-row')]/td[1]/a[@href]",
            Author = "//meta[@property='books:author']",
            WorkFromChapter = "/fiction/${work}/${slug}"
        },
        new("Fanfictions.fr", "https://www.fanfictions.fr/", "www.fanfictions.fr",
            @"/fanfictions/(?<work>[^/]+/[0-9]+_[^/]+)/chapters\.html",
            @"/fanfictions/(?<work>[^/]+/[0-9]+_[^/]+)/(?<part>[0-9]+_[^/]+)/lire\.html",
            "//*[@id='readarea']")
        {
            WorkTitle = "//main//h1[1] | //meta[@property='og:title']",
            ChapterTitle = "//title",
            Contents = "//h2/a[contains(@href,'/lire.html')]",
            WorkFromChapter = "/fanfictions/${work}/chapters.html"
        },
        new("FanFiktion.de", "https://www.fanfiktion.de/latest", "www.fanfiktion.de",
            @"/s/(?<work>[a-f0-9]+)/1(?:/[^/]+)?/?",
            @"/s/(?<work>[a-f0-9]+)/(?<part>[1-9][0-9]*)(?:/[^/]+)?/?",
            "//*[@id='storytext']")
        {
            WorkTitle = "//div[contains(@class,'story-left')]//h1 | //title",
            ChapterTitle = "(//select)[1]/option[@selected] | //title",
            ChapterOptions = "(//select)[1]/option",
            ChapterFromOption = "/s/${work}/{number}",
            WorkFromChapter = "/s/${work}/1"
        },
        new("Sink Into Your Eyes", "https://www.siye.co.uk/", "www.siye.co.uk",
            @"/(?:siye/)?viewstory\.php\?sid=(?<work>[0-9]+)",
            @"/(?:siye/)?viewstory\.php\?sid=(?<work>[0-9]+)(?:&chapter=(?<part>[0-9]+))?(?:&textsize=[-0-9]+)?",
            "//span[@style='font-size: 100%;']")
        {
            QueryIsRoute = true,
            WorkTitle = "//title",
            ChapterTitle = "//title",
            Contents = "//a[contains(@href,'&chapter=') and not(contains(@href,'&textsize=')) and not(contains(@href,'action='))]",
            AllowStandalone = "//span[@style='font-size: 100%;']",
            WorkFromChapter = "/viewstory.php?sid=${work}"
        },
        new("EFP Fanfiction", "https://efpfanfic.net/browse.php?type=recent", "efpfanfic.net",
            @"/viewstory\.php\?sid=(?<work>[0-9]+)(?:&i=1)?",
            @"/viewstory\.php\?sid=(?<work>[0-9]+)(?:&i=(?<part>[1-9][0-9]*))?",
            "//div[@class='storia']")
        {
            QueryIsRoute = true,
            WorkTitle = "//title",
            ChapterTitle = "//title",
            Contents = "//select[@name='sid']/option[contains(@value,'viewstory.php')]",
            AllowStandalone = "//div[@class='storia']",
            WorkFromChapter = "/viewstory.php?sid=${work}"
        },
        new("Twisting the Hellmouth", "https://www.tthfanfic.org/latest.php", "www.tthfanfic.org",
            @"/(?:T-[0-9]+/)?Story-(?<work>[0-9]+)(?:/[^/]+\.htm)?/?",
            @"/(?:T-[0-9]+/)?Story-(?<work>[0-9]+)(?:-(?<part>[1-9][0-9]*))?(?:/[^/]+\.htm)?/?",
            "//*[@id='storyinnerbody']")
        {
            WorkTitle = "//title",
            ChapterTitle = "//*[@id='storyinnerbody']/h3 | //title",
            Contents = "//select/option[contains(@value,'Story-')]",
            AllowStandalone = "//*[@id='storyinnerbody']",
            ExcludeContent = ".//h3[1]",
            WorkFromChapter = "/Story-${work}"
        },
        new("Central Novel", "https://centralnovel.com/", "centralnovel.com",
            @"/series/(?<work>[a-z0-9-]+)/?",
            @"/(?<work>[a-z0-9-]+?)-(?<part>capitulo-[a-z0-9.-]+)/?",
            "//div[contains(concat(' ',normalize-space(@class),' '),' epcontent ')]")
        {
            FirstChapter = "//div[contains(@class,'inepcx')]/a[starts-with(normalize-space(.),'Primeiro Capítulo') and @href]",
            NextChapter = NextByRel,
            WorkFromChapter = "/series/${work}/"
        },
        new("Tsundoku", "https://tsundoku.com.br/", "tsundoku.com.br",
            @"/manga/(?<work>[a-z0-9-]+)/?",
            @"/(?<work>[a-z0-9-]+?)-(?<part>(?:capitulo|vol|prologo)-[a-z0-9.-]+)/?",
            "//*[@id='readerarea' and contains(@class,'novelarea')]")
        {
            Contents = "//*[@id='chapterlist']//li//a[not(contains(@href,'ilustrac')) and @href]",
            ReverseContents = true,
            WorkFromChapter = "/manga/${work}/"
        },
        new("Asian Hobbyist", "https://www.asianhobbyist.com/", "www.asianhobbyist.com",
            @"/series/(?<work>[a-z0-9-]+)/?", @"/(?<work>[a-z0-9-]+)/(?<part>[a-z0-9.-]+)/?",
            EntryContent)
        {
            WorkTitle = "//h1[contains(@class,'entry-title')]",
            ChapterTitle = "//h2[contains(@class,'fnt-18')] | //h1[contains(@class,'entry-title')]",
            FirstChapter = "//a[normalize-space(.)='Read First Chapter' and @href]",
            NextChapter = "//a[contains(@class,'nextprev') and normalize-space(.)='Next' and @href]",
            WorkFromChapter = "/series/${work}/"
        },
        new("顶点小说", "https://www.ddtxt8.cc/", "www.ddtxt8.cc",
            @"/book/(?<work>[0-9]+)/?", @"/book/(?<work>[0-9]+)/(?<part>[0-9]+)\.html",
            "//*[@id='content']")
        {
            FirstChapter = "//a[@class='rl' and @href]",
            NextChapter = "//a[normalize-space(.)='下一章' and @href and @href!='#']",
            WorkFromChapter = "/book/${work}/"
        },
    ]);
}
