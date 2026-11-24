using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using TextDownloader.Core;

namespace TextDownloader;

internal partial class ReadingBrowserWindow : Window
{
    private const string ReadDocument = """
        (() => {
          const html = document.documentElement?.outerHTML;
          return html && html.length <= 4194304 ? html : null;
        })()
        """;
    private readonly string _profile;
    private BrowserRequest? _request;
    private bool _closed;

    public ReadingBrowserWindow(string profile)
    {
        _profile = profile;
        InitializeComponent();
    }

    public Task<StoryPage> ReadPageAsync(Uri url, Func<Uri, string, StoryPage> readPage, CancellationToken token) =>
        Dispatcher.InvokeAsync(() => ReadOnDispatcherAsync(url, readPage, token), DispatcherPriority.Normal, token).Task.Unwrap();

    private async Task<StoryPage> ReadOnDispatcherAsync(Uri url, Func<Uri, string, StoryPage> readPage, CancellationToken token)
    {
        if (_closed) throw new OperationCanceledException(token);
        try
        {
            if (PageView.CoreWebView2 is null) await InitializeBrowserAsync(token);
        }
        catch (WebView2RuntimeNotFoundException error)
        {
            throw new IOException("Для продолжения через браузер требуется Microsoft Edge WebView2 Runtime.", error);
        }
        if (_closed) throw new OperationCanceledException(token);

        var request = new BrowserRequest(url, readPage, token);
        _request = request;
        RequestedAddress.Text = url.AbsoluteUri;
        CheckPage.IsEnabled = OpenExpected.IsEnabled = true;
        try
        {
            NavigateToExpectedPage(request);
            return await request.Completion.Task.WaitAsync(token);
        }
        finally
        {
            _request = null;
            if (!_closed)
            {
                CheckPage.IsEnabled = OpenExpected.IsEnabled = false;
                if (token.IsCancellationRequested) PageView.Stop();
            }
        }
    }

    internal async Task InitializeBrowserAsync(CancellationToken token)
    {
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: _profile).WaitAsync(token);
        if (_closed) throw new OperationCanceledException(token);
        await PageView.EnsureCoreWebView2Async(environment).WaitAsync(token);
        if (_closed) throw new OperationCanceledException(token);
        var browser = PageView.CoreWebView2;
        browser.Settings.AreDevToolsEnabled = false;
        browser.Settings.AreHostObjectsAllowed = false;
        browser.Settings.IsWebMessageEnabled = false;
        browser.Settings.AreDefaultContextMenusEnabled = false;
        browser.NavigationStarting += OnNavigationStarting;
        browser.NavigationCompleted += OnNavigationCompleted;
        browser.NewWindowRequested += (_, args) => args.Handled = true;
        browser.DownloadStarting += (_, args) => args.Cancel = true;
    }

    private void NavigateToExpectedPage(BrowserRequest request)
    {
        BrowserStatus.Text = "Открываем страницу…";
        PageView.CoreWebView2.Navigate(request.Url.AbsoluteUri);
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (_request is not { } request ||
            !Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            uri.Host != request.Url.Host && uri.Host != "www." + request.Url.Host)
        {
            e.Cancel = true;
            BrowserStatus.Text = "Браузер остановил переход за пределы сайта.";
        }
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_request is not { } request) return;
        if (e.IsSuccess) await CheckOpenedPageAsync(request);
        else if (e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
            BrowserStatus.Text = "Браузер не смог открыть страницу. Попробуйте открыть её снова.";
    }

    private async void OnCheckPage(object sender, RoutedEventArgs e)
    {
        if (_request is { } request) await CheckOpenedPageAsync(request);
    }

    private async Task CheckOpenedPageAsync(BrowserRequest request)
    {
        if (request.Checking || request.Completion.Task.IsCompleted || request.Token.IsCancellationRequested) return;
        request.Checking = true;
        Uri? actual = null;
        try
        {
            var browser = PageView.CoreWebView2;
            string current = browser.Source;
            string result = await browser.ExecuteScriptAsync(ReadDocument);
            if (current != browser.Source || _request != request || _closed) return;
            if (!Uri.TryCreate(current, UriKind.Absolute, out actual))
                throw new InvalidDataException("Браузер пока не открыл страницу.");
            var html = JsonSerializer.Deserialize<string>(result) ?? throw new InvalidDataException("Страница пуста или слишком велика.");
            var page = await Task.Run(() => request.Read(actual, html), request.Token);
            if (_request == request && !_closed && current == browser.Source) request.Completion.TrySetResult(page);
        }
        catch (Exception error)
        {
            if (_request == request && !_closed && !request.Token.IsCancellationRequested)
            {
                BrowserStatus.Text = error is AgeConfirmationRequiredException
                    ? "Подтвердите возраст на странице, если вам исполнилось 18 лет."
                    : (actual?.Scheme == Uri.UriSchemeHttps ? $"Открыто {actual.GetLeftPart(UriPartial.Path)}. " : "") +
                      "Когда откроется ожидаемая страница, нажмите «Проверить открытую страницу».";
            }
        }
        finally { request.Checking = false; }
    }

    private void OnOpenExpected(object sender, RoutedEventArgs e)
    {
        if (_request is { } request) NavigateToExpectedPage(request);
    }

    private void OnPause(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        base.OnClosed(e);
        PageView.Dispose();
    }

    private sealed record BrowserRequest(Uri Url, Func<Uri, string, StoryPage> Read, CancellationToken Token)
    {
        public TaskCompletionSource<StoryPage> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Checking { get; set; }
    }
}
