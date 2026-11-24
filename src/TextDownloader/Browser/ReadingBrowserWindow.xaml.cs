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

internal partial class ReadingBrowserWindow : Window, IReadingBrowser
{
    private const string ReadDocument = """
        (() => {
          const html = document.documentElement?.outerHTML;
          return html && html.length <= 4194304 ? html : null;
        })()
        """;
    private readonly string _profile;
    private readonly SiteAuthorization? _rule;
    private BrowserRequest? _request;
    private bool _closed;

    public ReadingBrowserWindow(string profile, SiteAuthorization? rule = null)
    {
        _profile = profile;
        _rule = rule;
        InitializeComponent();
        OpenSignIn.Visibility = rule is { SignInUrl.Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        FillSignIn.Visibility = rule is { Secret.Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        OpenSignIn.IsEnabled = FillSignIn.IsEnabled = false;
    }

    public Task<T> ReadPageAsync<T>(Uri url, Func<Uri, string, T> readPage, CancellationToken token) where T : class =>
        Dispatcher.InvokeAsync(() => ReadOnDispatcherAsync(url, readPage, token), DispatcherPriority.Normal, token).Task.Unwrap();

    private async Task<T> ReadOnDispatcherAsync<T>(Uri url, Func<Uri, string, T> readPage, CancellationToken token) where T : class
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

        var request = new BrowserRequest(url, (address, html) => readPage(address, html), token);
        _request = request;
        RequestedAddress.Text = url.AbsoluteUri;
        CheckPage.IsEnabled = OpenExpected.IsEnabled = true;
        OpenSignIn.IsEnabled = FillSignIn.IsEnabled = _rule?.Matches(url) == true;
        try
        {
            NavigateToExpectedPage(request);
            return (T)await request.Completion.Task.WaitAsync(token);
        }
        finally
        {
            _request = null;
            if (!_closed)
            {
                CheckPage.IsEnabled = OpenExpected.IsEnabled = OpenSignIn.IsEnabled = FillSignIn.IsEnabled = false;
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
        if (_rule is not null)
        {
            browser.Settings.IsPasswordAutosaveEnabled = false;
            browser.Settings.IsGeneralAutofillEnabled = false;
        }
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
            !SiteAuthorization.IsHttps(uri) || !CanNavigate(request.Url, uri))
        {
            e.Cancel = true;
            BrowserStatus.Text = "Браузер остановил переход за пределы сайта.";
        }
    }

    private bool CanNavigate(Uri expected, Uri actual) =>
        _rule is null
            ? actual.Host == expected.Host || actual.Host == "www." + expected.Host
            : _rule.Matches(expected) && (_rule.Matches(actual) || Array.IndexOf(_rule.SignInHosts, actual.IdnHost) >= 0);

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

    private void OnOpenSignIn(object sender, RoutedEventArgs e)
    {
        if (_request is { } request && _rule is { SignInUrl.Length: > 0 } && _rule.Matches(request.Url))
            PageView.CoreWebView2.Navigate(_rule.SignInUrl);
    }

    private async void OnFillSignIn(object sender, RoutedEventArgs e)
    {
        if (_request is not { } request || _rule is not { Secret.Length: > 0 } || !_rule.Matches(request.Url)) return;
        try
        {
            var arguments = JsonSerializer.Serialize(new
            {
                url = _rule.SignInUrl, userField = _rule.UserField, passwordField = _rule.PasswordField,
                login = _rule.UserName, secret = _rule.Secret
            });
            const string fill = """
                (settings => {
                  const target = new URL(settings.url);
                  if (location.origin !== target.origin || location.pathname !== target.pathname ||
                      location.search !== target.search) return false;
                  const fields = name => Array.from(document.querySelectorAll('input')).filter(x => x.name === name);
                  const users = fields(settings.userField), passwords = fields(settings.passwordField);
                  if (users.length !== 1 || passwords.length !== 1) return false;
                  const user = users[0], password = passwords[0], form = user.form;
                  if (!form || form !== password.form || user.disabled || password.disabled ||
                      user.readOnly || password.readOnly || password.type !== 'password' ||
                      !['text', 'email', 'tel'].includes(user.type)) return false;
                  const action = new URL(form.action, location.href);
                  if (form.method !== 'post' || action.origin !== target.origin || action.username || action.password) return false;
                  const setValue = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
                  for (const [field, value] of [[user, settings.login], [password, settings.secret]]) {
                    setValue.call(field, value);
                    field.dispatchEvent(new Event('input', {bubbles: true}));
                    field.dispatchEvent(new Event('change', {bubbles: true}));
                  }
                  return true;
                })
                """;
            string result = await PageView.CoreWebView2.ExecuteScriptAsync(fill + "(" + arguments + ")");
            if (!_closed && _request == request)
                BrowserStatus.Text = result == "true" ? "Поля заполнены. Подтвердите вход на странице." :
                    "Откройте настроенную страницу входа и проверьте имена полей формы.";
        }
        catch (Exception)
        {
            if (!_closed && _request == request) BrowserStatus.Text = "Не удалось подставить данные на странице входа.";
        }
    }

    private void OnPause(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        base.OnClosed(e);
        PageView.Dispose();
    }

    private sealed record BrowserRequest(Uri Url, Func<Uri, string, object> Read, CancellationToken Token)
    {
        public TaskCompletionSource<object> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Checking { get; set; }
    }
}
