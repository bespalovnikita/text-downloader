using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using TextDownloader;
using TextDownloader.Core;

internal static class BestweaponUiTests
{
    private static readonly Uri Input = new("https://bestweapon.cc/post_48200");
    private static readonly XNamespace Html = "http://www.w3.org/1999/xhtml";
    private static readonly XNamespace Fb2 = "http://www.gribuser.ru/xml/fictionbook/2.0";

    public static async Task RunAsync(string root)
    {
        string profile = Path.Combine(root, "profile");
        var trace = new Calls();
        var window = new MainWindow(profile, () => new SyntheticBookSource(trace), enableTray: false);
        window.Show();
        await window.Ready.WaitAsync(TimeSpan.FromSeconds(10));
        window.OutputDirectory = Path.Combine(root, "books");
        ((TextBox)window.FindName("UrlInput")).Text = Input + "\n" + Input + "?share=synthetic";
        Click(window, "AddButton");
        await UntilAsync(() => window.Items.Count == 1, "Bestweapon post was not added or deduplicated.");
        await window.ActivePreview.WaitAsync(TimeSpan.FromSeconds(10));
        Assert(window.Items[0].Url == Input.AbsoluteUri, "Bestweapon post URL was rewritten as a Sefan page.");
        Assert(window.PreviewTitle == "Синтетический цикл — часть 3" && window.PreviewMetadata.Contains("Bestweapon", StringComparison.Ordinal),
            "Preview did not display the Bestweapon post/source.");
        Assert(((TextBlock)window.FindName("PreviewTitleText")).Text == window.PreviewTitle, "Bestweapon preview was not bound to the real control.");
        Assert(trace.Books == 0 && trace.Previews.SequenceEqual(new[] { Input }), "Preview downloaded more than the chosen post.");

        Click(window, "StartButton");
        await window.ActiveSession.WaitAsync(TimeSpan.FromSeconds(10));
        QueueItem item = window.Items.Single();
        Assert(item.Status == QueueStatus.Completed, "Bestweapon task failed: " + item.Message + "\n" + window.JournalText);
        Assert(trace.Books == 1 && trace.Previews.Count == 1 && trace.BookUrl == Input && !trace.ConfirmedAdult && !trace.Refresh,
            "Whole-book source contract was not called with the chosen URL/settings.");
        Assert(trace.CacheDirectory == Path.Combine(profile, "cache"), "Whole-book source did not receive the profile cache.");
        Assert(item.CompletedPages == 3 && item.TotalPages == 3 && item.Files.Count == 2 && item.Files.All(File.Exists),
            "Bestweapon parts or EPUB/FB2 results were not published.");
        Assert(window.PreviewMetadata.Contains("Bestweapon", StringComparison.Ordinal) && window.PreviewMetadata.Contains("частей: 3", StringComparison.Ordinal),
            "Completed Bestweapon book displayed Sefan page counts.");
        Assert(window.History.Count == 1 && window.History[0].Url == Input.AbsoluteUri && window.History[0].Pages == 3,
            "Bestweapon success was not recorded with the original post URL and part count.");

        var expected = Chapters();
        XDocument fb2 = XDocument.Load(item.Files.Single(path => path.EndsWith(".fb2", StringComparison.OrdinalIgnoreCase)));
        XElement[] sections = fb2.Descendants(Fb2 + "section").Where(section => section.Elements(Fb2 + "p").Any()).ToArray();
        Assert(sections.Length == expected.Length && sections.Select(section => section.Element(Fb2 + "title")!.Element(Fb2 + "p")!.Value)
            .SequenceEqual(expected.Select(chapter => chapter.Title)), "FB2 did not preserve the ordered chapter titles.");
        Assert(sections.SelectMany(section => section.Elements(Fb2 + "p")).Select(paragraph => paragraph.Value)
            .SequenceEqual(expected.SelectMany(chapter => chapter.Paragraphs)), "FB2 mixed or lost chapter paragraphs.");
        using (var zip = ZipFile.OpenRead(item.Files.Single(path => path.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))))
        {
            var navEntry = zip.Entries.Single(entry => entry.FullName.EndsWith("/nav.xhtml", StringComparison.Ordinal));
            using var input = navEntry.Open();
            XDocument navigation = XDocument.Load(input);
            XElement[] links = navigation.Descendants(Html + "a").Where(link => expected.Any(chapter => chapter.Title == link.Value)).ToArray();
            Assert(links.Select(link => link.Value).SequenceEqual(expected.Select(chapter => chapter.Title)),
                "EPUB table of contents did not preserve the chapter order.");
            string prefix = navEntry.FullName[..(navEntry.FullName.LastIndexOf('/') + 1)];
            foreach (var link in links)
                Assert(zip.GetEntry(prefix + link.Attribute("href")!.Value.Split('#')[0]) is not null, "EPUB chapter navigation points at a missing document.");
        }

