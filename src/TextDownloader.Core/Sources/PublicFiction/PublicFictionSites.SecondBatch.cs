namespace TextDownloader.Core;

internal static partial class PublicFictionSites
{
    private const string MadaraText = "//div[contains(concat(' ',normalize-space(@class),' '),' reading-content ')]" +
        "//div[contains(concat(' ',normalize-space(@class),' '),' text-left ')]";
    private const string MadaraChapters =
        "//li[contains(concat(' ',normalize-space(@class),' '),' wp-manga-chapter ')]/a[@href]";
    private const string NextByRel = "//a[@rel='next' and @href and @href!='#']";

    private static readonly PublicFictionSite[] SecondBatch =
    [
        new("Hiraeth Translation", "https://hiraethtranslation.com/", "hiraethtranslation.com",
            @"/novel/(?<work>[a-z0-9-]+)/?", @"/novel/(?<work>[a-z0-9-]+)/(?<part>[a-z0-9-]+)/?", MadaraText)
        {
            Contents = MadaraChapters,
            ReverseContents = true,
            WorkFromChapter = "/novel/${work}/",
            CanonicalMayIdentifyWork = true
        },
        new("Fantasy World Online", "https://f-w-o.com/", "f-w-o.com",
            @"/novel/(?<work>[a-z0-9-]+)/?", @"/novel/(?<work>[a-z0-9-]+)/(?<part>(?:[a-z0-9-]+/)?[a-z0-9-]+)/?", MadaraText)
        {
            Contents = MadaraChapters,
            ReverseContents = true,
            WorkFromChapter = "/novel/${work}/"
        },
        new("NovelFull", "https://novelfull.com/", "novelfull.com",
            @"/(?<work>[a-z0-9-]+)\.html", @"/(?<work>[a-z0-9-]+)/(?<part>[a-z0-9-]+)\.html",
            "//*[@id='chapter-content']")
        {
            CatalogLinks = "//h3/a[@href]",
            WorkTitle = "//h3[@class='title'][1]",
            ChapterTitle = "//a[contains(@class,'chapter-title')][1] | //h2[1]",
            FirstChapter = "(//*[@id='list-chapter']//ul[contains(@class,'list-chapter')]/li/a[@href])[1]",
            NextChapter = "//a[@id='next_chap' and @href and @href!='#' and not(contains(@class,'disabled'))]",
            WorkFromChapter = "/${work}.html"
        },
        new("FreeWebNovel", "https://freewebnovel.com/home", "freewebnovel.com",
            @"/novel/(?<work>[a-z0-9-]+)/?", @"/novel/(?<work>[a-z0-9-]+)/(?<part>chapter-[a-z0-9.-]+)/?",
            "//*[@id='article']")
        {
            ChapterTitle = "//h1[1]",
            FirstChapter = "(//ul[@id='idData']/li/a[@href])[1]",
            NextChapter = "//a[@id='next_url' and @href and @href!='#']",
            WorkFromChapter = "/novel/${work}"
        },
        new("WoopRead", "https://woopread.com/", "woopread.com",
            @"/series/(?<work>[a-z0-9-]+)/?", @"/series/(?<work>[a-z0-9-]+)/(?<part>[a-z0-9.-]+)/?",
            "//div[starts-with(@class,'space-y-4 ') and contains(@class,'footnote-ref')]")
        {
            ChapterTitle = "//h2[contains(@class,'notranslate')]",
            FirstChapter = "//a[@aria-label='Read Now' and @href]",
            NextChapter = "//a[normalize-space(.)='Next' and @href and @href!='#']",
            WorkFromChapter = "/series/${work}"
        },
        new("爱下电子书", "https://ixdzs8.com/", "ixdzs8.com",
            @"/read/(?<work>[0-9]+)/?", @"/read/(?<work>[0-9]+)/p(?<part>[0-9]+)\.html",
            "//article[contains(@class,'page-content')]")
        {
            ChapterTitle = "//article[contains(@class,'page-content')]/h2 | //h2[1]",
            FirstChapter = "//a[normalize-space(.)='立即阅读' and @href]",
            NextChapter = "//a[contains(concat(' ',normalize-space(@class),' '),' read-prog-next ') and @href and @href!='#']",
            Author = "//a[@class='bauthor']",
            ExcludeContent = ".//h2",
            WorkFromChapter = "/read/${work}/"
        },
        new("NovelRare", "https://novelrare.com/", "novelrare.com",
            @"/novel/(?<work>[a-z0-9-]+)/?", @"/novel/(?<work>[a-z0-9-]+)/(?<part>[a-z0-9.-]+)/?",
            "//div[@class='ne-reader-chapter-text']")
        {
            FirstChapter = "//a[normalize-space(.)='Start Reading' and @href]",
            NextChapter = "//div[@class='ne-reader-quick-nav']/a[starts-with(normalize-space(.),'Next') and @href and @href!='#']",
            WorkFromChapter = "/novel/${work}/"
        },
        new("Divine Dao Library", "https://www.divinedaolibrary.com/", "www.divinedaolibrary.com",
            @"/story/(?<work>[a-z0-9-]+)/?", @"/story/(?<work>[a-z0-9-]+)/(?<part>[a-z0-9.-]+)/?",
            "//section[contains(concat(' ',normalize-space(@class),' '),' chapter__content ')]")
        {
            FirstChapter = "(//ol[contains(concat(' ',normalize-space(@class),' '),' chapter-group__list ')]//a[@href])[1]",
            NextChapter = "//a[contains(concat(' ',normalize-space(@class),' '),' _next ') and @href and @href!='#']",
            Author = "//div[contains(@class,'story__identity-meta')]/a[@class='author']",
            WorkFromChapter = "/story/${work}/"
        },
        new("NoBadNovel", "https://www.nobadnovel.com/", "www.nobadnovel.com",
            @"/series/(?<work>[a-z0-9-]+)/?", @"/series/(?<work>[a-z0-9-]+)/(?<part>chapter-[a-z0-9.-]+)/?",
            "//div[starts-with(@class,'text-base sm:text-lg sm:leading-8 ')]")
        {
            FirstChapter = "//a[normalize-space(.)='Start Reading' and @href]",
            NextChapter = "//a[normalize-space(.)='Next Chapter' and @href and @href!='#']",
            WorkFromChapter = "/series/${work}"
        },
        new("Lazy Girl Translations", "https://lazygirltranslations.com/", "lazygirltranslations.com",
            @"/series/(?<work>[a-z0-9-]+)/?", @"/(?<work>[a-z0-9-]+?)-(?<part>chapter-[a-z0-9.-]+)/?",
            "//div[contains(concat(' ',normalize-space(@class),' '),' epcontent ')]")
        {
            FirstChapter = "//div[contains(@class,'inepcx')]/a[@href and starts-with(normalize-space(.),'First Chapter')]",
            NextChapter = NextByRel,
            WorkFromChapter = "/series/${work}/"
        },
    ];
}
