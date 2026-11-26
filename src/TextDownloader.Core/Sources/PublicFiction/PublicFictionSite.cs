using System.Text.RegularExpressions;

namespace TextDownloader.Core;

internal sealed class PublicFictionSite(
    string name, string catalog, string readingHost, string work, string chapter, string content)
{
    private readonly Regex _work = Route(work);
    private readonly Regex _chapter = Route(chapter);

    public string Name { get; } = name;
    public Uri Catalog { get; } = new(catalog);
    public string Host { get; } = readingHost;
    public string Content { get; } = content;
    public string CatalogLinks { get; init; } = "//a[@href]";
    public string WorkTitle { get; init; } = "//h1[1]";
    public string ChapterTitle { get; init; } = "//h1[1]";
    public string? Author { get; init; }
    public string? Contents { get; init; }
    public string? FirstChapter { get; init; }
    public string? NextChapter { get; init; }
    public string? ChapterOptions { get; init; }
    public string? ChapterFromOption { get; init; }
    public string? WorkFromChapter { get; init; }
    public bool QueryIsRoute { get; init; }
    public bool CanonicalMayIdentifyWork { get; init; }
    public bool ReverseContents { get; init; }
    public string? AllowStandalone { get; init; }
    public string? ExcludeContent { get; init; }

    public Match Work(Uri url) => _work.Match(Address(url));
    public Match Chapter(Uri url) => _chapter.Match(Address(url));

    public bool Owns(Uri url) => IsWeb(url) && url.IdnHost.Equals(Host, StringComparison.OrdinalIgnoreCase);

    public Uri Normalize(Uri url)
    {
        var builder = new UriBuilder(url) { Scheme = Uri.UriSchemeHttps, Port = -1, Fragment = "" };
        if (!QueryIsRoute) builder.Query = "";
        return builder.Uri;
    }

    public static bool IsWeb(Uri url) => url.IsAbsoluteUri && url.Scheme is "http" or "https" &&
        url.IsDefaultPort && url.UserInfo.Length == 0;

    public static string Key(Match route) => route.Groups["work"].Value;

    public static string Part(Match route) => route.Groups["part"].Value;

    private string Address(Uri url) => url.AbsolutePath + (QueryIsRoute ? url.Query : "");

    private static Regex Route(string pattern) =>
        new($@"\A(?:{pattern})\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
}