        window.Close();
        await UntilAsync(() => !window.IsVisible, "Bestweapon window did not close.");
        var restored = new MainWindow(profile, () => new SyntheticBookSource(trace), enableTray: false);
        restored.Show();
        await restored.Ready.WaitAsync(TimeSpan.FromSeconds(10));
        Assert(!restored.IsBusy && trace.Books == 1 && restored.Items.Single().Url == Input.AbsoluteUri && restored.History.Count == 1,
            "Bestweapon queue/history failed to restore or downloaded automatically.");
        Assert(restored.Items.Single().Files.All(File.Exists), "Restored Bestweapon results were lost.");
        restored.Close();
        await UntilAsync(() => !restored.IsVisible, "Restored Bestweapon window did not close.");
    }

    private static BookChapter[] Chapters() =>
    [
        new("Синтетическая часть 1", new Uri("https://bestweapon.cc/post_48001"), ["Первый искусственный абзац.", "Продолжение первой части."]),
        new("Синтетическая часть 2", new Uri("https://bestweapon.cc/post_48123"), ["Второй искусственный абзац.", "Продолжение второй части."]),
        new("Синтетическая часть 3", Input, ["Третий искусственный абзац.", "Синтетический конец."]),
    ];
    private static void Click(Window window, string name)
    {
        var button = (Button)window.FindName(name);
        Assert(button.IsEnabled, "Disabled control: " + name);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static async Task UntilAsync(Func<bool> predicate, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline) throw new Exception(message);
            await Task.Delay(20);
        }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class Calls
    {
        public List<Uri> Previews { get; } = [];
        public int Books;
        public Uri? BookUrl;
        public bool ConfirmedAdult, Refresh;
        public string? CacheDirectory;
    }
    private sealed class SyntheticBookSource(Calls calls) : IBookStorySource
    {
        public bool CanHandle(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == "https" && uri.Host == "bestweapon.cc" &&
            Regex.IsMatch(uri.AbsolutePath, @"^/post_[1-9]\d*$", RegexOptions.CultureInvariant);
        public Uri NormalizeToFirstPage(Uri uri) => CanHandle(uri) ? new Uri("https://bestweapon.cc" + uri.AbsolutePath) : throw new ArgumentException("Unsupported synthetic URL.");
        public async Task<StoryPage> GetPageAsync(Uri uri, bool confirmAdult, CancellationToken token)
        {
            calls.Previews.Add(uri);
            await Task.Delay(20, token);
            return new StoryPage(uri, 1, "Синтетический цикл — часть 3", "Синтетический автор", "Предпросмотр одной выбранной публикации.", null, null);
        }
        public async Task<BookDocument> DownloadBookAsync(Uri uri, bool confirmAdult, bool refresh, string cacheDirectory,
            IProgress<DownloadProgress>? progress, CancellationToken token)
        {
            calls.Books++;
            calls.BookUrl = uri;
            calls.ConfirmedAdult = confirmAdult;
            calls.Refresh = refresh;
            calls.CacheDirectory = cacheDirectory;
            var chapters = Chapters();
            for (int index = 0; index < chapters.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                progress?.Report(new DownloadProgress(index + 1, chapters.Length, $"Скачана часть {index + 1}"));
                await Task.Delay(20, token);
            }
            return new BookDocument("Синтетический цикл", "Синтетический автор", chapters[0].SourceUrl,
                chapters.SelectMany(chapter => chapter.Paragraphs).ToArray(), chapters.Length) { Chapters = chapters };
        }
        public void Dispose() { }
    }
}
