using System.Text.RegularExpressions;

namespace TextDownloader.Core;

public sealed partial class StoryDownloader
{
    private static readonly Regex ChapterNumber = new(@"(?:^|[\s(])(?:часть|глава|ч\.)\s*(?:№\s*)?(?<number>[1-9][0-9]*)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ChapterOrdinal = new(@"^\s*(?<number>[1-9][0-9]*)\s*[.)](?:\s|$)", RegexOptions.CultureInvariant);
    private static readonly Regex ChapterSuffix = new(@"[\s.,;:–—-]*\(?(?:часть|глава|ч\.)\s*(?:№\s*)?[1-9][0-9]*[\s).]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private async Task<BookDocument> DownloadSefanBookAsync(SefanSource source, Uri requested, bool confirmAdult, bool refresh,
        IProgress<DownloadProgress>? progress, CancellationToken token, DownloadRecovery? recovery)
    {
        Uri first = GetAttemptStart(requested, refresh, recovery);
        StoryPage selected;
        try { selected = await LoadSefanStartAsync(first, progress).ConfigureAwait(false); }
        catch (SefanStoryScopeMismatchException) when (!SefanSource.IsNeutralGenre(first))
        {
            token.ThrowIfCancellationRequested();
            first = SefanSource.WithNeutralGenre(first);
            progress?.Report(new DownloadProgress(0, null, "Сайт изменил маршрут рассказа. Повторяем запрос через /i/.")
                { LogLevel = DownloadLogLevel.Warning });
            selected = await LoadSefanStartAsync(first, progress).ConfigureAwait(false);
        }
        var contents = selected.Contents!;
        IReadOnlyList<SefanContentsEntry>? plan = null;
        if (contents.Entries.Count > 0)
        {
            try { plan = BuildSefanPlan(source, selected, contents.Entries); }
            catch (InvalidDataException) when (contents.IndexUrl is not null) { }
        }
        if (plan is null && contents.IndexUrl is { } indexUrl)
        {
            progress?.Report(new DownloadProgress(0, null, "Читаем оглавление Sefan…")
            {
                Checkpoint = new DownloadCheckpoint(first, selected.Url, indexUrl)
            });
            Uri request = indexUrl;
            if (recovery?.ReplacementPageUrl is { } candidate)
            {
                try { request = SefanSource.ValidateReplacementUrl(indexUrl, candidate); }
                catch (Exception error) when (error is ArgumentException or SefanStoryScopeMismatchException) { }
            }
            var entries = await source.GetContentsAsync(request, selected, confirmAdult, token).ConfigureAwait(false);
            plan = BuildSefanPlan(source, selected, entries);
            // Save the verified index with the first page so a failed chapter
            // can be retried without downloading the index again.
            selected = selected with { Contents = new SefanContents(null, entries) };
            await WriteCacheAsync(PartCacheDirectory(first), selected, token).ConfigureAwait(false);
        }

        if (plan is null || plan.Count <= 1)
            return await DownloadPartAsync(first, confirmAdult, refresh, progress, token, recovery, selected).ConfigureAwait(false);

        var chapters = new List<BookChapter>(plan.Count);
        int completedPages = 0;
        long characters = 0;
        Uri lastSuccessfulPage = selected.Url;
        string? author = selected.Author;
        for (int index = 0; index < plan.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var entry = plan[index];
            bool isSelected = SefanSource.IsSameReadingStory(entry.Url, selected.Url);
            int partNumber = index + 1;
            Uri previousPage = lastSuccessfulPage;
            var partProgress = new ForwardProgress(item =>
            {
                var checkpoint = item.Checkpoint;
                if (checkpoint?.LastSuccessfulPageUrl is { } last) lastSuccessfulPage = last;
                progress?.Report(item with
                {
                    CompletedPages = completedPages + item.CompletedPages,
                    TotalPages = partNumber == plan.Count ? completedPages + item.TotalPages : null,
                    Message = $"Часть {partNumber} из {plan.Count}. {item.Message}",
                    Checkpoint = checkpoint is null ? null : checkpoint with { LastSuccessfulPageUrl = checkpoint.LastSuccessfulPageUrl ?? previousPage }
                });
            });
            var book = await DownloadPartAsync(isSelected ? first : entry.Url, confirmAdult, refresh, partProgress, token,
                recovery, isSelected ? selected : null, page => ValidateSefanChapter(page, entry.Label, author)).ConfigureAwait(false);
            author ??= book.Author;
            characters += book.Paragraphs.Sum(p => (long)p.Length);
            completedPages += book.PageCount;
            if (characters > MaxStoryCharacters || completedPages > MaxPages)
                throw new InvalidDataException("Цикл Sefan превышает допустимый объём одной книги.");
            chapters.Add(new BookChapter(book.Title, book.SourceUrl, book.Paragraphs));
        }

        string title = ChapterSuffix.Replace(selected.Title, "").Trim();
        if (title.Length == 0) title = selected.Title;
        return new BookDocument(title, author, requested, chapters.SelectMany(chapter => chapter.Paragraphs).ToArray(), completedPages)
            { Chapters = chapters.AsReadOnly() };

        async Task<StoryPage> LoadSefanStartAsync(Uri start, IProgress<DownloadProgress>? report)
        {
            string cache = PartCacheDirectory(start);
            var page = refresh ? null : await ReadCacheAsync(cache, 1, start, start, token).ConfigureAwait(false);
            if (page?.Contents is not null) return page;

            Uri? replacement = GetReplacement(start, recovery);
            Uri next = replacement is not null && SefanSource.PageNumber(replacement) == 1 ? replacement : start;
            report?.Report(new DownloadProgress(0, null, "Получаем первую страницу Sefan…")
            {
                Checkpoint = new DownloadCheckpoint(start, null, next)
            });
            page = await source.GetPageAsync(next, confirmAdult, token).ConfigureAwait(false);
            ValidatePage(page, start, next, 1);
            ValidateChainPage(page, null, []);
            await WriteCacheAsync(cache, page, token).ConfigureAwait(false);
            return page;
        }
    }

