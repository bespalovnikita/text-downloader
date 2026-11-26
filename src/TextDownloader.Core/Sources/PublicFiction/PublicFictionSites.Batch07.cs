namespace TextDownloader.Core;

internal static partial class PublicFictionSites
{
    static partial void AddBatch07(Dictionary<int, PublicFictionSite[]> batches) => batches.Add(7,
    [
        new("Sleepy Translations", "https://sleepytranslations.com/series/", "sleepytranslations.com",
            @"/series/(?<work>[a-z0-9-]+)/?", @"/series/(?<work>[a-z0-9-]+)/(?<part>[a-z0-9.-]+)/?", MadaraText)
        {
            FirstChapter = "//a[@id='btn-read-last' and @href and @href!='#']",
            NextChapter = "//a[contains(concat(' ',normalize-space(@class),' '),' next_page ') and @href and @href!='#']",
            WorkFromChapter = "/series/${work}/"
        },
        new("Ckanda Writes", "https://ckandawrites.online/az-lists/", "ckandawrites.online",
            @"/(?![a-z0-9-]+-(?:chapter|extra)-[a-z0-9.-]+/?$)(?<work>[a-z0-9-]+)/?",
            @"/(?<work>[a-z0-9-]+?)-(?<part>(?:chapter|extra)-[a-z0-9.-]+)/?",
            "//div[contains(concat(' ',normalize-space(@class),' '),' epcontent ')]")
        {
            FirstChapter = "//div[contains(@class,'inepcx')]/a[@href and starts-with(normalize-space(.),'First Chapter')]",
            NextChapter = NextByRel,
            WorkFromChapter = "/${work}/"
        },
        new("Tiger Translations", "https://tigertranslations.org/", "tigertranslations.org",
            @"/(?![a-z0-9-]+-[0-9]+(?:-[a-z0-9-]+)?/?$)(?<work>[a-z0-9-]+)/?",
            @"/(?:[0-9]{4}/[0-9]{2}/[0-9]{2}/)?(?<work>[a-z0-9-]+?)-(?<part>[0-9]+(?:-[a-z0-9-]+)?)/?",
            EntryContent)
        {
            CatalogLinks = "//nav//a[@href]",
            WorkTitle = "//h1[@class='entry-title']",
            ChapterTitle = "//h1[@class='entry-title']",
            FirstChapter = "(//div[contains(@class,'entry-content')]//a[starts-with(normalize-space(.),'Chapter 1') and " +
                "(normalize-space(.)='Chapter 1' or starts-with(normalize-space(.),'Chapter 1:') or starts-with(normalize-space(.),'Chapter 1 '))])[1]",
            NextChapter = "//div[contains(@class,'entry-content')]//a[normalize-space(.)='Next Chapter' and @href]",
            WorkFromChapter = "/${work}/"
        },
        new("BabelNovel", "https://babelnovel.com/", "babelnovel.com",
            @"/books/(?<work>[a-z0-9-]+)/?", @"/books/(?<work>[a-z0-9-]+)/chapters/(?<part>c[0-9]+(?:-[0-9]+)?)/?",
            "//article[starts-with(@class,'chapter_container')]/section")
        {
            ChapterTitle = "//title",
            FirstChapter = "(//a[contains(@class,'chapter-item_container') and " +
                "substring(@href,string-length(@href)-2)='/c1'])[1]",
            NextChapter = "//a[@rel='next' and @href and @href!='#']",
            WorkFromChapter = "/books/${work}"
        },
        new("Gravity Tales", "https://gravitytales.com/", "gravitytales.com",
            @"/story/(?<work>[a-z0-9-]+)/?", @"/story/(?<work>[a-z0-9-]+)/(?<part>[a-z0-9.-]+)/?",
            "//section[contains(concat(' ',normalize-space(@class),' '),' chapter__content ')]")
        {
            Contents = "//ol[contains(@class,'chapter-group__list')]//a[contains(@class,'chapter-group__list-item-link') and @href]",
            WorkFromChapter = "/story/${work}/"
        },
        new("FAQWiki", "https://faqwiki.xyz/", "faqwiki.xyz",
            @"/(?![a-z0-9-]+-chapter-[a-z0-9.-]+/?$)(?<work>[a-z0-9-]+)/?",
            @"/(?<work>[a-z0-9-]+?)-(?<part>chapter-[a-z0-9.-]+)/?", EntryContent)
        {
            CatalogLinks = "//div[contains(@class,'entry-content')]//a[@href]",
            WorkTitle = "//h1[@class='entry-title']",
            ChapterTitle = "//h1[@class='entry-title']",
            FirstChapter = "(//div[contains(@class,'entry-content')]//a[contains(@href,'-chapter-1/') and @href])[1]",
            NextChapter = "//a[contains(concat(' ',normalize-space(@class),' '),' next-chapter ') and @href and @href!='#']",
            ExcludeContent = ".//div[contains(@class,'code-block')]",
            WorkFromChapter = "/${work}/"
        },
        new("Star Trek Fan Fiction", "https://trekfanfiction.net/", "trekfanfiction.net",
            @"/(?<work>(?!(?:category|tag|dtremel|wp-admin|wp-content)/)[a-z0-9-]+/[a-z0-9-]+)/?", "(?!)",
            "//article//div[contains(concat(' ',normalize-space(@class),' '),' entry-content ')]")
        {
            CatalogLinks = "//h2/a[@href] | //h3/a[@href]",
            WorkTitle = "//h1[contains(@class,'entry-title')]",
            ChapterTitle = "//h1[contains(@class,'entry-title')]",
            AllowStandalone = "//article//div[contains(concat(' ',normalize-space(@class),' '),' entry-content ')]"
        },
        new("RanobeHub", "https://ranobehub.org/", "ranobehub.org",
            @"/ranobe/(?<work>[a-z0-9-]+)/?", @"/ranobe/(?<work>[a-z0-9-]+)/chapter/(?<part>[0-9]+)/?",
            "//div[contains(concat(' ',normalize-space(@class),' '),' reader-content ')]")
        {
            Contents = "//div[contains(@class,'book-chapter-viewport') and contains(@class,'is-complete')]" +
                "//a[starts-with(@id,'chapter-list-') and @href]",
            ReverseContents = true,
            WorkFromChapter = "/ranobe/${work}"
        },
        new("Novels.pl", "https://www.novels.pl/", "www.novels.pl",
            @"/novel/(?<work>[A-Za-z0-9-]+)/?", @"/novel/(?<work>[A-Za-z0-9-]+)/(?<part>[0-9]+)/[^/]+\.html",
            "//*[@id='chapter-content']")
        {
            WorkTitle = "//meta[@property='og:title']",
            ChapterTitle = "//meta[@property='og:title']",
            FirstChapter = "(//table[contains(@class,'listing')]//a[contains(@href,'/1/') and @href])[1]",
            NextChapter = "//a[normalize-space(.)='Next' and @href and @href!='#']",
            WorkFromChapter = "/novel/${work}"
        },
        new("SciFi-Stories", "https://scifistories.com/library/new_stories.php", "scifistories.com",
            @"/s/(?<work>[0-9]+)(?:/[^/]+)?/?", @"/s/(?<work>[0-9]+):(?<part>[0-9]+)(?:/[^/]+)?/?",
            "//*[@id='sr'] | //article[contains(@class,'story')]")
        {
            WorkTitle = "//title",
            ChapterTitle = "//title",
            Contents = "//a[contains(@class,'chapter') and starts-with(@href,'/s/') and contains(@href,':')]",
            AllowStandalone = "//*[@id='sr'] | //article[contains(@class,'story')]",
            WorkFromChapter = "/s/${work}"
        },
    ]);
}
