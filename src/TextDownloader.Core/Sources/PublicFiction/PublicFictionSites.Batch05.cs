namespace TextDownloader.Core;

internal static partial class PublicFictionSites
{
    static partial void AddBatch05(Dictionary<int, PublicFictionSite[]> batches) => batches.Add(5,
    [
        new("KnoxT", "https://knoxt.space/", "knoxt.space",
            @"/(?![a-z0-9-]+-chapter-[a-z0-9.-]+/?$)(?<work>[a-z0-9-]+)/?",
            @"/(?<work>[a-z0-9-]+?)-(?<part>chapter-[a-z0-9.-]+)/?",
            "//div[contains(concat(' ',normalize-space(@class),' '),' epcontent ')]")
        {
            FirstChapter = "//li[@class='tseplsfrst']/a[@href]",
            NextChapter = NextByRel,
            WorkFromChapter = "/${work}/"
        },
        new("Dummy Novels", "https://dummynovels.com/", "dummynovels.com",
            @"/novel/(?<work>[a-z0-9-]+)/?", @"/novel/(?<work>[a-z0-9-]+)/(?<part>[a-z0-9.-]+)/?",
            "//div[@data-widget_type='theme-post-content.default']/div[@class='elementor-widget-container']")
        {
            FirstChapter = "//a[@class='readmore-btn-link' and @href]",
            NextChapter = "//div[@class='chapter-nav-next']/a[@href and @href!='#']",
            WorkFromChapter = "/novel/${work}/"
        },
        new("Novel543", "https://www.novel543.com/", "www.novel543.com",
            @"/(?<work>[0-9]+)/?", @"/(?<work>[0-9]+)/(?<part>[0-9]+_[0-9]+(?:[-_][0-9]+)?)\.html",
            "//*[@id='chapterWarp']//*[contains(concat(' ',normalize-space(@class),' '),' content ')]")
        {
            FirstChapter = "//a[normalize-space(.)='開始閱讀' and @href]",
            NextChapter = "(//a[normalize-space(.)='下一頁' and @href] | " +
                "//a[normalize-space(.)='下一章' and @href][not(//a[normalize-space(.)='下一頁' and @href])])[1]",
            WorkFromChapter = "/${work}/"
        },
        new("Mira’s Cocktail", "https://www.miraslation.net/", "www.miraslation.net",
            @"/novels/(?<work>[a-z0-9-]+)/?", @"/novels/(?<work>[a-z0-9-]+)/chapters/(?<part>[0-9]+/[a-z0-9.-]+)/?",
            "//main[contains(concat(' ',normalize-space(@class),' '),' prose ')]")
        {
            WorkTitle = "//h1[contains(@class,'decoration-primary')]",
            ChapterTitle = "//h1[contains(@class,'decoration-primary')]",
            Contents = "//main//a[contains(@href,'/chapters/') and @href]",
            WorkFromChapter = "/novels/${work}/"
        },
        new("Hyacinth in Bloom", "https://hyacinthbloom.com/", "hyacinthbloom.com",
            @"/series/(?<work>[a-z0-9-]+)/?", @"/(?<work>[a-z0-9-]+)/(?<part>[a-z0-9.-]+)/?",
            "//div[contains(concat(' ',normalize-space(@class),' '),' epcontent ')]")
        {
            FirstChapter = "//div[contains(@class,'inepcx')]/a[@href and starts-with(normalize-space(.),'First Chapter')]",
            NextChapter = NextByRel,
            WorkFromChapter = "/series/${work}/"
        },
        new("JrNovels", "https://jrnovels.com/", "jrnovels.com",
            @"/novel/(?<work>[a-z0-9-]+)/?", @"/novel/(?<work>[a-z0-9-]+)/(?<part>[0-9]+)/?",
            "//div[contains(concat(' ',normalize-space(@class),' '),' text-dark ') and contains(@class,'bg-white')]")
        {
            WorkTitle = "//meta[@property='og:title']",
            ChapterTitle = "//meta[@property='og:title']",
            FirstChapter = "//a[normalize-space(.)='ALL CHAPTERS' and @href]",
            NextChapter = "//ul[contains(@class,'pagination')]/li[contains(@class,'active')]/following-sibling::li[1]/a[@href]",
            ExcludeContent = ".//ul[contains(@class,'pagination')]",
            WorkFromChapter = "/novel/${work}"
        },
        new("BakaPervert", "https://bakapervert.wordpress.com/", "bakapervert.wordpress.com",
            @"/(?![a-z0-9-]+-chapter-[a-z0-9.-]+/?$)(?<work>[a-z0-9-]+)/?",
            @"/(?<work>[a-z0-9-]+?)-(?<part>chapter-[a-z0-9.-]+)/?",
            EntryContent)
        {
            CatalogLinks = "//ul[contains(@class,'sub-menu')]/li/a[@href]",
            WorkTitle = "//h1[@class='entry-title']",
            ChapterTitle = "//h1[@class='entry-title']",
            FirstChapter = "(//div[contains(@class,'entry-content')]//a[contains(@href,'-chapter-1/') and @href])[1]",
            NextChapter = "//div[contains(@class,'entry-content')]//a[normalize-space(.)='Next' and @href and @href!='#']",
            ExcludeContent = ".//div[contains(@class,'sharedaddy')]",
            WorkFromChapter = "/${work}/"
        },
        new("Demon Translations", "https://demontranslations.com/", "demontranslations.com",
            @"/(?<work>[a-z0-9-]+)/?", @"/(?<work>[a-z0-9-]+)/(?<part>chapter-[a-z0-9.-]+)/?",
            EntryContent)
        {
            CatalogLinks = "//ul[contains(@class,'sub-menu')]/li/a[@href]",
            WorkTitle = "//h1[@class='entry-title']",
            ChapterTitle = "//h1[@class='entry-title']",
            FirstChapter = "(//div[contains(@class,'entry-content')]//a[contains(@href,'/chapter-0001/') or contains(@href,'/chapter-1/')])[1]",
            NextChapter = "//div[contains(@class,'entry-content')]//a[starts-with(normalize-space(.),'Next') and @href and @href!='#']",
            ExcludeContent = ".//div[contains(@class,'sharedaddy')]",
            WorkFromChapter = "/${work}/"
        },
        new("Shanghai Fantasy", "https://shanghaifantasy.com/", "shanghaifantasy.com",
            @"/novel/(?<work>[a-z0-9-]+)/?", @"/(?<work>[a-z0-9-]+?)-(?<part>chapter-[a-z0-9.-]+)/?",
            "//div[contains(concat(' ',normalize-space(@class),' '),' contenta ')]")
        {
            WorkTitle = "//title",
            ChapterTitle = "//title",
            FirstChapter = "//a[normalize-space(.)='Start Reading' and @href]",
            NextChapter = NextByRel,
            WorkFromChapter = "/novel/${work}/"
        },
        new("Chireads", "https://chireads.com/category/translatedtales/", "chireads.com",
            @"/translatedtales/(?<work>[^/]+)/?", @"/translatedtales/(?<work>[^/]+)/(?<part>[^/]+/[0-9]{4}/[0-9]{2}/[0-9]{2})/?",
            "//*[@id='content' and contains(@class,'article-font')]")
        {
            WorkTitle = "//h3[contains(@class,'inform-title')]",
            ChapterTitle = "//title",
            Contents = "//div[@class='chapitre']//a[contains(@href,'/translatedtales/')]",
            WorkFromChapter = "/translatedtales/${work}/"
        },
    ]);
}
