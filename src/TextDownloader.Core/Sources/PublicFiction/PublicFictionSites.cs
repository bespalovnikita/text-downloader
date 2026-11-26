namespace TextDownloader.Core;

internal static class PublicFictionSites
{
    private const string SyosetuText = "//div[@class='js-novel-text p-novel__text']";
    private const string FanficContents = "//div[@class='FicContentsChapterName']/a[@href]";
    private const string FanficText = "//div[contains(concat(' ',normalize-space(@class),' '),' ReadContent ')]" +
        "//div[contains(concat(' ',normalize-space(@class),' '),' chapter ')]";

    internal static IReadOnlyList<PublicFictionSite> All { get; } =
    [
        FanFictionArchive("FanFiction.Net", "www.fanfiction.net", "/book/Harry-Potter/"),
        FanFictionArchive("FictionPress", "www.fictionpress.com", "/fiction/Fantasy/"),
        new("Fanfics.me", "https://fanfics.me/find", "fanfics.me", @"/fic(?<work>[0-9]+)",
            @"/read\.php\?id=(?<work>[0-9]+)(?:&chapter=(?<part>[0-9]+))?", FanficText)
        {
            QueryIsRoute = true,
            Contents = FanficContents,
            FirstChapter = "//*[@id='FicReadLink']/a[@href]",
            ChapterTitle = "//div[contains(@class,'ReadContent')]//h2[normalize-space()][1] | //div[contains(@class,'ReadContent')]/h1[1]",
            WorkFromChapter = "/fic${work}"
        },
        new("Ranobe.me", "https://ranobe.me/catalog?sort=recommend", "ranobe.me", @"/ranobe(?<work>[0-9]+)",
            @"/ranobe(?<work>[0-9]+)/(?<part>[0-9]+)", FanficText)
        {
            Contents = FanficContents,
            FirstChapter = "//*[@id='FicReadLink']/a[@href]",
            WorkFromChapter = "/ranobe${work}"
        },
        new("Scribble Hub", "https://www.scribblehub.com/series-ranking/?sort=2", "www.scribblehub.com",
            @"/series/(?<work>[0-9]+)/[^/]+/?", @"/read/(?<work>[0-9]+)-(?<slug>[^/]+)/chapter/(?<part>[0-9]+)/?",
            "//*[@id='chp_raw']")
        {
            WorkTitle = "//div[@class='fic_title']",
            ChapterTitle = "//div[@class='chapter-title']",
            Author = "//span[@class='auth_name_fic']/a",
            FirstChapter = "//div[@class='read_buttons']/a[contains(@href,'/chapter/')]",
            NextChapter = "//a[contains(concat(' ',normalize-space(@class),' '),' btn-next ') and @href]",
            WorkFromChapter = "/series/${work}/${slug}/"
        },
        new("Shōsetsuka ni Narō", "https://yomou.syosetu.com/rank/list/type/total_total/", "ncode.syosetu.com",
            @"/(?<work>n[0-9]+[a-z]+)/?", @"/(?<work>n[0-9]+[a-z]+)/(?<part>[1-9][0-9]*)/?", SyosetuText)
        {
            WorkTitle = "//h1[@class='p-novel__title']",
            ChapterTitle = "//h1[@class='p-novel__title']",
            Author = "//div[@class='p-novel__author']/a",
            FirstChapter = "(//a[@class='p-eplist__subtitle' and @href])[1]",
            NextChapter = "//a[contains(concat(' ',normalize-space(@class),' '),' c-pager__item--next ') and @href]",
            AllowStandalone = SyosetuText,
            WorkFromChapter = "/${work}/"
        },
        new("Kakuyomu", "https://kakuyomu.jp/rankings/all/daily?work_variation=long", "kakuyomu.jp",
            @"/works/(?<work>[0-9]+)/?", @"/works/(?<work>[0-9]+)/episodes/(?<part>[0-9]+)/?",
            "//div[contains(concat(' ',normalize-space(@class),' '),' widget-episodeBody ')]")
        {
            ChapterTitle = "//h2[contains(@class,'js-vertical-composition-item')]",
            FirstChapter = "(//a[contains(@class,'WorkTocSection_link') and @href])[1]",
            NextChapter = "//a[@id='contentMain-readNextEpisode' and @href]",
            WorkFromChapter = "/works/${work}"
        },
        new("Fimfiction", "https://www.fimfiction.net/stories", "www.fimfiction.net",
            @"/story/(?<work>[0-9]+)/[^/]+/?", @"/story/(?<work>[0-9]+)/(?<part>[0-9]+)/(?<slug>[^/]+)/[^/]+/?",
            "//*[@id='chapter-body']/div[contains(concat(' ',normalize-space(@class),' '),' bbcode ')]")
        {
            WorkTitle = "//meta[@property='og:title']",
            ChapterTitle = "//h1[@class='chapter-title']",
            Contents = "//a[@class='chapter-title' and @href]",
            WorkFromChapter = "/story/${work}/${slug}"
        },
        new("ReadNovel", "https://readnovel.eu/browse", "readnovel.eu",
            @"/novel/(?<work>[a-z0-9-]+)/?", @"/chapter/(?<work>[a-z0-9-]+)-(?<part>[0-9]+(?:\.[0-9]+)?)/?",
            "//*[@id='chapter-content']")
        {
            FirstChapter = "(//section[@id='chapters']//ol//a[@href])[1]",
            NextChapter = "//nav//a[normalize-space(.)='Next' and @href]",
            WorkFromChapter = "/novel/${work}"
        },
        new("ReadNovelFull", "https://readnovelfull.com/novel-list/hot-novel", "readnovelfull.com",
            @"/(?<work>[a-z0-9-]+?)(?:-v[0-9]+|-novel)?\.html", @"/(?<work>[a-z0-9-]+)/(?<part>[a-z0-9-]+)\.html",
            "//*[@id='chr-content']")
        {
            ChapterTitle = "//h2[1]",
            Author = "//a[@itemprop='author']",
            FirstChapter = "//a[contains(concat(' ',normalize-space(@class),' '),' btn-read-now ') and @href]",
            NextChapter = "//a[@id='next_chap' and @href and not(contains(@class,'disabled'))]",
            ExcludeContent = ".//h1 | .//h2 | .//h3",
            WorkFromChapter = "/${work}.html"
        },
    ];

    private static PublicFictionSite FanFictionArchive(string name, string host, string catalogPath) =>
        new(name, "https://" + host + catalogPath, host, @"/s/(?<work>[0-9]+)/1(?:/[^/]+)?/?",
            @"/s/(?<work>[0-9]+)/(?<part>[1-9][0-9]*)(?<slug>/[^/]+)?/?", "//*[@id='storytext']")
        {
            CatalogLinks = "//a[@class='stitle' and @href]",
            WorkTitle = "//*[@id='profile_top']/b[1]",
            ChapterTitle = "(//select[@id='chap_select'])[1]/option[@selected] | " +
                "//*[@id='profile_top']/b[1][not(//select[@id='chap_select'])]",
            Author = "//*[@id='profile_top']/a[starts-with(@href,'/u/')][1]",
            ChapterOptions = "(//select[@id='chap_select'])[1]/option",
            ChapterFromOption = "/s/${work}/{number}${slug}",
            WorkFromChapter = "/s/${work}/1${slug}"
        };
}