    private IReadOnlyList<SefanContentsEntry> BuildSefanPlan(SefanSource source, StoryPage selected,
        IReadOnlyList<SefanContentsEntry> entries)
    {
        var byChapter = new Dictionary<string, SefanContentsEntry>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var normalized = source.NormalizeToFirstPage(entry.Url);
            var key = SefanSource.ChapterKey(normalized);
            if (!byChapter.TryAdd(key, entry with { Url = normalized }) &&
                !SefanSource.IsSameReadingStory(byChapter[key].Url, normalized))
                throw new InvalidDataException("Оглавление Sefan содержит противоречивые адреса части.");
        }
        if (byChapter.Count > MaxPages)
            throw new InvalidDataException("Оглавление Sefan превышает допустимое число частей.");
        if (!byChapter.Values.Any(entry => SefanSource.IsSameReadingStory(entry.Url, selected.Url)))
            throw new InvalidDataException("В оглавлении Sefan не найдена выбранная часть рассказа.");

        var numbered = byChapter.Values.Select(entry => (Entry: entry, Number: GetChapterNumber(entry.Label))).ToArray();
        if (GetChapterNumber(selected.Title) > numbered.Length)
            throw new InvalidDataException("Оглавление Sefan не содержит все предыдущие части.");
        if (numbered.All(item => item.Number is not null))
        {
            Array.Sort(numbered, (left, right) => Nullable.Compare(left.Number, right.Number));
            if (numbered.Where((item, index) => item.Number != index + 1).Any())
                throw new InvalidDataException("Оглавление Sefan пропускает или повторяет номера частей.");
        }
        return numbered.Select(item => item.Entry).ToArray();
    }

    private static void ValidateSefanChapter(StoryPage page, string label, string? author)
    {
        if (!string.IsNullOrWhiteSpace(author) && !string.IsNullOrWhiteSpace(page.Author) &&
            !author.Equals(page.Author, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("У частей из оглавления Sefan указаны разные авторы.");
        if (GetChapterNumber(label) is { } expected && GetChapterNumber(page.Title) is { } actual && actual != expected)
            throw new InvalidDataException("Номер части Sefan отличается от указанного в оглавлении.");
    }

    private static int? GetChapterNumber(string label)
    {
        var match = ChapterOrdinal.Match(label);
        if (!match.Success) match = ChapterNumber.Match(label);
        return int.TryParse(match.Success ? match.Groups["number"].Value : label, out int number) && number > 0 ? number : null;
    }

    private sealed class ForwardProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }
}
