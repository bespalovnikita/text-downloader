namespace TextDownloader.Core;

internal static partial class PublicFictionSites
{
    static partial void AddBatch09(Dictionary<int, PublicFictionSite[]> batches) => batches.Add(9,
    [
        new("SF轻小说", "https://book.sfacg.com/", "book.sfacg.com",
            @"/Novel/(?<work>[0-9]+)(?:/MainIndex)?/?", @"/Novel/(?<work>[0-9]+)/(?<part>[0-9]+/[0-9]+)/?",
            "//*[@id='ChapterBody' or @id='articleContent']")
        {
            WorkTitle = "//h1[@class='story-title'] | //h1",
            ContentsPath = "/Novel/${work}/MainIndex/",
            Contents = "//a[contains(@href,'/Novel/') and @href and starts-with(normalize-space(.),'第')]",
            WorkFromChapter = "/Novel/${work}/"
        },
        new("17K小说网", "https://www.17k.com/", "www.17k.com",
            @"/(?:book|list)/(?<work>[0-9]+)\.html", @"/chapter/(?<work>[0-9]+)/(?<part>[0-9]+)\.html",
            "//*[@id='readArea']//div[contains(concat(' ',normalize-space(@class),' '),' p ')] | //div[@class='readAreaBox']//*[@id='content']")
        {
            ContentsPath = "/list/${work}.html",
            Contents = "//dl//a[contains(@href,'/chapter/') and @href]",
            WorkFromChapter = "/book/${work}.html"
        },
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
        new("Ranahnovel", "https://ranahnovel.com/", "ranahnovel.com",
            @"/novel/(?<work>[a-z0-9-]+)/?", @"/novel/(?<work>[a-z0-9-]+)/(?<part>chapter-[a-z0-9.-]+)/?",
            "//*[@id='chapter-content'] | //div[contains(concat(' ',normalize-space(@class),' '),' chapter-body ')]")
        {
            FirstChapter = "//a[contains(@class,'btn-primary') and starts-with(normalize-space(.),'Baca Chapter') and @href]",
            NextChapter = "//a[(@rel='next' or contains(normalize-space(.),'Selanjutnya')) and @href and @href!='#']",
            WorkFromChapter = "/novel/${work}"
        },
        new("Novelzhen", "https://novelzhen.com/", "novelzhen.com",
            @"/novel/(?<work>[a-z0-9-]+)/?", @"/novel/(?<work>[a-z0-9-]+)/(?<part>[a-z0-9-]+-chapter-[a-z0-9.-]+)/?",
            "//*[@id='chapter-content'] | //div[contains(concat(' ',normalize-space(@class),' '),' reader-content ')]")
        {
            FirstChapter = "(//a[contains(@href,'-chapter-1') and (normalize-space(.)='Start Reading' or " +
                "substring(@href,string-length(@href)-9)='-chapter-1') and @href])[1]",
            NextChapter = "//a[(@rel='next' or normalize-space(.)='Next Chapter') and @href and @href!='#']",
            WorkFromChapter = "/novel/${work}"
        },
        new("Novels.pl", "https://www.novels.pl/", "www.novels.pl",
            @"/novel/(?<work>[A-Za-z0-9-]+)/?", @"/novel/(?<work>[A-Za-z0-9-]+)/(?<part>[0-9]+)/[^/]+\.html",
            "//*[@id='chapter-content']")
        {
            WorkTitle = "//meta[@property='og:title'] | //title",
            ChapterTitle = "//meta[@property='og:title'] | //title",
            ContentsPath = "/novel/${work}?p=1",
            FirstChapter = "(//table[contains(@class,'listing')]//a[contains(@href,'.html') and contains(@href,'/novel/')])[1]",
            NextChapter = "//a[normalize-space(.)='Next' and @href and @href!='#']",
            WorkFromChapter = "/novel/${work}"
        },
        new("Đọc Truyện", "https://dtruyen.com/", "dtruyen.com",
            @"/(?<work>[a-z0-9-]+)/?", @"/(?<work>[a-z0-9-]+)/(?<part>[a-z0-9-]+_[0-9]+)\.html",
            "//*[@id='chapter-content']")
        {
            CatalogLinks = "//h3/a[@href]",
            FirstChapter = "(//a[contains(@href,'chuong-1-') and @href])[1]",
            NextChapter = "//a[contains(concat(' ',normalize-space(@class),' '),' next ') and @href and @href!='#']",
            WorkFromChapter = "/${work}/"
        },
        new("Truyện Full", "https://truyenfull.vision/", "truyenfull.vision",
            @"/(?<work>[a-z0-9-]+)/?", @"/(?<work>[a-z0-9-]+)/(?<part>chuong-[a-z0-9.-]+)/?",
            "//*[@id='chapter-c']")
        {
            CatalogLinks = "//h3[contains(@class,'truyen-title')]/a[@href]",
            WorkTitle = "//h3[contains(@class,'title')] | //h1",
            ChapterTitle = "//a[contains(@class,'chapter-title')]",
            FirstChapter = "(//*[@id='list-chapter']//a[@href])[1]",
            NextChapter = "//a[@id='next_chap' and @href and @href!='#' and not(contains(@class,'disabled'))]",
            WorkFromChapter = "/${work}/"
        },
        new("Inkitt", "https://www.inkitt.com/fanfiction", "www.inkitt.com",
            @"/stories/(?<work>[a-z0-9-]+/[0-9]+)/?", @"/stories/(?<work>[a-z0-9-]+/[0-9]+)/chapters/(?<part>[0-9]+)/?",
            "//*[@id='chapter-content'] | //div[contains(concat(' ',normalize-space(@class),' '),' chapter-content ')]")
        {
            Contents = "//a[contains(@href,'/chapters/') and @href]",
            WorkFromChapter = "/stories/${work}"
        },
    ]);
}
