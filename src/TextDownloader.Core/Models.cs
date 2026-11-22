namespace TextDownloader.Core;

public enum BookFormat { Epub, Fb2, Both }

public sealed record StoryPage(Uri Url, int Number, string Title, string? Author,
    string Fragment, Uri? NextUrl, int? TotalPages);

public sealed record BookChapter(string Title, Uri SourceUrl, IReadOnlyList<string> Paragraphs);

public sealed record BookDocument(string Title, string? Author, Uri SourceUrl,
    IReadOnlyList<string> Paragraphs, int PageCount)
{
    /// <summary>Ordered logical chapters; null preserves the continuous Sefan text format.</summary>
    public IReadOnlyList<BookChapter>? Chapters { get; init; }
}

public sealed record DownloadProgress(int CompletedPages, int? TotalPages, string Message)
{
    public DownloadCheckpoint? Checkpoint { get; init; }
}

public sealed record DownloadCheckpoint(Uri AttemptStartUrl, Uri? LastSuccessfulPageUrl, Uri? PendingPageUrl);

public sealed record DownloadRecovery(DownloadCheckpoint? Checkpoint, Uri? ReplacementPageUrl);

public interface IStorySource : IDisposable
{
    bool CanHandle(Uri uri);
    Uri NormalizeToFirstPage(Uri uri);
    Task<StoryPage> GetPageAsync(Uri uri, bool confirmAdult, CancellationToken cancellationToken);
}

public sealed class AgeConfirmationRequiredException : Exception
{
    public AgeConfirmationRequiredException() : base("Sefan запросил подтверждение возраста. Если вам исполнилось 18 лет, включите подтверждение в настройках задания.") { }
}
