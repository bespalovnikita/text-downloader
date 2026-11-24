using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using TextDownloader.Core;

namespace TextDownloader;

internal partial class SiteAuthorizationWindow : Window
{
    private readonly SiteAuthorizationStore _store;
    private readonly Func<SiteAuthorization, Window, CancellationToken, Task> _check;
    private readonly ObservableCollection<RuleRow> _rows = [];
    private readonly string _suggestedUrl;
    private SiteAuthorization? _editing;
    private CancellationTokenSource? _checkStop;
    private bool _saving, _closed;
    private SiteAuthorization? _lastCheckedRule;

    public SiteAuthorizationWindow(SiteAuthorizationStore store, IEnumerable<SiteAuthorization> rules, string? suggestedUrl,
        Func<SiteAuthorization, Window, CancellationToken, Task> check)
    {
        _store = store;
        _check = check;
        _suggestedUrl = suggestedUrl ?? "";
        InitializeComponent();
        RulesList.ItemsSource = _rows;
        foreach (var rule in rules.OrderBy(rule => rule.Host, StringComparer.Ordinal))
            _rows.Add(new RuleRow(rule));
        RulesList.SelectedIndex = _rows.Count == 0 ? -1 : 0;
        if (_rows.Count == 0) ShowRule(null);
    }

    public SiteAuthorization[] Rules => _rows.Select(row => row.Rule).ToArray();

    private SiteAuthorizationMethod Method => Mode.SelectedValue is SiteAuthorizationMethod value
        ? value : SiteAuthorizationMethod.Browser;

    private void ShowRule(SiteAuthorization? rule)
    {
        _editing = rule;
        if (rule is null && Uri.TryCreate(_suggestedUrl, UriKind.Absolute, out var url))
            HostInput.Text = url.IdnHost;
        else HostInput.Text = rule?.Host ?? "";
        Mode.SelectedValue = rule?.Method ?? SiteAuthorizationMethod.Browser;
        EnabledRule.IsChecked = rule?.Enabled ?? true;
        LoginInput.Text = rule?.UserName ?? "";
        FillForm.IsChecked = rule is { Method: SiteAuthorizationMethod.Browser, Secret.Length: > 0 };
        SignInInput.Text = rule?.SignInUrl ?? "";
        SignInHostsInput.Text = string.Join(", ", rule?.SignInHosts ?? []);
        UserFieldInput.Text = rule?.UserField ?? "";
        PasswordFieldInput.Text = rule?.PasswordField ?? "";
        SecretInput.Clear();
        CheckUrlInput.Text = rule?.CheckUrl ?? _suggestedUrl;
        _lastCheckedRule = rule;
        RemoveRule.IsEnabled = rule is not null;
        Status.Text = "";
        RefreshFields();
    }

    private void RefreshFields()
    {
        if (FormFieldNames is null || SecretLabel is null) return;
        bool isBrowser = Method == SiteAuthorizationMethod.Browser;
        bool isForm = isBrowser && FillForm.IsChecked == true;
        BrowserSettings.Visibility = isBrowser ? Visibility.Visible : Visibility.Collapsed;
        LoginFields.Visibility = Method == SiteAuthorizationMethod.Basic || isForm ? Visibility.Visible : Visibility.Collapsed;
        FormFieldNames.Visibility = isForm ? Visibility.Visible : Visibility.Collapsed;
        SecretFields.Visibility = !isBrowser || isForm ? Visibility.Visible : Visibility.Collapsed;
        SecretLabel.Content = Method switch
        {
            SiteAuthorizationMethod.Bearer => "Bearer-токен",
            SiteAuthorizationMethod.Cookies => "Cookies (name=value; other=value)",
            _ => "Пароль"
        };
    }

    private SiteAuthorization ReadRule()
    {
        string host = SiteAuthorization.NormalizeHost(HostInput.Text);
        if (_rows.Any(row => row.Rule != _editing && row.Rule.Host == host))
            throw new ArgumentException("Для этого домена уже есть правило.");
        var method = Method;
        bool browser = method == SiteAuthorizationMethod.Browser;
        bool form = browser && FillForm.IsChecked == true;
        string secret = SecretInput.Password;
        if (secret.Length == 0 && _editing is { } previous && previous.Host == host && previous.Method == method)
            secret = previous.Secret;
        if (form && (secret.Length == 0 || LoginInput.Text.Trim().Length == 0))
            throw new ArgumentException("Введите логин и пароль для веб-формы.");
        var rule = new SiteAuthorization
        {
            Host = host,
            Method = method,
            Enabled = EnabledRule.IsChecked == true,
            UserName = form || method == SiteAuthorizationMethod.Basic ? LoginInput.Text.Trim() : "",
            Secret = browser && !form ? "" : secret,
            SignInUrl = browser ? SignInInput.Text.Trim() : "",
            SignInHosts = browser ? SignInHostsInput.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(SiteAuthorization.NormalizeHost).Distinct(StringComparer.Ordinal).ToArray() : [],
            UserField = form ? UserFieldInput.Text.Trim() : "",
            PasswordField = form ? PasswordFieldInput.Text.Trim() : "",
            CheckUrl = CheckUrlInput.Text.Trim()
        };
        rule.Validate();
        return rule;
    }

