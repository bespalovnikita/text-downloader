using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using TextDownloader.Core;

namespace TextDownloader;

public partial class MainWindow
{
    private readonly SiteAuthorizationStore _authorizationStore;
    private SiteAuthorization[] _siteAuthorization = [];
    private bool _authorizationUnavailable;

    private async Task LoadSiteAuthorizationAsync()
    {
        try { _siteAuthorization = await Task.Run(_authorizationStore.LoadAsync); }
        catch (Exception)
        {
            _authorizationUnavailable = true;
            Log("Не удалось открыть защищённые правила авторизации. Доступна обычная загрузка.",
                DownloadLogLevel.Warning);
        }
    }

    private SiteAuthorization? RuleFor(Uri target) =>
        _siteAuthorization.FirstOrDefault(rule => rule.Enabled && rule.Matches(target));

    private IStorySource CreateSourceFor(TimeSpan delay, IProgress<DownloadLogEvent>? log,
        SiteAuthorization? rule, IReadingBrowser? browser = null)
    {
        if (rule is null && browser is null) return _sourceFactory(delay, log);
        if (rule is not null)
            log?.Report(new DownloadLogEvent(DownloadLogLevel.Information,
                $"Правило авторизации для {rule.Host} · {rule.Method switch
                {
                    SiteAuthorizationMethod.Basic => "HTTP Basic",
                    SiteAuthorizationMethod.Bearer => "Bearer",
                    SiteAuthorizationMethod.Cookies => "Cookies",
                    _ => "браузер"
                }}"));
        return _sessionSourceFactory(delay, log, browser, rule);
    }

    private void OnSiteAuthorization(object sender, RoutedEventArgs e)
    {
        if (!_initialized || !CanEditSettings || !_store.CanWrite) return;
        if (_authorizationUnavailable)
        {
            CurrentStatus = "Защищённые правила авторизации не открылись. Исходный файл сохранён.";
            return;
        }
        var dialog = new SiteAuthorizationWindow(_authorizationStore, _siteAuthorization, SelectedItem?.Url, CheckSiteRuleAsync)
            { Owner = this };
        _previewStop?.Cancel();
        dialog.ShowDialog();
        _siteAuthorization = dialog.Rules;
        if (!_closing && SelectedItem is not null) _previewTask = PreviewSelectedAsync(SelectedItem);
    }

    private ReadingBrowserWindow OpenReadingBrowser(Window owner, SiteAuthorization? rule)
    {
        string directory = rule is null ? Path.Combine(ProfileDirectory, "browser")
            : Path.Combine(ProfileDirectory, "site-browsers", "site-" + rule.Host);
        var browser = new ReadingBrowserWindow(directory, rule) { Owner = owner };
        browser.Show();
        return browser;
    }

    private async Task CheckSiteRuleAsync(SiteAuthorization rule, Window owner, CancellationToken token)
    {
        var requested = new Uri(rule.CheckUrl);
        TimeSpan delay = _settings.GetPageDelay();
        bool confirmAdult = ConfirmAdult;
        using IStorySource available = _sourceFactory(delay, null);
        if (!available.CanHandle(requested))
            throw new InvalidDataException("Адрес проверки должен вести на поддерживаемую страницу чтения.");
        if (!rule.Matches(available.NormalizeToFirstPage(requested)))
            throw new InvalidDataException("Укажите домен, который приложение использует для страницы чтения.");

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        ReadingBrowserWindow? browser = null;
        EventHandler closed = (_, _) => stop.Cancel();
        try
        {
            if (rule.Method == SiteAuthorizationMethod.Browser)
            {
                if (!available.CanReadInBrowser(requested)) throw new InvalidDataException("Источник пока не поддерживает браузер.");
                browser = OpenReadingBrowser(owner, rule);
                browser.Closed += closed;
            }
            await Task.Run(async () =>
            {
                using IStorySource source = CreateSourceFor(delay, null, rule, browser);
                await source.GetPageAsync(requested, confirmAdult, stop.Token).ConfigureAwait(false);
            }, stop.Token);
        }
        finally
        {
            if (browser is not null)
            {
                browser.Closed -= closed;
                if (browser.IsVisible) browser.Close();
            }
        }
    }
}
