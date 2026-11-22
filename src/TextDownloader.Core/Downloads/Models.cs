using System.Text.Json.Serialization;

namespace TextDownloader.Core;

public enum BookFormat { Epub, Fb2, Both }

public sealed record StoryPage(Uri Url, int Number, string Title, string? Author,
    string Fragment, Uri? NextUrl, int? TotalPages)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SefanContents? Contents { get; init; }
}

public sealed record BookChapter(string Title, Uri SourceUrl, IReadOnlyList<string> Paragraphs);

public sealed record BookDocument(string Title, string? Author, Uri SourceUrl,
    IReadOnlyList<string> Paragraphs, int PageCount)
{
    /// <summary>Ordered logical chapters; null represents a story without separate chapters.</summary>
    public IReadOnlyList<BookChapter>? Chapters { get; init; }
}

public enum DownloadLogLevel { Detail, Information, Warning, Error }

public sealed record DownloadLogEvent(DownloadLogLevel Level, string Message);

public sealed record DownloadProgress(int CompletedPages, int? TotalPages, string Message)
{
    public DownloadCheckpoint? Checkpoint { get; init; }
    public DownloadLogLevel LogLevel { get; init; }
}

public sealed record DownloadCheckpoint(Uri AttemptStartUrl, Uri? LastSuccessfulPageUrl, Uri? PendingPageUrl);

public sealed record DownloadRecovery(DownloadCheckpoint? Checkpoint, Uri? ReplacementPageUrl);

public sealed class AgeConfirmationRequiredException : Exception
{
    public AgeConfirmationRequiredException() : base("Sefan запросил подтверждение возраста. Если вам исполнилось 18 лет, включите подтверждение в настройках задания.") { }
}
