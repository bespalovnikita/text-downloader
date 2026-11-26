namespace TextDownloader.Core;

internal static partial class PublicFictionSites
{
    private const string SilmarillionTitle = "//div[contains(concat(' ',normalize-space(@class),' '),' views-field-body ')]" +
        "//div[contains(concat(' ',normalize-space(@class),' '),' fanwork-listing ')]/h1";

    static partial void AddBatch10(Dictionary<int, PublicFictionSite[]> batches) => batches.Add(10,
    [
        FanficPlatform("Fanfic.es", "fanfic.es", "/popular"),
        FanficPlatform("Книга Фанфиков", "ficbook.net", "/fanfiction"),
        new("Silmarillion Writers' Guild", "https://www.silmarillionwritersguild.org/most-recent/writing/fiction",
            "www.silmarillionwritersguild.org", @"/node/(?<work>[0-9]+)(?:/fanwork)?/?", "(?!)",
            "//div[contains(concat(' ',normalize-space(@class),' '),' view-content ') and " +
            ".//div[contains(@class,'views-field-field-chapter-text')]]")
        {
            CatalogLinks = "//a[contains(@href,'/fanwork') and @href]",
            WorkTitle = SilmarillionTitle,
            ChapterTitle = SilmarillionTitle,
            ContentsPath = "/node/${work}/fanwork",
            AllowStandalone = "//*[contains(@class,'views-field-field-chapter-text')]",
            ExcludeContent = ".//summary | .//details[./h4[normalize-space(.)='Chapter End Notes']] | " +
                ".//*[contains(concat(' ',normalize-space(@class),' '),' views-field ') " +
                "and not(contains(@class,'views-field-field-chapter-text'))]"
        },
        new("Novel Oku", "https://novelokur.com.tr/rankings/", "novelokur.com.tr",
            @"/novel/(?<work>[a-z0-9-]+)/?", @"/chapter/(?<part>[a-z0-9-]+)/?", "//*[@id='chapterContent']")
        {
            Contents = "//a[contains(concat(' ',normalize-space(@class),' '),' chapter-item ') " +
                "and not(contains(concat(' ',normalize-space(@class),' '),' locked ')) and contains(@href,'/chapter/')]",
            FirstChapter = "(//a[contains(@href,'/chapter/') and contains(normalize-space(.),'OKUMAYA BAŞLA')])[1]",
            ContentsDefinesChapterMembership = true,
            ReverseContents = true
        },
        new("Asianovel", "https://www.asianovel.net/stories/", "www.asianovel.net",
            @"/story/(?<work>[0-9]+)/?", @"/chapter/chapter-(?<part>[0-9]+)/?", "//*[@id='chapter-content']")
        {
            Contents = "//ol[contains(@class,'chapter-group__list')]//a[contains(@class,'chapter-group__list-item-link') and " +
                "(starts-with(@href,'/chapter/chapter-') or starts-with(@href,'https://www.asianovel.net/chapter/chapter-'))]",
            ContentsDefinesChapterMembership = true
        },
        new("WuxiaClick", "https://wuxia.click/browse", "wuxia.click",
            @"/novel/(?<work>[a-z0-9-]+)/?", @"/chapter/(?<work>[a-z0-9-]+?)-(?<part>[0-9]+(?:\.[0-9]+)?)/?",
            "//article[contains(@class,'prose')] | //*[@id='chapter-content']")
        {
            FirstChapter = "(//a[@href and starts-with(normalize-space(.),'Read first chapter')])[1]",
            NextChapter = "//a[@href and contains(@href,'/chapter/') and (@rel='next' or @aria-label='Next chapter' or " +
                "normalize-space(.)='Next' or starts-with(normalize-space(.),'Next Chapter'))]",
            WorkFromChapter = "/novel/${work}"
        },
        DatedTranslationSite("CClaw Translations", "cclawtranslations.home.blog", "/completed-projects/",
            @"/(?<work>[a-z0-9-]+-toc)/?", "contains(@href,'/20') and " +
            "(contains(@href,'chapter') or contains(@href,'prologue') or contains(@href,'epilogue') or contains(@href,'afterword'))"),
        new("Re:Library", "https://re-library.com/translations/", "re-library.com",
            @"/(?<section>translations|originals)/(?<work>[a-z0-9-]+)/?",
            @"/(?<section>translations|originals)/(?<work>[a-z0-9-]+)/(?<part>[a-z0-9-]+(?:/[a-z0-9-]+)?)/?", EntryContent)
        {
            CatalogLinks = "//article[contains(@class,'type-page')]//a[@href and not(contains(@href,'/most-popular/'))]",
            Contents = "//*[@id='content']//article[contains(@class,'type-page')]//a[@href and " +
                "(contains(@href,'/translations/') or contains(@href,'/originals/')) and " +
                "(contains(@href,'/chapter-') or contains(@href,'/prologue') or contains(@href,'/epilogue'))]",
            WorkFromChapter = "/${section}/${work}/"
        },
        new("Infinite Novel Translations", "https://infinitenoveltranslations.net/light-novels/", "infinitenoveltranslations.net",
            @"/(?<work>[a-z0-9-]+)/?", @"/(?<work>[a-z0-9-]+)/(?<part>[a-z0-9-]+(?:/[a-z0-9-]+)?)/?",
            "//div[contains(concat(' ',normalize-space(@class),' '),' bs-card-box ') and starts-with(../@id,'post-')]")
        {
            CatalogLinks = "//div[contains(@class,'bs-card-box')]//a[@href]",
            WorkTitle = "//h1[normalize-space()] | //title",
            ChapterTitle = "//h1[normalize-space()] | //title",
            Contents = "//div[contains(@class,'bs-card-box') and starts-with(../@id,'post-')]//a[@href and " +
                "(contains(@href,'/chapter-') or contains(@href,'/prologue-') or contains(@href,'/epilogue-'))]",
            WorkFromChapter = "/${work}/"
        },
        new("Pure Love Translations", "https://puretl.com/translations", "puretl.com",
            @"/(?<work>[a-z0-9-]+)/?",
            @"/(?<work>[a-z0-9-]+)/(?<part>(?:c|ch|chapter)-?(?<sequence>[0-9]+)(?:[-.][a-z0-9.-]+)?|(?<prologue>prologue))/?",
            "//main//div[contains(concat(' ',normalize-space(@class),' '),' sqs-html-content ') and " +
            "string-length(normalize-space(.))>300]")
        {
            CatalogLinks = "//main//a[contains(@class,'gallery-grid-image-link') and @href]",
            WorkTitle = "//title",
            ChapterTitle = "//h1[contains(@class,'entry-title')] | //title",
            Contents = "//a[contains(concat(' ',normalize-space(@class),' '),' archive-item-link ') and @href]",
            SortContentsByChapterNumber = true,
            WorkFromChapter = "/${work}"
        }
    ]);

