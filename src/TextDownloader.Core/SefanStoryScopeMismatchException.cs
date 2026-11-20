namespace TextDownloader.Core;

/// <summary>A source link changed the story identity or its non-neutral genre route.</summary>
public sealed class SefanStoryScopeMismatchException : IOException
{
    public Uri OriginalUrl { get; }
    public Uri RejectedUrl { get; }

    public SefanStoryScopeMismatchException(Uri originalUrl, Uri rejectedUrl)
        : base("Ссылка ведёт на другой рассказ; загрузка остановлена: " + rejectedUrl.AbsoluteUri)
    {
        OriginalUrl = originalUrl;
        RejectedUrl = rejectedUrl;
    }
}
