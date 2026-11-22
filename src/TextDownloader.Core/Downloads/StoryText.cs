using System.Text.RegularExpressions;

namespace TextDownloader.Core;

internal static class StoryText
{
    public static string Clean(string text) => Regex.Replace(
        Regex.Replace(text, @"[\x00-\x08\x0B\x0C\x0E-\x1F]", ""), @"[^\S\n]+", " ").Trim();
}
