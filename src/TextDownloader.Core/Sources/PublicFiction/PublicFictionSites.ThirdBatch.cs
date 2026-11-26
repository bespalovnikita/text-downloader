namespace TextDownloader.Core;

internal static partial class PublicFictionSites
{
    private static readonly PublicFictionSite[] ThirdBatch =
    [
        new("Hameln", "https://syosetu.org/?mode=rank", "syosetu.org",
            @"/novel/(?<work>[0-9]+)/?", @"/novel/(?<work>[0-9]+)/(?<part>[0-9]+)\.html",
            "//*[@id='honbun']")
        {
            CatalogLinks = "//a[@class='blo_title_link' and @href]",
            WorkTitle = "//title",
            ChapterTitle = "//title",
            Contents = "//a[@class='episode-list__link' and @href]",
            AllowStandalone = "//*[@id='honbun']",
            WorkFromChapter = "/novel/${work}/"
        },
        new("Snowy Codex", "https://snowycodex.com/", "snowycodex.com",
            @"/novels/(?<work>[a-z0-9-]+)/?", @"/novels/(?<work>[a-z0-9-]+)/(?<part>[a-z0-9.-]+)/?",
            "//div[contains(concat(' ',normalize-space(@class),' '),' entry-content ')]")
        {
            WorkTitle = "//meta[@property='og:title']",
            ChapterTitle = "//meta[@property='og:title']",
            FirstChapter = "(//div[contains(@class,'entry-content')]//a[contains(@href,'/chapter-1/') and @href])[1]",
            NextChapter = "//div[contains(@class,'entry-content')]//a[normalize-space(.)='Next Chapter' and @href and @href!='#']",
            ExcludeContent = ".//a[normalize-space(.)='Next Chapter' or normalize-space(.)='Previous Chapter'] | .//h2",
            WorkFromChapter = "/novels/${work}/"
        },
        new("TeaNovel", "https://www.teanovel.com/", "www.teanovel.com",
            @"/novel/(?<work>[a-z0-9-]+)/?", @"/novel/(?<work>[a-z0-9-]+)/(?<part>[0-9]+(?:\.[0-9]+)?)/?",
            "//div[contains(concat(' ',normalize-space(@class),' '),' google-anno-skip ')]")
        {
            FirstChapter = "//a[contains(@class,'bg-lime-600') and contains(@href,'/novel/')]",
            NextChapter = "//a[normalize-space(.)='Next Chapter' and @href and @href!='#']",
            WorkFromChapter = "/novel/${work}"
        },
        new("Novel Mania", "https://novelmania.com.br/", "novelmania.com.br",
            @"/novels/(?<work>[a-z0-9-]+)/?", @"/novels/(?<work>[a-z0-9-]+)/capitulos/(?<part>[a-z0-9.-]+)/?",
            "//div[contains(concat(' ',normalize-space(@class),' '),' rich-text-content ')]")
        {
            FirstChapter = "//a[starts-with(@aria-label,'Começar a ler') and @href]",
            NextChapter = "//nav[@aria-label='Navegação entre capítulos']/a[contains(@class,'navBtnNext') and @href]",
            WorkFromChapter = "/novels/${work}"
        },
        new("Dobytranslations", "https://dobytranslations.com/", "dobytranslations.com",
            @"/series/(?<work>[a-z0-9-]+)/?", @"/(?<work>[a-z0-9-]+?)-(?<part>[0-9][a-z0-9.-]*)/?",
            "//div[contains(concat(' ',normalize-space(@class),' '),' epcontent ')]")
        {
            FirstChapter = "//li[@class='tseplsfrst']/a[@href]",
            NextChapter = NextByRel,
            WorkFromChapter = "/series/${work}/"
        },
        new("Asura Scans", "https://asurascans.com/", "asurascans.com",
            @"/novels/(?<work>[a-z0-9-]+)/?", @"/novels/(?<work>[a-z0-9-]+)/chapter/(?<part>[0-9]+(?:\.[0-9]+)?)/?",
            "//div[contains(concat(' ',normalize-space(@class),' '),' select-none ') and ./p]")
        {
            FirstChapter = "//a[contains(@href,'/chapter/') and normalize-space(.)='First Chapter']",
            NextChapter = "//a[normalize-space(.)='Next' and @href and @href!='#']",
            WorkFromChapter = "/novels/${work}"
        },
        new("NovelFrance", "https://novelfrance.fr/", "novelfrance.fr",
            @"/novel/(?<work>[a-z0-9-]+)/?", @"/novel/(?<work>[a-z0-9-]+)/(?<part>chapter-[a-z0-9.-]+)/?",
            "//div[contains(concat(' ',normalize-space(@class),' '),' chapter-content ')]")
        {
            FirstChapter = "//a[normalize-space(.)='Commencer la lecture' and @href]",
            NextChapter = "//nav[@aria-label='Chapitres voisins']/a[starts-with(normalize-space(.),'Suivant') and @href]",
            WorkFromChapter = "/novel/${work}"
        },
        new("MegaNovel", "https://www.meganovel.com/", "www.meganovel.com",
            @"/story/(?<work>[a-zA-Z0-9_-]+_[0-9]+)/?",
            @"/story/(?<work>[a-zA-Z0-9_-]+_[0-9]+)/(?<part>[a-zA-Z0-9_-]+_[0-9]+)/?",
            "//div[contains(concat(' ',normalize-space(@class),' '),' read-content ')]")
        {
            ChapterTitle = "//title",
            FirstChapter = "(//div[@class='catalog-box']/a[@href])[1]",
            NextChapter = "//a[normalize-space(.)='Next Chapter' and @href and @href!='#']",
            WorkFromChapter = "/story/${work}"
        },
        new("Light Novel Translations", "https://lightnovelstranslations.com/", "lightnovelstranslations.com",
            @"/novel/(?<work>[a-z0-9-]+)/?", @"/novel/(?<work>[a-z0-9-]+)/(?<part>[a-z0-9.-]+)/?",
            "//div[@class='text_story']")
        {
            WorkTitle = "//meta[@property='og:title']",
            ChapterTitle = "//meta[@property='og:title']",
            FirstChapter = "//a[@id='lnt-reading-button' and @href]",
            NextChapter = NextByRel,
            WorkFromChapter = "/novel/${work}/"
        },
        new("Revenger Novels", "https://revengernovel.com/", "revengernovel.com",
            @"/series/(?<work>[a-z0-9-]+)/?", @"/series/(?<work>[a-z0-9-]+)/(?<part>[0-9]+/[a-z0-9.-]+)/?",
            "//*[@id='chapterContent']")
        {
            Contents = "//a[@class='chapter-card-link' and @href]",
            WorkFromChapter = "/series/${work}"
        },
    ];
}
