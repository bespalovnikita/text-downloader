using System.Net;

namespace TextDownloader.Core;

internal static class StoryRedirect
{
    public const int Limit = 5;

    public static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    public static Uri ReadLocation(Uri responseUri, HttpResponseMessage response, int redirectsFollowed)
    {
        if (response.Headers.Location is not { } location || !Uri.TryCreate(responseUri, location, out var target))
            throw new InvalidDataException($"HTTP {(int)response.StatusCode}. Сайт не указал корректный адрес перенаправления (Location).");
        if (redirectsFollowed >= Limit)
            throw new InvalidDataException("Слишком много перенаправлений сайта.");
        return target;
    }

    // Call after checking the source's URL scope. Keep server-supplied path and
    // query for transport while retaining HTTPS on the default port.
    public static Uri ForRequest(Uri verified) =>
        verified.Scheme == Uri.UriSchemeHttps && verified.IsDefaultPort && verified.Fragment.Length == 0
            ? verified
            : new UriBuilder(verified) { Scheme = Uri.UriSchemeHttps, Port = -1, Fragment = "" }.Uri;
}
