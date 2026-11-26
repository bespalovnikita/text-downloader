namespace TextDownloader.Core;

internal static partial class PublicFictionSites
{
    static partial void AddBatch09(Dictionary<int, PublicFictionSite[]> batches) => batches.Add(9,
    [
        new("69书吧", "https://www.69shuba.com/", "www.69shuba.com",
            @"/book/(?<work>[0-9]+)\.htm", @"/txt/(?<work>[0-9]+)/(?<part>[0-9]+)\.htm",
            "//div[contains(concat(' ',normalize-space(@class),' '),' txtnav ')]")
        {
            WorkTitle = "//div[contains(@class,'booknav2')]//h1",
            FirstChapter = "//a[normalize-space(.)='开始阅读' and contains(@href,'.htm')]",
            NextChapter = "//a[normalize-space(.)='下一章' and @href and @href!='#']",
            ExcludeContent = ".//h1 | .//div[contains(@class,'txtinfo')] | .//*[@id='txtright']",
            WorkFromChapter = "/book/${work}.htm"
        },
        new("半夏小說", "https://www.xbanxia.cc/", "www.xbanxia.cc",
            @"/books/(?<work>[0-9]+)\.html", @"/books/(?<work>[0-9]+)/(?<part>[0-9]+(?:_[0-9]+)?)\.html",
            "//*[@id='nr1']")
        {
            WorkTitle = "//div[contains(@class,'book-describe')]//h1",
            Contents = "//div[contains(@class,'book-list')]//li/a[@href]",
            WorkFromChapter = "/books/${work}.html"
        },
        new("SF轻小说", "https://book.sfacg.com/", "book.sfacg.com",
            @"/Novel/(?<work>[0-9]+)/?", @"/Novel/(?<work>[0-9]+)/(?<part>[0-9]+/[0-9]+)/?",
            "//*[@id='ChapterBody']")
        {
            WorkTitle = "//meta[@property='og:title'] | //h1[1]",
            FirstChapter = "//a[contains(@href,'/Novel/') and normalize-space(.)='立即阅读']",
            NextChapter = "//a[normalize-space(.)='下一章' and @href and @href!='#']",
            WorkFromChapter = "/Novel/${work}/"
        },
        new("17K小说网", "https://www.17k.com/", "www.17k.com",
            @"/book/(?<work>[0-9]+)\.html", @"/chapter/(?<work>[0-9]+)/(?<part>[0-9]+)\.html",
            "//*[@id='readArea']//div[contains(concat(' ',normalize-space(@class),' '),' p ')]")
        {
            FirstChapter = "//a[normalize-space(.)='开始阅读' and contains(@href,'/chapter/')]",
            NextChapter = "//a[normalize-space(.)='下一章' and @href and @href!='#']",
            WorkFromChapter = "/book/${work}.html"
        },
        new("晋江文学城", "https://www.jjwxc.net/", "www.jjwxc.net",
            @"/onebook\.php\?novelid=(?<work>[0-9]+)", @"/onebook\.php\?novelid=(?<work>[0-9]+)&chapterid=(?<part>[0-9]+)",
            "//div[contains(concat(' ',normalize-space(@class),' '),' noveltext ')]")
        {
            QueryIsRoute = true,
            WorkTitle = "//span[@itemprop='articleSection'] | //title",
            ChapterTitle = "//title",
            Contents = "//table[@id='oneboolt']//a[contains(@href,'&chapterid=')]",
            ExcludeContent = ".//div[contains(@class,'noveltitle') or contains(@class,'readsmall')]",
            WorkFromChapter = "/onebook.php?novelid=${work}"
        },
        new("飞卢小说网", "https://b.faloo.com/", "b.faloo.com",
            @"/(?<work>[0-9]+)\.html", @"/(?<work>[0-9]+)_(?<part>[0-9]+)\.html",
            "//*[@id='content' or @id='n_content']")
        {
            CatalogLinks = "//a[contains(@href,'.html') and @href]",
            FirstChapter = "(//a[contains(@href,'_1.html') and @href])[1]",
            NextChapter = "//a[normalize-space(.)='下一章' and @href and @href!='#']",
            WorkFromChapter = "/${work}.html"
        },
        new("Luminare Translations", "https://luminaretranslations.com/", "luminaretranslations.com",
            @"/series/(?<work>[a-z0-9-]+)/?",
            @"/series/(?<work>[a-z0-9-]+)/(?<part>[a-z0-9.-]+)/?",
            "//article//div[contains(concat(' ',normalize-space(@class),' '),' chapter-content ')] | " + EntryContent)
        {
            FirstChapter = "//a[(normalize-space(.)='Start Reading' or normalize-space(.)='Read First') and @href]",
            NextChapter = NextByRel,
            WorkFromChapter = "/series/${work}/"
        },
        new("Exiled Rebels", "https://exiledrebelsscanlations.com/novels/", "exiledrebelsscanlations.com",
            @"/novels/(?<work>[a-z0-9-]+)/?",
            @"/(?<work>[a-z0-9-]+?)-(?<part>chapter-[a-z0-9.-]+)/?",
            EntryContent)
        {
            FirstChapter = "(//div[contains(@class,'entry-content')]//a[contains(@href,'-chapter-1/') and @href])[1]",
            NextChapter = "//a[normalize-space(.)='Next Chapter' and @href and @href!='#']",
            ExcludeContent = ".//div[contains(@class,'sharedaddy')]",
            WorkFromChapter = "/novels/${work}/"
        },
        new("ReadLightNovel", "https://www.readlightnovel.org/", "www.readlightnovel.org",
            @"/(?<work>[a-z0-9-]+)/?", @"/(?<work>[a-z0-9-]+)/(?<part>chapter-[a-z0-9.-]+)/?",
            "//div[contains(@class,'chapter-content3')]//div[contains(concat(' ',normalize-space(@class),' '),' desc ')]")
        {
            CatalogLinks = "//h3/a[@href]",
            WorkTitle = "//div[contains(@class,'block-title')]/h1",
            ChapterTitle = "//div[contains(@class,'chapter-content3')]//h3 | //title",
            Contents = "//div[contains(@class,'chapter-list')]//a[@href]",
            WorkFromChapter = "/${work}"
        },
        new("RPG Novels", "https://rpgnovels.com/active-projects/", "rpgnovels.com",
            @"/(?<work>[a-z0-9-]+)/?", @"/(?<work>[a-z0-9-]+)/(?<part>[a-z0-9.-]+)/?",
            EntryContent)
        {
            CatalogLinks = "//div[contains(@class,'entry-content')]//a[@href]",
            WorkTitle = "//h1[contains(@class,'entry-title')]",
            ChapterTitle = "//h1[contains(@class,'entry-title')]",
            FirstChapter = "(//div[contains(@class,'entry-content')]//a[starts-with(normalize-space(.),'Chapter 1') and @href])[1]",
            NextChapter = "//a[normalize-space(.)='Next' and @href and @href!='#']",
            WorkFromChapter = "/${work}/"
        },
    ]);
}
