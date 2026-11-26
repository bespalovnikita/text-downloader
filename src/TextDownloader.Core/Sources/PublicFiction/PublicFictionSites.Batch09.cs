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
            "//div[contains(concat(' ',normalize-space(@class),' '),' chapter-content ')]")
        {
            FirstChapter = "(//a[contains(@href,'-chapter-1') and (normalize-space(.)='Start Reading' or " +
                "substring(@href,string-length(@href)-9)='-chapter-1') and @href])[1]",
            NextChapter = "//a[(@rel='next' or normalize-space(.)='Next') and @href and @href!='#']",
            WorkFromChapter = "/novel/${work}"
        },
        new("Novels.pl", "https://www.novels.pl/", "www.novels.pl",
            @"/novel/(?<work>[A-Za-z0-9-]+)/?(?:\?p=[0-9]+)?", @"/novel/(?<work>[A-Za-z0-9-]+)/(?<part>[0-9]+)/[^/]+\.html",
            "//*[@id='chapter-content']")
        {
            WorkTitle = "//meta[@property='og:title'] | //title",
            ChapterTitle = "//meta[@property='og:title'] | //title",
            QueryIsRoute = true,
            ContentsPath = "/novel/${work}?p=1",
            ContentsPageLink = "//a[normalize-space(.)='Last' and contains(@href,'?p=')]",
            FirstChapter = "(//table[contains(@class,'listing')]//a[contains(@href,'.html') and contains(@href,'/novel/')])[last()]",
            NextChapter = "//a[normalize-space(.)='Next' and @href and @href!='#']",
            WorkFromChapter = "/novel/${work}"
        },
        new("TruyenDich", "https://truyendich.vn/danh-sach/truyen-mien-phi/", "truyendich.vn",
            @"/(?<work>[a-z0-9-]+)/?", @"/(?<work>[a-z0-9-]+)/(?<part>chuong-[a-z0-9.-]+)/?",
            "//*[@id='chapter-c']")
        {
            CatalogLinks = "//h3//a[@href] | //a[.//h3]",
            FirstChapter = "(//a[substring(@href,string-length(@href)-8)='/chuong-1' or " +
                "substring(@href,string-length(@href)-9)='/chuong-1/'])[1]",
            NextChapter = "//a[@id='next_chap' and @href and @href!='#' and not(contains(@class,'disabled'))]",
            WorkFromChapter = "/${work}/"
        },
        new("Novel World Translations", "https://novelworldtranslations.com/az-lists/", "novelworldtranslations.com",
            @"/series/(?<work>[a-z0-9-]+)/?", @"/(?<work>[a-z0-9-]+?)-(?<part>[a-z0-9-]+)/?",
            "//div[contains(concat(' ',normalize-space(@class),' '),' epcontent ')]")
        {
            FirstChapter = "(//a[@href and starts-with(normalize-space(.),'First Chapter')])[1]",
            FirstChapterMayUseDifferentSlug = true,
            NextChapter = "//a[normalize-space(.)='Next' and @href and @href!='#']"
        },
        WorksArchive("SquidgeWorld Archive", "squidgeworld.org", "/works?work_search%5Brating_ids%5D=10"),
        WorksArchive("Ad Astra Star Trek Fanfiction Archive", "www.adastrafanfic.com", "/works"),
        new("Fanficauthors.net", "https://jeconais.fanficauthors.net/Harry_Potter/", "jeconais.fanficauthors.net",
            @"/(?<work>[A-Za-z0-9_-]+)/index/?",
            @"/(?<work>[A-Za-z0-9_-]+)/(?<part>(?!index(?:/|$))[A-Za-z0-9_-]+)/?",
            "//div[contains(concat(' ',normalize-space(@class),' '),' chapterDisplay ')]/div[1]")
        {
            WorkTitle = "//h2[contains(@class,'text-center')] | //title",
            ChapterTitle = "//h2[contains(@class,'text-center')] | //title",
            Contents = "//*[@id='main']//a[starts-with(@href,'/') and " +
                "string-length(@href)-string-length(translate(@href,'/',''))=3 and " +
                "substring(@href,string-length(@href))='/' and not(contains(@href,'/index/'))]",
            WorkFromChapter = "/${work}/index/"
        }
    ]);

    private static PublicFictionSite WorksArchive(string name, string host, string catalog) =>
        new(name, "https://" + host + catalog, host, @"/works/(?<work>[0-9]+)/?",
            @"/works/(?<work>[0-9]+)/chapters/(?<part>[0-9]+)/?",
            "//*[@id='chapters']//*[contains(concat(' ',normalize-space(@class),' '),' userstuff ') " +
            "and not(ancestor::*[contains(concat(' ',normalize-space(@class),' '),' notes ')])]")
        {
            CatalogLinks = "//h4[contains(@class,'heading')]/a[starts-with(@href,'/works/')]",
            WorkTitle = "//h2[contains(concat(' ',normalize-space(@class),' '),' title ')]",
            ChapterTitle = "//*[@id='chapters']//h3[contains(@class,'title')] | //h2[contains(@class,'title')]",
            Author = "//a[@rel='author']",
            AllowStandalone = "//*[@id='chapters']",
            WorkMayRedirectToFirstChapter = true,
            NextChapter = "//li[contains(concat(' ',normalize-space(@class),' '),' next ')]/a[contains(@href,'/chapters/')]",
            WorkFromChapter = "/works/${work}"
        };
}