    private static PublicFictionSite FanficPlatform(string name, string host, string catalog) =>
        new(name, "https://" + host + catalog, host,
            @"/readfic/(?<work>[0-9]+)(?:-[^/]+)?/?",
            @"/readfic/(?<work>[0-9]+)(?:-[^/]+)?/(?<part>[0-9]+)/?",
            "//*[@id='content' and contains(concat(' ',normalize-space(@class),' '),' part_text ')]")
        {
            CatalogLinks = "//a[starts-with(@href,'/readfic/') or contains(@href,'/readfic/')]",
            ChapterTitle = "//article[@id='part_content']//h2 | //h1[contains(@class,'heading')]",
            Author = "//a[contains(@href,'/authors/') and normalize-space(.)]",
            Contents = "//a[contains(concat(' ',normalize-space(@class),' '),' part-link ') and starts-with(@href,'/readfic/')]",
            AllowStandalone = "//*[@id='content' and contains(concat(' ',normalize-space(@class),' '),' part_text ')]",
            WorkFromChapter = "/readfic/${work}"
        };

    private static PublicFictionSite DatedTranslationSite(string name, string host, string catalog, string overview,
        string chapterLinkFilter) => new(name, "https://" + host + catalog, host, overview,
            @"/(?<part>20[0-9]{2}/[0-9]{2}/[0-9]{2}/[a-z0-9-]+)/?", EntryContent)
        {
            CatalogLinks = "//div[contains(@class,'entry-content')]//a[@href]",
            WorkTitle = "//h1[contains(@class,'entry-title')] | //title",
            ChapterTitle = "//h1[contains(@class,'entry-title')] | //title",
            Contents = "//div[contains(@class,'entry-content')]//a[@href and " + chapterLinkFilter + "]",
            ContentsDefinesChapterMembership = true,
            ExcludeContent = ".//*[contains(@class,'sharedaddy') or contains(@class,'jp-relatedposts')]"
        };
}
