namespace TextDownloader.Core;

internal static partial class PublicFictionSites
{
    static partial void AddBatch06(Dictionary<int, PublicFictionSite[]> batches) => batches.Add(6,
    [
        new("BabelNovel", "https://babelnovel.com/", "babelnovel.com",
            @"/books/(?<work>[a-z0-9-]+)/?", @"/books/(?<work>[a-z0-9-]+)/chapters/(?<part>c[0-9]+(?:-[0-9]+)?)/?",
            "//article[starts-with(@class,'chapter_container')]/section")
        {
            ChapterTitle = "//title",
            FirstChapter = "(//a[contains(@class,'chapter-item_container') and " +
                "substring(@href,string-length(@href)-2)='/c1'])[1]",
            NextChapter = "//a[@rel='next' and @href and @href!='#']",
            WorkFromChapter = "/books/${work}"
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
        new("RanobeHub", "https://ranobehub.org/", "ranobehub.org",
            @"/ranobe/(?<work>[a-z0-9-]+)/?", @"/ranobe/(?<work>[a-z0-9-]+)/chapter/(?<part>[0-9]+)/?",
            "//div[contains(concat(' ',normalize-space(@class),' '),' reader-content ')]")
        {
            Contents = "//div[contains(@class,'book-chapter-viewport') and contains(@class,'is-complete')]" +
                "//a[starts-with(@id,'chapter-list-') and @href]",
            ReverseContents = true,
            WorkFromChapter = "/ranobe/${work}"
        },
        new("FAQWiki", "https://faqwiki.xyz/", "faqwiki.xyz",
            @"/(?![a-z0-9-]+-chapter-[a-z0-9.-]+/?$)(?<work>[a-z0-9-]+)/?",
            @"/(?<work>[a-z0-9-]+?)-(?<part>chapter-[a-z0-9.-]+)/?", EntryContent)
        {
            CatalogLinks = "//div[contains(@class,'entry-content')]//a[@href]",
            WorkTitle = "//h1[@class='entry-title']",
            ChapterTitle = "//h1[@class='entry-title']",
            FirstChapter = "(//div[contains(@class,'entry-content')]//a[contains(@href,'-chapter-1/') and @href])[1]",
            NextChapter = "//a[contains(concat(' ',normalize-space(@class),' '),' next-chapter ') and @href and @href!='#']",
            ExcludeContent = ".//div[contains(@class,'code-block')]",
            WorkFromChapter = "/${work}/"
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
        new("Exiled Rebels", "https://exiledrebelsscanlations.com/novels/", "exiledrebelsscanlations.com",
            @"/novels/(?<work>[a-z0-9-]+)/?",
            @"/(?<work>[a-z0-9-]+?)-(?<part>chapter-[a-z0-9.-]+)/?",
            EntryContent)
        {
            FirstChapter = "(//div[contains(@class,'lcp_catlist')]//a[contains(@href,'-chapter-1/') and @href] | " +
                "//div[contains(@class,'entry-content')]//a[contains(@href,'-chapter-1/') and @href])[1]",
            FirstChapterMayUseDifferentSlug = true,
            NextChapter = "//a[normalize-space(.)='Next Chapter' and @href and @href!='#']",
            ExcludeContent = ".//div[contains(@class,'sharedaddy')]",
            WorkTitle = "//h1[@class='entry-title']"
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
        new("The Original Naruto Fanfiction Archive", "https://narutofic.org/browse.php?type=titles", "narutofic.org",
            @"/viewstory\.php\?sid=(?<work>[0-9]+)",
            @"/viewstory\.php\?sid=(?<work>[0-9]+)(?:&textsize=[-0-9]+)?(?:&chapter=(?<part>[1-9][0-9]*))?(?:&textsize=[-0-9]+)?",
            "//*[@id='story']")
        {
            QueryIsRoute = true,
            WorkTitle = "//title",
            ChapterTitle = "//title",
            Contents = "//*[@id='chapters']//a[contains(@href,'&chapter=')] | " +
                "//select[@name='chapter']/option[contains(@value,'viewstory.php')]",
            NextChapter = "//*[@id='next']/a[@href and contains(@href,'&chapter=')] | " +
                "//a[@class='next' and contains(@href,'&chapter=')]",
            AllowStandalone = "//*[@id='story']",
            WorkFromChapter = "/viewstory.php?sid=${work}"
        },
    ]);
}
