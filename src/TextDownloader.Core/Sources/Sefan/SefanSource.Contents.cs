using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace TextDownloader.Core;

public sealed record SefanContentsEntry(string Label, Uri Url);
public sealed record SefanContents(Uri? IndexUrl, IReadOnlyList<SefanContentsEntry> Entries);

public sealed partial class SefanSource
{
    private static readonly Regex ContentsPath = new(
        @"^(?<directory>/stories/[a-z]{2}/\d+/[^/]+/)vse-chasti/(?<slug>[^/]+)\.html$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static SefanContents ReadPageContents(HtmlDocument document, Uri page, HashSet<HtmlNode> excluded)
    {
        var links = document.DocumentNode.Descendants("a")
            .Select(node => (Node: node, Url: ReadContentsLink(node, page)))
            .Where(link => link.Url is not null)
            .ToArray();
        foreach (var link in links) excluded.Add(link.Node);
        var contentsLinks = links.Where(link => !CleanText(Render(link.Node, false))
            .Equals("Оглавления нет", StringComparison.OrdinalIgnoreCase)).ToArray();
        var indexes = contentsLinks.Select(link => link.Url).Distinct().ToArray();
        if (indexes.Length > 1) throw new InvalidDataException("На странице Sefan найдено несколько разных ссылок на оглавление.");
        Uri? index = indexes.SingleOrDefault();
        if (index is null) return new SefanContents(null, []);

        foreach (var link in contentsLinks)
        {
            var form = link.Node.Ancestors("form").FirstOrDefault();
            if (form is null) continue;
            excluded.Add(form);
            var entries = ReadPartSelector(form, page);
            if (entries.Count > 0) return new SefanContents(index, entries);
        }
        return new SefanContents(index, []);
    }

    private static Uri? ReadContentsLink(HtmlNode anchor, Uri page)
    {
        if (!Uri.TryCreate(page, Attribute(anchor, "href"), out var candidate)) return null;
        try
        {
            string part = PathPattern.Match(page.AbsolutePath).Groups["part"].Value;
            var (index, chapter) = NormalizeContentsUrl(candidate, part);
            return IsSameReadingStory(chapter, page) ? index : null;
        }
        catch (ArgumentException) { return null; }
    }

    private static IReadOnlyList<SefanContentsEntry> ReadPartSelector(HtmlNode form, Uri page)
    {
        var selectors = form.Descendants("select").Where(node => Attribute(node, "name") == "s").ToArray();
        if (selectors.Length != 1 || !Uri.TryCreate(page, Attribute(form, "action"), out var directory) ||
            !directory.AbsolutePath.EndsWith('/') || directory.Query.Length != 0 || directory.Fragment.Length != 0 ||
            directory.Host is not ("sefan.ru" or "www.sefan.ru") || directory.UserInfo.Length != 0)
            return [];
        var current = PathPattern.Match(page.AbsolutePath);
        string currentSlug = Uri.UnescapeDataString(current.Groups["slug"].Value);
        var entries = new List<SefanContentsEntry>();
        foreach (var option in selectors[0].Descendants("option"))
        {
            string value = Uri.UnescapeDataString(Attribute(option, "value"));
            if (value.Length == 0 || value.Any(c => char.IsControl(c) || c is '/' or '\\' or '?' or '#')) return [];
            bool isCurrent = value == currentSlug;
            string suffix = isCurrent ? current.Groups["part"].Value : "1";
            var candidate = new Uri(directory, Uri.EscapeDataString(value) + $"_{suffix}.html");
            try { candidate = Normalize(candidate); }
            catch (ArgumentException) { return []; }
            if (isCurrent && !IsSameReadingStory(candidate, page)) return [];
            entries.Add(new SefanContentsEntry(CleanText(Render(option, false)), candidate));
        }
        return entries.Any(entry => IsSameReadingStory(entry.Url, page)) ? entries : [];
    }

    internal async Task<IReadOnlyList<SefanContentsEntry>> GetContentsAsync(Uri index, StoryPage selected,
        bool confirmAdult, CancellationToken token)
    {
        var original = ValidateIndexReplacement(index, index);
        var (document, actual) = await GetDocumentAsync(original, confirmAdult, token,
            candidate => ValidateIndexReplacement(original, candidate)).ConfigureAwait(false);

        // Sefan's own contents page can lay out numbered chapters as
        // sibling divs under #top. Breadcrumb and story excerpt links have no number marker.
        var numberedLinks = document.GetElementbyId("top")?.Elements("div")
            .SelectMany(row => row.Elements("a"))
            .Where(anchor => anchor.Elements("span").Any(label => Tokens(label, "class").Contains("lhup") &&
                label.Elements("span").Any(number => Tokens(number, "class").Contains("o") &&
                    Regex.IsMatch(CleanText(Render(number, false)), @"^[1-9][0-9]*\.$"))))
            .ToArray() ?? [];
        if (numberedLinks.Length > 0)
        {
            var entries = numberedLinks.Select(link => ReadIndexLink(link, actual, selected)
                ?? throw new InvalidDataException("В оглавлении Sefan найден недопустимый адрес части.")).ToArray();
            if (entries.Any(entry => IsSameReadingStory(entry.Url, selected.Url))) return entries;
            throw new InvalidDataException("В оглавлении Sefan не найдена выбранная часть рассказа.");
        }

        // Choose the list, table or content block that contains the selected
        // chapter. Links elsewhere can point to recommendations and site navigation.
        var groups = document.DocumentNode.Descendants()
            .Where(n => n.Name is "ol" or "ul" or "table" or "main" || Attribute(n, "id") == "rsz")
            .ToArray();
        foreach (var group in groups.Reverse())
        {
            var entries = ReadIndexEntries(group, actual, selected);
            if (entries.Any(entry => IsSameReadingStory(entry.Url, selected.Url))) return entries;
        }
        throw new InvalidDataException("В оглавлении Sefan не найдена выбранная часть рассказа.");
    }

    private static IReadOnlyList<SefanContentsEntry> ReadIndexEntries(HtmlNode group, Uri index, StoryPage selected)
    {
        var entries = new List<SefanContentsEntry>();
        foreach (var node in group.Descendants())
        {
            if (node.Name == "a")
            {
                if (ReadIndexLink(node, index, selected) is { } entry) entries.Add(entry);
            }
            else if (node.Name is "li" or "b" or "strong" or "span" or "td" &&
                !node.ChildNodes.Any(child => child.NodeType == HtmlNodeType.Element) && CleanText(Render(node, false)).Equals(selected.Title, StringComparison.OrdinalIgnoreCase))
                entries.Add(new SefanContentsEntry(selected.Title, selected.Url));
        }
        return entries;
    }

    private static SefanContentsEntry? ReadIndexLink(HtmlNode anchor, Uri index, StoryPage selected)
    {
        if (!Uri.TryCreate(index, Attribute(anchor, "href"), out var url)) return null;
        try { url = Normalize(url); }
        catch (ArgumentException) { return null; }
        return PageNumber(url) == 1 && Language(url) == Language(selected.Url)
            ? new SefanContentsEntry(CleanText(Render(anchor, false)), url)
            : null;
    }

    private static (Uri Index, Uri Chapter) NormalizeContentsUrl(Uri uri, string part = "1")
    {
        if (!uri.IsAbsoluteUri) throw new ArgumentException("Нужен абсолютный адрес оглавления Sefan.");
        var match = ContentsPath.Match(uri.AbsolutePath);
        if (!match.Success) throw new ArgumentException("Нужен адрес оглавления Sefan.");
        var chapter = new Uri(uri, match.Groups["directory"].Value + match.Groups["slug"].Value + $"_{part}.html");
        chapter = Normalize(chapter);
        return (new Uri("https://sefan.ru" + uri.AbsolutePath), chapter);
    }

    private static Uri ValidateIndexReplacement(Uri expected, Uri candidate)
    {
        var (_, expectedChapter) = NormalizeContentsUrl(expected);
        var (index, chapter) = NormalizeContentsUrl(candidate);
        SameStory(chapter, expectedChapter);
        return index;
    }

    internal static Uri ValidateReplacementUrl(Uri expected, Uri candidate)
    {
        if (ContentsPath.IsMatch(expected.AbsolutePath)) return ValidateIndexReplacement(expected, candidate);
        var replacement = SameStory(candidate, expected);
        if (PageNumber(expected) != PageNumber(replacement))
            throw new ArgumentException("Укажите адрес той же страницы, на которой остановилась загрузка.");
        return replacement;
    }

    internal static bool IsSameReadingStory(Uri candidate, Uri expected)
    {
        try { SameStory(candidate, expected); return true; }
        catch (Exception error) when (error is SefanStoryScopeMismatchException or ArgumentException) { return false; }
    }

    internal static string ChapterKey(Uri uri)
    {
        var match = PathPattern.Match(Normalize(uri).AbsolutePath);
        return $"{match.Groups["language"].Value}/{match.Groups["slug"].Value}_{match.Groups["part"].Value}";
    }

    private static string Language(Uri uri) => PathPattern.Match(uri.AbsolutePath).Groups["language"].Value;
}
