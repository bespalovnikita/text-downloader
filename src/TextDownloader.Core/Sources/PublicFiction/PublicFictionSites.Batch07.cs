namespace TextDownloader.Core;

internal static partial class PublicFictionSites
{
    private const string SquarespaceReader = "//article//div[contains(concat(' ',normalize-space(@class),' '),' sqs-html-content ') " +
        "and not(ancestor::footer) and string-length(normalize-space(.))>100]";

    static partial void AddBatch07(Dictionary<int, PublicFictionSite[]> batches) => batches.Add(7,
    [
        new("Ckanda Writes", "https://ckandawrites.online/az-lists/", "ckandawrites.online",
            @"/(?![a-z0-9-]+-(?:chapter|extra)-[a-z0-9.-]+/?$)(?<work>[a-z0-9-]+)/?",
            @"/(?<work>[a-z0-9-]+?)-(?<part>(?:chapter|extra)-[a-z0-9.-]+)/?",
            "//div[contains(concat(' ',normalize-space(@class),' '),' epcontent ')]")
        {
            FirstChapter = "//div[contains(@class,'inepcx')]/a[@href and starts-with(normalize-space(.),'First Chapter')]",
            FirstChapterMayUseDifferentSlug = true,
            NextChapter = NextByRel
        },
        new("Gravity Tales", "https://gravitytales.com/", "gravitytales.com",
            @"/story/(?<work>[a-z0-9-]+)/?", @"/story/(?<work>[a-z0-9-]+)/(?<part>[a-z0-9.-]+)/?",
            "//section[contains(concat(' ',normalize-space(@class),' '),' chapter__content ')]")
        {
            Contents = "//ol[contains(@class,'chapter-group__list')]//a[contains(@class,'chapter-group__list-item-link') and " +
                "(starts-with(@href,'/story/') or starts-with(@href,'https://gravitytales.com/story/'))]",
            WorkFromChapter = "/story/${work}/"
        },
        new("Fringe Capybara", "https://www.fringecapybara.com/", "www.fringecapybara.com",
            @"/(?<work>[a-z0-9-]+-toc)/?", @"/(?<work>[a-z0-9-]+)/(?<part>(?:ch|chapter)[a-z0-9.-]+)/?",
            SquarespaceReader)
        {
            WorkTitle = "//title",
            Contents = "//main//a[@href and (contains(@href,'/ch-') or contains(@href,'/ch0') or contains(@href,'/chapter'))]",
            ContentsDefinesChapterMembership = true
        },
        new("Snowy Codex", "https://snowycodex.com/novels/", "snowycodex.com",
            @"/novels/(?<work>[a-z0-9-]+)/?", @"/novels/(?<work>[a-z0-9-]+)/(?<part>[a-z0-9.-]+)/?", EntryContent)
        {
            WorkTitle = "//h1[@class='entry-title']",
            ChapterTitle = "//h1[@class='entry-title'] | //title",
            FirstChapter = "(//div[contains(concat(' ',normalize-space(@class),' '),' entry-content ')]//a[@href and " +
                "(contains(@href,'/chapter-1/') or contains(@href,'/prologue/'))])[1]",
            NextChapter = "//a[@href and (normalize-space(.)='Next Chapter' or normalize-space(.)='Next' or @rel='next')]",
            WorkFromChapter = "/novels/${work}/"
        },
        new("Sunovels", "https://sunovels.com/", "sunovels.com",
            @"/novel/(?<work>[a-z0-9-]+)/?", @"/novel/(?<work>[a-z0-9-]+)/(?<part>[0-9]+(?:\.[0-9]+)?)/?",
            "//div[contains(concat(' ',normalize-space(@class),' '),' chapter-content ')]")
        {
            FirstChapter = "(//div[contains(@class,'header-links')]//a[@href and contains(@href,'/novel/')])[1]",
            NextChapter = "//a[@class='nextchap' and @href and @href!='#']",
            CanonicalMayBeSiteHomepage = true,
            WorkFromChapter = "/novel/${work}"
        },
        new("Athena Translations", "https://athenatls.com/novels/", "athenatls.com",
            @"/novel/(?<work>[a-z0-9-]+)/?", @"/(?<work>[a-z0-9-]+?)-(?<part>(?:vol|volume|chapter|prologue|epilogue|extra)-[a-z0-9-]+)/?",
            "//*[@id='chapter-content']")
        {
            CatalogLinks = "//a[contains(concat(' ',normalize-space(@class),' '),' i2-cover ') and @href]",
            Contents = "//a[contains(concat(' ',normalize-space(@class),' '),' i2-ch ') and starts-with(@href,'https://athenatls.com/') and " +
                "(contains(@href,'-chapter-') or contains(@href,'-prologue') or contains(@href,'-epilogue') or contains(@href,'-extra-'))]",
            ContentsDefinesChapterMembership = true
        },
        new("FictionHunt", "https://fictionhunt.com/", "fictionhunt.com",
            @"/stories/(?<work>[a-z0-9]+/[a-z0-9-]+)/?", @"/stories/(?<work>[a-z0-9]+/[a-z0-9-]+)/chapters/(?<part>[0-9]+)/?",
            "//*[contains(concat(' ',normalize-space(@class),' '),' js-chapter-content ') or @id='chapter-content']")
        {
            ChapterTitle = "//h2[contains(@class,'js-chapter-title')] | //title",
            Contents = "//a[contains(@href,'/chapters/') and @href]",
            WorkFromChapter = "/stories/${work}"
        },
        new("FicWad", "https://ficwad.com/category/44", "ficwad.com",
            @"/story/(?<work>[0-9]+)/?", @"/story/(?<work>(?<part>[0-9]+))/?", "//*[@id='storytext']")
        {
            WorkTitle = "//title",
            ChapterTitle = "//title",
            Contents = "//*[@id='storytext']//ol//a[starts-with(@href,'/story/') and not(contains(@href,'/reviews'))]",
            ContentsDefinesChapterMembership = true,
            AllowStandalone = "//*[@id='storytext']"
        },
        new("Fuyu Neko Translations", "https://www.fuyuneko.org/work", "www.fuyuneko.org",
            @"/(?<work>[a-z][a-z0-9]{2,10})/?",
            @"/(?:alpaca|bamboo-spirit|blog-1|blog-2|capybara|fuyu-02|manatee)/(?<work>[a-z0-9-]+)/(?<part>[a-z0-9.-]+)/?",
            SquarespaceReader)
        {
            CatalogLinks = "//div[contains(@class,'project-description')]//a[@href and not(contains(@href,'?'))] | " +
                "//li[contains(@class,'page-collection')]/a[@href]",
            WorkTitle = "//title",
            ChapterTitle = "//title",
            Contents = "//div[contains(@class,'sqs-html-content')]//a[@href and (contains(@href,'/ch-') or contains(@href,'/ch0'))]",
            SortContentsByChapterNumber = true,
            WorkFromChapter = "/${work}"
        },
        new("Lois & Clark Fanfic Archive", "https://www.lcfanfic.com/noscript/title/a.html", "www.lcfanfic.com",
            @"/stories/(?<work>[0-9]{4}/html/[a-zA-Z0-9_-]+)\.html", "(?!)", "//main")
        {
            AllowStandalone = "//main[.//p]",
            WorkTitle = "//main//h1 | //h1",
            Author = "//main//a[starts-with(@href,'/stories/author/')]",
            ExcludeContent = ".//header | .//footer | .//h1"
        }
    ]);
}
