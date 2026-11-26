namespace TextDownloader.Core;

internal static partial class PublicFictionSites
{
    private const string EntryContent = "//div[contains(concat(' ',normalize-space(@class),' '),' entry-content ')]";

    private static readonly PublicFictionSite[] FourthBatch =
    [
        new("Dusk Blossoms", "https://duskblossoms.com/", "duskblossoms.com",
            @"/novel/(?<work>[a-z0-9-]+)/?", @"/novel/(?<work>[a-z0-9-]+)/(?<part>[a-z0-9.-]+)/?", MadaraText)
        {
            FirstChapter = "//a[@id='btn-read-last' and @href and @href!='#']",
            NextChapter = "//a[contains(concat(' ',normalize-space(@class),' '),' next_page ') and @href and @href!='#']",
            WorkFromChapter = "/novel/${work}/",
            CanonicalMayIdentifyWork = true
        },
        new("Virlyce", "https://www.virlyce.com/", "www.virlyce.com",
            @"/(?<work>[a-z0-9-]+)/?", @"/(?<work>[a-z0-9-]+)/(?<part>[a-z0-9-]*chapter-[a-z0-9.-]+)/?", EntryContent)
        {
            CatalogLinks = "//ul[contains(@class,'sub-menu')]/li/a[@href]",
            WorkTitle = "//meta[@property='og:title']",
            ChapterTitle = "//meta[@property='og:title']",
            FirstChapter = "(//div[contains(@class,'entry-content')]//a[contains(@href,'chapter-1/') and @href])[1]",
            NextChapter = "//div[contains(@class,'entry-content')]//a[normalize-space(.)='Next' and @href and @href!='#']",
            ExcludeContent = ".//a[normalize-space(.)='Next' or normalize-space(.)='Previous']",
            WorkFromChapter = "/${work}/"
        },
        new("IndoWebNovel", "https://indowebnovel.id/", "indowebnovel.id",
            @"/series/(?<work>[a-z0-9-]+)/?", @"/(?<work>[a-z0-9-]+?)-(?<part>chapter-[a-z0-9.-]+)/?",
            "//div[@class='tldariinggrissendiribrojangancopy']")
        {
            WorkTitle = "//meta[@property='og:title']",
            ChapterTitle = "//h2[1]",
            FirstChapter = "(//ul[contains(@class,'series-chapterlists')]//a[contains(@href,'-chapter-1-') or contains(@href,'-chapter-1/')])[1]",
            NextChapter = NextByRel,
            WorkFromChapter = "/series/${work}/"
        },
        new("NovelFull.net", "https://novelfull.net/", "novelfull.net",
            @"/(?<work>[a-z0-9-]+)\.html", @"/(?<work>[a-z0-9-]+)/(?<part>[a-z0-9.-]+)\.html",
            "//*[@id='chapter-content']")
        {
            FirstChapter = "//a[normalize-space(.)='Read first' and @href]",
            NextChapter = "//div[@class='reader-next-item']/a[@href and @href!='#']",
            WorkFromChapter = "/${work}.html"
        },
        new("WordRain", "https://wordrain69.com/", "wordrain69.com",
            @"/manga/(?<work>[a-z0-9-]+)/?", @"/manga/(?<work>[a-z0-9-]+)/(?<part>[a-z0-9.-]+)/?",
            MadaraText)
        {
            FirstChapter = "//a[@id='btn-read-last' and @href and @href!='#']",
            NextChapter = "//a[contains(concat(' ',normalize-space(@class),' '),' next_page ') and @href and @href!='#']",
            WorkFromChapter = "/manga/${work}/",
            CanonicalMayIdentifyWork = true
        },
        new("LabNovel", "https://labnovel.ru/", "labnovel.ru",
            @"/(?<work>[a-z0-9-]+)/?", @"/(?<work>[a-z0-9-]+)/(?<part>glava-[a-z0-9.-]+)/?",
            "//div[contains(concat(' ',normalize-space(@class),' '),' editblock ')]")
        {
            FirstChapter = "(//div[contains(@class,'chapter-list')]//a[starts-with(normalize-space(.),'Глава 1:') and @href])[1]",
            NextChapter = "//li[@class='next']/a[@rel='next' and @href and @href!='#']",
            WorkFromChapter = "/${work}/"
        },
        new("MyDramaNovel", "https://mydramanovel.com/", "mydramanovel.com",
            @"/(?<work>[a-z0-9-]+)/?", @"/(?<work>[a-z0-9-]+)/(?<part>chapter-(?<sequence>[0-9]+)(?:-[a-z0-9-]+)?|(?<prologue>prologue)(?:-[a-z0-9-]+)?)/?",
            "//div[contains(concat(' ',normalize-space(@class),' '),' tdb_single_content ')]")
        {
            Contents = "//h3[contains(@class,'entry-title')]/a[contains(@href,'/chapter-') or contains(@href,'/prologue')]",
            SortContentsByChapterNumber = true,
            WorkFromChapter = "/${work}/"
        },
        new("Psychfic", "https://www.psychfic.com/", "www.psychfic.com",
            @"/viewstory\.php\?sid=(?<work>[0-9]+)",
            @"/viewstory\.php\?sid=(?<work>[0-9]+)(?:&textsize=[0-9]+)?(?:&chapter=(?<part>[0-9]+))?",
            "//*[@id='story']")
        {
            QueryIsRoute = true,
            WorkTitle = "//title",
            ChapterTitle = "//title",
            FirstChapter = "//*[@id='output']//a[contains(@href,'&chapter=1') and @href][1]",
            NextChapter = "//*[@id='next']/a[@class='next' and @href]",
            AllowStandalone = "//*[@id='story']",
            WorkFromChapter = "/viewstory.php?sid=${work}"
        },
        new("Portkey Archive", "https://portkey-archive.org/ship/harry-and-hermione", "portkey-archive.org",
            @"/story/(?<work>[0-9]+)/?", @"/story/(?<work>[0-9]+)/(?<part>[0-9]+)/?",
            "//div[@class='chapter-text']")
        {
            Contents = "//li[contains(@class,'chapter-list__item')]/a[@href]",
            Author = "//p[@class='story-head__byline']/a[1]",
            WorkFromChapter = "/story/${work}/"
        },
        new("Stories of Arda", "https://storiesofarda.com/", "storiesofarda.com",
            @"/story/(?<work>[0-9]+)/?", @"/story/(?<work>[0-9]+)/chapter/(?<part>[0-9]+)/?",
            "//main//article[contains(concat(' ',normalize-space(@class),' '),' chapter-text ')]")
        {
            ChapterTitle = "//main//h1[1]",
            Contents = "//main//li[contains(@class,'mb-1')]/a[contains(@href,'/chapter/')]",
            WorkFromChapter = "/story/${work}"
        },
    ];
}
