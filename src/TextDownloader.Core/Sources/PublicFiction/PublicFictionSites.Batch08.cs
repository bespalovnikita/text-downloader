namespace TextDownloader.Core;

internal static partial class PublicFictionSites
{
    static partial void AddBatch08(Dictionary<int, PublicFictionSite[]> batches) => batches.Add(8,
    [
        EfictionArchive("Nine Lives", "www.ninelivesarchive.com", ""),
        EfictionArchive("Midnight Whispers", "midnightwhispers.net", ""),
        EfictionArchive("Ashwinder", "ashwinder.sycophanthex.com", ""),
        EfictionArchive("Chaos", "chaos.sycophanthex.com", ""),
        EfictionArchive("Occlumency", "occlumency.sycophanthex.com", ""),
        EfictionArchive("Lumos", "lumos.sycophanthex.com", ""),
        EfictionArchive("The Original Naruto Fanfiction Archive", "narutofic.org", ""),
        EfictionArchive("Twilighted", "www.twilighted.net", ""),
        EfictionArchive("Potions and Snitches", "www.potionsandsnitches.org", "/fanfiction"),
        EfictionArchive("FanficTalk Archive", "fanfictalk.com", "/archive"),
    ]);

    private static PublicFictionSite EfictionArchive(string name, string host, string directory)
    {
        string story = directory + "/viewstory.php";
        string route = directory + @"/viewstory\.php\?sid=(?<work>[0-9]+)";
        return new PublicFictionSite(name, "https://" + host + directory + "/browse.php?type=titles", host,
            route, route + @"(?:&textsize=[-0-9]+)?(?:&chapter=(?<part>[1-9][0-9]*))?(?:&textsize=[-0-9]+)?",
            "//*[@id='story']")
        {
            QueryIsRoute = true,
            WorkTitle = "//title",
            ChapterTitle = "//title",
            Contents = "//*[@id='chapters']//a[contains(@href,'&chapter=')] | " +
                "//select[@name='chapter']/option[contains(@value,'viewstory.php')]",
            NextChapter = "//*[@id='next']/a[@href and contains(@href,'&chapter=')] | " +
                "//a[@class='next' and contains(@href,'&chapter=')]",
            AllowStandalone = "//*[@id='story']",
            WorkFromChapter = story + "?sid=${work}"
        };
    }
}
