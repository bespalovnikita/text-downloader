using System.Security.Cryptography;
using System.Text;

namespace TextDownloader.Core;

public static class StoryCacheLayout
{
    internal static string Pages(string root, Uri first) => Path.Combine(root, Key(first));
    internal static string Bestweapon(string root, Uri first) => Path.Combine(root, "bestweapon", Key(first));

    // Historical versions used shared folders named after the first reading URL.
    // These paths are only used to assess the age of a saved attempt.
    public static IEnumerable<string> LegacyDirectories(string root, Uri url, DownloadCheckpoint? checkpoint)
    {
        if (url.Host is "sefan.ru" or "www.sefan.ru")
        {
            using var source = new SefanSource();
            var urls = new[] { url, checkpoint?.AttemptStartUrl, checkpoint?.LastSuccessfulPageUrl };
            foreach (var candidate in urls.OfType<Uri>().Where(source.CanHandle))
            {
                var first = source.NormalizeToFirstPage(candidate);
                yield return Pages(root, first);
                yield return Pages(root, SefanSource.WithNeutralGenre(first));
            }
        }
        else if (url.Host is "bestweapon.cc" or "www.bestweapon.cc")
        {
            using var source = new BestweaponSource();
            if (source.CanHandle(url)) yield return Bestweapon(root, source.NormalizeToFirstPage(url));
        }
    }

    private static string Key(Uri first) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(first.AbsoluteUri))).ToLowerInvariant();
}