    private void OnSelect(object sender, SelectionChangedEventArgs e)
    {
        if (HostInput is not null) ShowRule((RulesList.SelectedItem as RuleRow)?.Rule);
    }
    private void OnModeChanged(object sender, SelectionChangedEventArgs e) => RefreshFields();
    private void OnFillFormChanged(object sender, RoutedEventArgs e) => RefreshFields();
    private void OnNew(object sender, RoutedEventArgs e)
    {
        RulesList.SelectedItem = null;
        ShowRule(null);
        HostInput.Focus();
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        SiteAuthorization rule;
        try { rule = ReadRule(); }
        catch (ArgumentException error) { Status.Text = error.Message; return; }

        if (_lastCheckedRule is { } checkedRule && checkedRule.SignInHosts.SequenceEqual(rule.SignInHosts) &&
            (checkedRule with { Enabled = rule.Enabled, SignInHosts = rule.SignInHosts, CheckedAt = null, CheckSucceeded = null }) == rule)
            rule = rule with { CheckedAt = checkedRule.CheckedAt, CheckSucceeded = checkedRule.CheckSucceeded };

        var next = Rules.Where(item => item != _editing).Append(rule).OrderBy(item => item.Host, StringComparer.Ordinal).ToArray();
        await SaveAsync(next, rule.Host, "Правило сохранено.");
    }

    private async void OnRemove(object sender, RoutedEventArgs e)
    {
        if (_editing is not { } selected) return;
        await SaveAsync(Rules.Where(item => item != selected).ToArray(), null, "Правило удалено.");
    }

    private async Task SaveAsync(SiteAuthorization[] rules, string? selectedHost, string done)
    {
        SetBusy(true);
        _saving = true;
        try
        {
            await Task.Run(() => _store.SaveAsync(rules));
            _rows.Clear();
            foreach (var rule in rules) _rows.Add(new RuleRow(rule));
            RulesList.SelectedItem = _rows.FirstOrDefault(row => row.Rule.Host == selectedHost);
            Status.Text = done;
        }
        catch (Exception) { Status.Text = "Не удалось записать защищённые настройки авторизации."; }
        finally { _saving = false; SetBusy(false); }
    }

    private async void OnCheck(object sender, RoutedEventArgs e)
    {
        SiteAuthorization rule;
        bool succeeded;
        try
        {
            rule = ReadRule();
            if (rule.CheckUrl.Length == 0) throw new ArgumentException("Укажите ссылку на страницу чтения для проверки.");
        }
        catch (ArgumentException error) { Status.Text = error.Message; return; }

        Status.Text = "Проверяем доступ к странице…";
        using var stop = new CancellationTokenSource();
        _checkStop = stop;
        SetBusy(true);
        try
        {
            await _check(rule with { Enabled = true }, this, stop.Token);
            succeeded = true;
            Status.Text = "Страница доступна. Сохраните правило, чтобы запомнить результат проверки.";
        }
        catch (OperationCanceledException) { Status.Text = "Проверка остановлена."; return; }
        catch (Exception)
        {
            succeeded = false;
            Status.Text = "Не удалось получить страницу чтения. Проверьте адрес и способ входа.";
        }
        finally
        {
            _checkStop = null;
            if (!_closed) SetBusy(false);
        }
        _lastCheckedRule = rule with { CheckedAt = DateTimeOffset.UtcNow, CheckSucceeded = succeeded };
    }

    private void SetBusy(bool busy)
    {
        Editor.IsEnabled = RulesList.IsEnabled = NewRule.IsEnabled = CheckRule.IsEnabled = SaveRule.IsEnabled = !busy;
        RemoveRule.IsEnabled = !busy && _editing is not null;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_saving) { e.Cancel = true; return; }
        _checkStop?.Cancel();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e) { _closed = true; base.OnClosed(e); }
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private sealed record RuleRow(SiteAuthorization Rule)
    {
        public string Summary => (Rule.Method switch
        {
            SiteAuthorizationMethod.Basic => "HTTP Basic",
            SiteAuthorizationMethod.Bearer => "Bearer-токен",
            SiteAuthorizationMethod.Cookies => "Cookies",
            _ => "Браузер"
        }) + (Rule.Enabled ? "" : " · отключено") + (Rule.CheckSucceeded switch
        {
            true => " · доступ есть",
            false => " · ошибка входа",
            null => ""
        }) + (Rule.CheckedAt is { } checkedAt ? $" · {checkedAt.ToLocalTime():dd.MM HH:mm}" : "");
    }
}
