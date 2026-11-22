using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using TextDownloader.Core;
using TextDownloader.Core.Updates;
using TextDownloader.Updates;
using Forms = System.Windows.Forms;

namespace TextDownloader;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly AppStore _store;
    private readonly Func<TimeSpan, IProgress<DownloadLogEvent>?, IStorySource> _sourceFactory;
    private readonly bool _enableTray;
    private readonly IAppUpdateClient _updateClient;
    private readonly bool _ownsUpdateClient;
    private readonly string _updateTargetDirectory;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Settings _settings = new();
    private CancellationTokenSource? _sessionStop;
    private CancellationTokenSource? _previewStop;
    private Task _sessionTask = Task.CompletedTask;
    private Task _previewTask = Task.CompletedTask;
    private Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _trayIcon;
    private bool _initialized, _closing, _allowClose, _saveFailed;
    private StopReason _stopReason;
    private enum StopReason { None, Pause, Cancel }

    public ObservableCollection<QueueItem> Items { get; } = new();
    public ObservableCollection<HistoryEntry> History { get; } = new();
    public Task Ready => _ready.Task;
    public Task ActiveSession => _sessionTask;
    public Task ActivePreview => _previewTask;
    public string ProfileDirectory => _store.DirectoryPath;
    public UpdateWindow? CurrentUpdateWindow { get; private set; }
    internal PreparedAppUpdate? PendingAppUpdate { get; private set; }

    private QueueItem? _selectedItem;
    public QueueItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!Set(ref _selectedItem, value)) return;
            Changed(nameof(CanCopySelectedUrl));
            if (JournalSelectedOnly) JournalView.Refresh();
            RefreshPageRecovery();
            if (_initialized && !IsUpdating) _previewTask = PreviewSelectedAsync(value);
        }
    }
    private QueueItem? _recoveryItem;
    public QueueItem? RecoveryItem
    {
        get => _recoveryItem;
        private set
        {
            if (!Set(ref _recoveryItem, value)) return;
            RecoveryPageInput.Text = value?.Checkpoint?.PendingPageUrl?.AbsoluteUri ?? "";
            RecoveryError.Text = "";
        }
    }

    private QueueItem? _activeItem;
    public QueueItem? ActiveItem { get => _activeItem; private set => Set(ref _activeItem, value); }
    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set { if (Set(ref _isBusy, value)) { Changed(nameof(CanStart)); Changed(nameof(CanEditSettings)); Changed(nameof(StartLabel)); CurrentUpdateWindow?.RefreshInstallEligibility(); } }
    }
    private bool _isUpdating;
    public bool IsUpdating
    {
        get => _isUpdating;
        private set
        {
            if (!Set(ref _isUpdating, value)) return;
            Changed(nameof(CanStart)); Changed(nameof(CanEditSettings));
            Changed(nameof(CanReceiveInput)); Changed(nameof(CanCopySelectedUrl)); Changed(nameof(CanOpenUpdates));
        }
    }
    public bool CanStart => _initialized && _validPageDelay && !IsBusy && !IsUpdating && !_closing && _store.CanWrite && !_saveFailed;
    public bool CanEditSettings => !IsBusy && !IsUpdating && !_closing;
    public bool CanReceiveInput => _initialized && !IsUpdating && !_closing;
    public bool CanCopySelectedUrl => CanReceiveInput && SelectedItem is not null;
    public bool CanOpenUpdates => _initialized && !IsUpdating && !_closing;
    public string StartLabel => Items.Any(q => q.Status == QueueStatus.Paused) ? "▶ Продолжить очередь" : "▶ Скачать очередь";
    public string QueueCountText => $"{Items.Count} · готово {Items.Count(q => q.Status == QueueStatus.Completed)}";
    public BookFormat Format { get => _settings.Format; set { if (IsUpdating) return; _settings.Format = value; Changed(); ScheduleSave(); } }
    public string OutputDirectory { get => _settings.OutputDirectory; set { if (IsUpdating) return; _settings.OutputDirectory = value; Changed(); ScheduleSave(); } }
    public bool ConfirmAdult { get => _settings.ConfirmAdult; set { if (IsUpdating) return; _settings.ConfirmAdult = value; Changed(); ScheduleSave(); } }
    public bool RefreshCache { get => _settings.RefreshCache; set { if (IsUpdating) return; _settings.RefreshCache = value; Changed(); ScheduleSave(); } }
    public bool MinimizeToTray { get => _settings.MinimizeToTray; set { if (IsUpdating) return; _settings.MinimizeToTray = value; Changed(); ScheduleSave(); } }
    private string _pageDelayText = "1";
    private bool _validPageDelay = true;
    public string PageDelayText
    {
        get => _pageDelayText;
        set
        {
            if (IsUpdating || !Set(ref _pageDelayText, value)) return;
            _validPageDelay = decimal.TryParse(value.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out decimal seconds) &&
                seconds is >= Settings.MinimumPageDelaySeconds and <= Settings.MaximumPageDelaySeconds;
            if (_validPageDelay)
            {
                _settings.PageDelaySeconds = seconds;
                ScheduleSave();
            }
            Changed(nameof(PageDelayError));
            Changed(nameof(CanStart));
        }
    }
    public string PageDelayError => _validPageDelay ? "" : "Введите задержку в секундах, от 0,1.";
    private string _currentStatus = "Добавьте ссылку на рассказ Sefan или публикацию Bestweapon.";
    public string CurrentStatus { get => _currentStatus; private set => Set(ref _currentStatus, value); }
    private string _previewTitle = "Выберите рассказ";
    public string PreviewTitle { get => _previewTitle; private set => Set(ref _previewTitle, value); }
    private string _previewMetadata = "Sefan / Bestweapon · EPUB / FB2";
    public string PreviewMetadata { get => _previewMetadata; private set => Set(ref _previewMetadata, value); }
    private string _previewStatus = "Сведения загружаются при выборе строки.";
    public string PreviewStatus { get => _previewStatus; private set => Set(ref _previewStatus, value); }
    private string _previewText = "";
    public string PreviewText { get => _previewText; private set => Set(ref _previewText, value); }
    public event PropertyChangedEventHandler? PropertyChanged;

    public MainWindow(string profileDirectory, Func<IStorySource>? sourceFactory = null, bool enableTray = true,
        IAppUpdateClient? updateClient = null, string? updateTargetDirectory = null)
    {
        _store = new AppStore(profileDirectory);
        _sourceFactory = sourceFactory is null ? SupportedStorySource.CreateLoggedFactory() : (_, _) => sourceFactory();
        JournalView = CreateJournalView();
        _enableTray = enableTray;
        _ownsUpdateClient = updateClient is null;
        _updateClient = updateClient ?? new UpdateClient();
        _updateTargetDirectory = Path.GetFullPath(updateTargetDirectory ?? AppContext.BaseDirectory);
        InitializeComponent();
        DataContext = this;
        Items.CollectionChanged += (_, _) => { Changed(nameof(QueueCountText)); Changed(nameof(StartLabel)); RefreshPageRecovery(); ScheduleSave(); };
        _saveTimer.Tick += async (_, _) => { _saveTimer.Stop(); await SaveSafelyAsync(); };
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        try
        {
            StateSnapshot saved = await Task.Run(_store.LoadAsync);
            _settings = saved.Settings;
            Changed(nameof(JournalLevel));
            JournalView.Refresh();
            _pageDelayText = _settings.PageDelaySeconds.ToString(CultureInfo.CurrentCulture);
            _validPageDelay = true;
            Changed(nameof(PageDelayText));
            Changed(nameof(PageDelayError));
            foreach (QueueItem item in saved.Queue) Attach(item);
            foreach (HistoryEntry entry in saved.History) History.Add(entry);
            QueueColumn.Width = new GridLength(Clamp(_settings.QueueWidth, 250, Math.Max(250, ActualWidth * .35)));
            PreviewColumn.Width = new GridLength(Clamp(_settings.PreviewWidth, 250, Math.Max(250, ActualWidth * .35)));
            foreach (string property in new[] { nameof(Format), nameof(OutputDirectory), nameof(ConfirmAdult), nameof(RefreshCache), nameof(MinimizeToTray) }) Changed(property);
            CurrentStatus = Items.Count > 0 ? "Очередь восстановлена. Загрузка начнётся после нажатия «Скачать»." : "Добавьте ссылку на рассказ Sefan или публикацию Bestweapon.";
            Log($"Text Downloader {typeof(MainWindow).Assembly.GetName().Version}; профиль: {_store.DirectoryPath}");
        }
        catch (Exception error)
        {
            CurrentStatus = "Не удалось прочитать сохранение. Исходный файл защищён от перезаписи. " + error.Message;
            Log(CurrentStatus, DownloadLogLevel.Error);
        }
        finally
        {
            _initialized = true;
            Changed(nameof(CanStart));
            Changed(nameof(CanReceiveInput)); Changed(nameof(CanCopySelectedUrl)); Changed(nameof(CanOpenUpdates));
            _ready.TrySetResult(true);
            if (_enableTray && !_closing) CreateTray();
        }
    }

    public async Task ReceiveArgumentsAsync(IEnumerable<string> args)
    {
        await Ready;
        foreach (string argument in args)
        {
            if (argument.StartsWith("http", StringComparison.OrdinalIgnoreCase)) await AddUrlsAsync(argument);
            else if (argument.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && File.Exists(argument))
                await LoadListAsync(argument);
        }
    }

    public async Task AddUrlsAsync(string raw)
    {
        await Ready;
        if (IsUpdating || _closing) { CurrentStatus = "Добавление ссылок временно недоступно во время обновления."; return; }
        if (raw.Length > 1024 * 1024) { CurrentStatus = "Список ссылок слишком велик (не более 1 МиБ)."; return; }
        var candidates = Regex.Matches(raw, "https?://[^\\s<>\\\"']+", RegexOptions.IgnoreCase).Select(m => m.Value.TrimEnd(')', ',', ';')).ToArray();
        if (candidates.Length == 0) { CurrentStatus = "Вставьте HTTPS-ссылку на рассказ Sefan или публикацию Bestweapon."; return; }
        if (Items.Count + candidates.Length > 10000) { CurrentStatus = "Очередь ограничена 10 000 ссылками."; return; }
        int added = 0, rejected = 0;
        QueueItem? first = null;
        TimeSpan pageDelay = _settings.GetPageDelay();
        using IStorySource source = _sourceFactory(pageDelay, null);
        foreach (string candidate in candidates)
        {
            try
            {
                if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri) || !source.CanHandle(uri)) { rejected++; continue; }
                string url = source.NormalizeToFirstPage(uri).AbsoluteUri;
                if (Items.Any(item => item.Url == url)) continue;
                var item = new QueueItem { Url = url };
                Attach(item);
                first ??= item;
                added++;
            }
            catch (Exception error) { rejected++; Log("Ссылка отклонена: " + error.Message, DownloadLogLevel.Warning); }
        }
        CurrentStatus = $"Добавлено: {added}." + (rejected > 0 ? $" Не поддерживаются: {rejected}. Принимаются страницы чтения sefan.ru/stories и публикации bestweapon.cc/post_номер." : "");
        Log(CurrentStatus, rejected > 0 ? DownloadLogLevel.Warning : DownloadLogLevel.Information);
        if (first is not null) SelectedItem = first;
        await SaveSafelyAsync();
    }

    private void Attach(QueueItem item)
    {
        item.PropertyChanged += (_, change) =>
        {
            Changed(nameof(QueueCountText));
            Changed(nameof(StartLabel));
            if (change.PropertyName == nameof(QueueItem.Status)) RefreshPageRecovery();
            ScheduleSave();
        };
        Items.Add(item);
    }

    private async Task PreviewSelectedAsync(QueueItem? item, bool force = false)
    {
        if (IsUpdating || _closing) return;
        _previewStop?.Cancel();
        _previewStop?.Dispose();
        _previewStop = new CancellationTokenSource();
        CancellationToken token = _previewStop.Token;
        PreviewTitle = item?.DisplayTitle ?? "Выберите рассказ";
        PreviewMetadata = item is null ? "Sefan / Bestweapon · EPUB / FB2" : SourceName(item.Url) + " · EPUB / FB2";
        PreviewText = "";
        PreviewStatus = item is null ? "" : "Получаем сведения…";
        if (item is null) return;
        if (!force && item.Status == QueueStatus.Failed && item.Checkpoint?.PendingPageUrl is not null)
        {
            PreviewStatus = item.Message;
            return;
        }
        bool confirmAdult = ConfirmAdult;
        TimeSpan pageDelay = _settings.GetPageDelay();
        var log = SourceJournal(item, "Предпросмотр · ");
        Log("Запрос предпросмотра.", DownloadLogLevel.Detail, item);
        try
        {
            StoryPage page = await Task.Run(async () =>
            {
                using IStorySource source = _sourceFactory(pageDelay, log);
                var downloader = new StoryDownloader(source, _store.CacheDirectory);
                return await downloader.PreviewAsync(new Uri(item.Url), confirmAdult, token);
            }, token);
            if (token.IsCancellationRequested || SelectedItem != item) return;
            item.Title = page.Title;
            PreviewTitle = page.Title;
            string count = IsBestweapon(item.Url)
                ? page.TotalPages is > 1 ? $" · всего в цикле: {page.TotalPages}" : page.TotalPages == 1 ? " · одна публикация" : " · части цикла определятся при загрузке"
                : page.TotalPages is > 0 ? $" · страниц: {page.TotalPages}" : " · число страниц определится при загрузке";
            PreviewMetadata = SourceName(page.Url.AbsoluteUri) + " · " + (string.IsNullOrWhiteSpace(page.Author) ? "Автор не указан" : page.Author) +
                count;
            PreviewText = page.Fragment.Replace("\r\n", "\n").Trim();
            if (PreviewText.Length > 3000) PreviewText = PreviewText[..3000] + "…";
            PreviewStatus = IsBestweapon(item.Url)
                ? "Выбранная публикация и предыдущие части цикла будут собраны в одну книгу."
                : page.Contents is { IndexUrl: not null } or { Entries.Count: > 1 }
                    ? "Связанные части из оглавления будут собраны в одну книгу."
                    : "Начало рассказа. Файлы появятся после завершения загрузки.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (SelectedItem != item || token.IsCancellationRequested) return;
            PreviewStatus = error.Message;
            Log("Предпросмотр: " + error.Message, DownloadLogLevel.Warning, item);
        }
    }

    public async Task StartAsync()
    {
        await Ready;
        if (!CanStart) return;
        QueueItem[] pending = Items.Where(item => item.Status is QueueStatus.Queued or QueueStatus.Paused).ToArray();
        if (pending.Length == 0) { CurrentStatus = "Нет ожидающих заданий. Для ошибок нажмите «Повторить ошибки»."; return; }
        if (string.IsNullOrWhiteSpace(OutputDirectory) || !Path.IsPathFullyQualified(OutputDirectory))
        { CurrentStatus = "Укажите полный путь к папке книг."; return; }
        _stopReason = StopReason.None;
        _sessionStop?.Dispose();
        _sessionStop = new CancellationTokenSource();
        IsBusy = true;
        _sessionTask = RunSessionAsync(pending, Path.GetFullPath(OutputDirectory), Format, ConfirmAdult, RefreshCache, _settings.GetPageDelay(), _sessionStop.Token);
        await _sessionTask;
        CurrentUpdateWindow?.RefreshInstallEligibility();
    }

    private async Task RunSessionAsync(QueueItem[] pending, string directory, BookFormat format, bool confirmAdult, bool refresh, TimeSpan pageDelay, CancellationToken token)
    {
        string? sessionError = null;
        Log($"Очередь запущена. Заданий: {pending.Length}. Формат: {format}. Интервал запросов: {pageDelay.TotalSeconds:0.#######} с.");
        try
        {
            await SaveOrThrowAsync();
            foreach (QueueItem item in pending)
            {
                if (token.IsCancellationRequested) break;
                if (!Items.Contains(item)) continue;
                ActiveItem = item;
                if (refresh && !item.ResumeDownload && item.Status != QueueStatus.Paused)
                {
                    item.CacheRunId = Guid.NewGuid();
                    item.Checkpoint = null;
                    item.ReplacementPageUrl = null;
                }
                var recovery = new DownloadRecovery(item.Checkpoint, item.ReplacementPageUrl);
                item.ResumeDownload = true;
                string cache = item.CacheRunId is { } runId
                    ? Path.Combine(_store.CacheDirectory, "refresh", runId.ToString("N"))
                    : _store.CacheDirectory;
                item.Status = QueueStatus.Running;
                item.Message = "Получаем текст…";
                CurrentStatus = item.DisplayTitle;
                Log((recovery.Checkpoint is null ? "Начинаем загрузку" : "Продолжаем загрузку") + $" · {item.Url}", item: item);
                var log = SourceJournal(item);
                var progress = new Progress<DownloadProgress>(p =>
                {
                    if (item.Status != QueueStatus.Running) return;
                    item.CompletedPages = p.CompletedPages;
                    item.TotalPages = p.TotalPages;
                    item.Message = p.Message;
                    Log(p.Message, p.LogLevel, item);
                    if (p.Checkpoint is not null) item.Checkpoint = p.Checkpoint;
                    CurrentStatus = $"{item.DisplayTitle}: {CountLabel(item.Url)}: {p.CompletedPages}/{p.TotalPages?.ToString() ?? "?"} · {p.Message}";
                    UpdateTray();
                });
                try
                {
                    await SaveOrThrowAsync();
                    var result = await Task.Run(async () =>
                    {
                        using IStorySource source = _sourceFactory(pageDelay, log);
                        var downloader = new StoryDownloader(source, cache);
                        BookDocument book = await downloader.DownloadAsync(new Uri(item.Url), confirmAdult, false, progress, token, recovery);
                        token.ThrowIfCancellationRequested();
                        log.Report(new(DownloadLogLevel.Information, $"Записываем книгу в формате {format}."));
                        IReadOnlyList<string> files = await BookExporter.ExportAsync(book, directory, format, token);
                        return (Book: book, Files: files);
                    }, token);
                    // Export owns its cancellation/publication boundary. Once it returns,
                    // commit this successful result even if cancellation arrives now.
                    item.Title = result.Book.Title;
                    item.Files = result.Files.ToList();
                    item.CompletedPages = result.Book.PageCount;
                    item.TotalPages = result.Book.PageCount;
                    item.Message = $"Готово: {result.Files.Count} файл(а).";
                    var entry = new HistoryEntry { Title = result.Book.Title, Url = item.Url, CompletedAt = DateTimeOffset.UtcNow, Pages = result.Book.PageCount, Files = result.Files.ToList() };
                    History.Insert(0, entry);
                    item.ResumeDownload = false;
                    item.Checkpoint = null;
                    item.ReplacementPageUrl = null;
                    item.Status = QueueStatus.Completed;
                    try { await SaveOrThrowAsync(); }
                    catch
                    {
                        History.Remove(entry);
                        item.Status = QueueStatus.Failed;
                        item.Message = "Книга сохранена, но историю записать не удалось. " + string.Join("; ", item.Files);
                        throw;
                    }
                    Log($"Готово. {CountLabel(item.Url)}: {result.Book.PageCount}. " + string.Join(", ", result.Files), item: item);
                    if (SelectedItem == item)
                    {
                        // A slow network preview must not replace the completed
                        // book after a fast export from the page cache.
                        _previewStop?.Cancel();
                        PreviewTitle = result.Book.Title;
                        PreviewMetadata = SourceName(result.Book.SourceUrl.AbsoluteUri) + " · " + (result.Book.Author ?? "Автор не указан") + $" · {CountLabel(item.Url)}: {result.Book.PageCount}";
                        PreviewText = string.Join("\n\n", result.Book.Paragraphs.Take(12));
                        if (PreviewText.Length > 5000) PreviewText = PreviewText[..5000] + "…";
                        PreviewStatus = "Книга готова. Нажмите «Открыть книгу».";
                    }
                }
                catch (OperationCanceledException)
                {
                    item.Status = _stopReason == StopReason.Pause ? QueueStatus.Paused : QueueStatus.Cancelled;
                    item.Message = item.Status == QueueStatus.Paused ? "Пауза. Продолжение использует загруженные фрагменты." : "Загрузка отменена. Фрагменты сохранены в кеше.";
                    Log(item.Message, item: item);
                    break;
                }
                catch (Exception error)
                {
                    if (!item.Message.StartsWith("Книга сохранена", StringComparison.Ordinal)) item.Message = error.Message;
                    item.Status = QueueStatus.Failed;
                    CurrentStatus = item.DisplayTitle + ": " + item.Message;
                    Log(error.Message, DownloadLogLevel.Error, item);
                    if (_saveFailed) break;
                }
            }
        }
        catch (Exception error)
        {
            sessionError = "Очередь остановлена: " + error.Message;
            CurrentStatus = sessionError;
            Log(CurrentStatus, DownloadLogLevel.Error);
        }
        finally
        {
            IsBusy = false;
            ActiveItem = null;
            CurrentStatus = sessionError ?? (_stopReason == StopReason.Pause ? "Пауза. Нажмите «Продолжить очередь»." :
                _stopReason == StopReason.Cancel ? "Загрузка отменена." : _saveFailed ? "Ошибка сохранения состояния. Проверьте журнал и права на папку профиля." :
                $"Очередь завершена. Готово: {Items.Count(q => q.Status == QueueStatus.Completed)}, ошибок: {Items.Count(q => q.Status == QueueStatus.Failed)}.");
            if (sessionError is null) Log(CurrentStatus);
            await SaveSafelyAsync();
            UpdateTray();
        }
    }

    public void Pause()
    {
        if (!IsBusy) return;
        _stopReason = StopReason.Pause;
        _sessionStop?.Cancel();
        CurrentStatus = "Останавливаем текущий запрос для паузы…";
    }
    public void Cancel()
    {
        if (!IsBusy) return;
        _stopReason = StopReason.Cancel;
        _sessionStop?.Cancel();
        CurrentStatus = "Отменяем текущую загрузку…";
    }

    private async Task SaveOrThrowAsync()
    {
        if (!_initialized || !_store.CanWrite) return;
        _saveTimer.Stop();
        _settings.QueueWidth = QueueColumn.ActualWidth;
        _settings.PreviewWidth = PreviewColumn.ActualWidth;
        try
        {
            await _store.SaveAsync(new StateSnapshot { Settings = _settings, Queue = Items.ToList(), History = History.ToList() });
            if (_saveFailed) { _saveFailed = false; Changed(nameof(CanStart)); }
        }
        catch
        {
            _saveFailed = true;
            Changed(nameof(CanStart));
            throw;
        }
    }
    private async Task SaveSafelyAsync()
    {
        try { await SaveOrThrowAsync(); }
        catch (Exception error) { CurrentStatus = "Не удалось сохранить состояние: " + error.Message; Log(CurrentStatus, DownloadLogLevel.Error); }
    }
    private void ScheduleSave() { if (_initialized && !_closing && !IsUpdating && _store.CanWrite) { _saveTimer.Stop(); _saveTimer.Start(); } }

    private static bool IsBestweapon(string url) => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) &&
        (uri.Host.Equals("bestweapon.cc", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("www.bestweapon.cc", StringComparison.OrdinalIgnoreCase));
    private static string SourceName(string url) => IsBestweapon(url) ? "Bestweapon" : "Sefan";
    private static string CountLabel(string url) => IsBestweapon(url) ? "частей" : "страниц";
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Changed(name); return true;
    }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private static double Clamp(double value, double min, double max) => double.IsFinite(value) ? Math.Clamp(value, min, max) : min;

    private async void OnAdd(object sender, RoutedEventArgs e) { await AddUrlsAsync(UrlInput.Text); UrlInput.Clear(); }
    private async void OnPaste(object sender, RoutedEventArgs e)
    {
        try { if (Clipboard.ContainsText()) { UrlInput.Text = Clipboard.GetText(); await AddUrlsAsync(UrlInput.Text); UrlInput.Clear(); } }
        catch (Exception error) { CurrentStatus = "Не удалось прочитать буфер: " + error.Message; }
    }

    private void OnCopySelectedUrl(object sender, RoutedEventArgs e)
    {
        if (!CanReceiveInput || SelectedItem is not { } item) return;
        try
        {
            Clipboard.SetText(item.Url);
            CurrentStatus = "Ссылка скопирована.";
        }
        catch (ExternalException)
        {
            CurrentStatus = "Буфер обмена занят. Попробуйте скопировать ссылку ещё раз.";
        }
    }

    private async void OnUrlKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await AddUrlsAsync(UrlInput.Text); UrlInput.Clear(); } }
    private async void OnStart(object sender, RoutedEventArgs e) => await StartAsync();
    private void OnPause(object sender, RoutedEventArgs e) => Pause();
    private void OnCancel(object sender, RoutedEventArgs e) => Cancel();
    private void OnRetry(object sender, RoutedEventArgs e)
    {
        if (IsUpdating || _closing) return;
        foreach (QueueItem item in Items.Where(q => q.Status is QueueStatus.Failed or QueueStatus.Cancelled))
        {
            item.ResumeDownload = true;
            item.Status = QueueStatus.Queued;
            item.Message = "Задание продолжится с первой незагруженной страницы.";
            Log("Задание поставлено в очередь для продолжения.", item: item);
        }
        CurrentStatus = "Нажмите «Скачать очередь», чтобы повторить задания.";
        ScheduleSave();
    }
    private void RefreshPageRecovery()
    {
        static bool CanRecover(QueueItem? item) =>
            item?.Status == QueueStatus.Failed && item.Checkpoint?.PendingPageUrl is not null;
        RecoveryItem = CanRecover(SelectedItem) ? SelectedItem : Items.LastOrDefault(CanRecover);
    }

    private void OnCopyLastPage(object sender, RoutedEventArgs e)
    {
        if (RecoveryItem?.Checkpoint?.LastSuccessfulPageUrl is not { } url) return;
        try { Clipboard.SetText(url.AbsoluteUri); }
        catch (ExternalException)
        { RecoveryError.Text = "Буфер обмена занят. Выделите адрес и нажмите Ctrl+C."; }
    }

    private async void OnUseRecoveryPage(object sender, RoutedEventArgs e)
    {
        if (!CanEditSettings || RecoveryItem is not { Checkpoint.PendingPageUrl: { } expected } item) return;
        try
        {
            if (!Uri.TryCreate(RecoveryPageInput.Text.Trim(), UriKind.Absolute, out var uri))
                throw new ArgumentException("Введите полный адрес страницы.");
            item.ReplacementPageUrl = StoryDownloader.ValidateReplacementPage(expected, uri);
        }
        catch (Exception error) when (error is ArgumentException or SefanStoryScopeMismatchException)
        {
            RecoveryError.Text = error.Message;
            return;
        }
        item.ResumeDownload = true;
        item.Status = QueueStatus.Queued;
        item.Message = "Адрес принят. Задание готово к продолжению.";
        Log($"Адрес для продолжения принят · {item.ReplacementPageUrl}", item: item);
        CurrentStatus = "Нажмите «Скачать очередь», чтобы продолжить.";
        await SaveSafelyAsync();
    }

    private void OnRepeatSelected(object sender, RoutedEventArgs e)
    {
        if (IsBusy || IsUpdating || _closing) return;
        var selected = QueueList.SelectedItems.Cast<QueueItem>().ToArray();
        if (selected.Length == 0 && SelectedItem is not null) selected = new[] { SelectedItem };
        foreach (QueueItem item in selected)
        {
            item.ResumeDownload = false;
            item.Checkpoint = null;
            item.ReplacementPageUrl = null;
            item.Status = QueueStatus.Queued;
            item.Message = "Повторная загрузка готова к запуску. Прежние книги сохранены.";
        }
        CurrentStatus = "Нажмите «Скачать очередь». Для обновлённого текста включите «Скачать заново (без кеша)».";
        ScheduleSave();
    }
    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (IsUpdating || _closing) return;
        var selected = QueueList.SelectedItems.Cast<QueueItem>().ToArray();
        if (selected.Length == 0 && SelectedItem is not null) selected = new[] { SelectedItem };
        foreach (QueueItem item in selected.Where(item => item.Status != QueueStatus.Running)) Items.Remove(item);
        if (SelectedItem is not null && !Items.Contains(SelectedItem)) SelectedItem = null;
    }
    private void OnClearCompleted(object sender, RoutedEventArgs e)
    {
        if (IsUpdating || _closing) return;
        foreach (QueueItem item in Items.Where(q => q.Status == QueueStatus.Completed).ToArray()) Items.Remove(item);
        if (SelectedItem is not null && !Items.Contains(SelectedItem)) SelectedItem = null;
    }
    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        if (!CanEditSettings) return;
        var picker = new OpenFolderDialog { Title = "Папка готовых книг", Multiselect = false };
        if (Directory.Exists(OutputDirectory)) picker.InitialDirectory = OutputDirectory;
        if (picker.ShowDialog(this) == true) OutputDirectory = picker.FolderName;
    }
    private async void OnLoadList(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "Список ссылок", Filter = "Текстовый список (*.txt)|*.txt", CheckFileExists = true };
        if (picker.ShowDialog(this) == true) await LoadListAsync(picker.FileName);
    }
    private async Task LoadListAsync(string path)
    {
        if (IsUpdating || _closing) return;
        try
        {
            if (new FileInfo(path).Length > 1024 * 1024) { CurrentStatus = "Список не должен превышать 1 МиБ."; return; }
            await AddUrlsAsync(await File.ReadAllTextAsync(path));
        }
        catch (Exception error) { CurrentStatus = "Не удалось прочитать список: " + error.Message; }
    }
    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.Text) || e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            foreach (string path in (string[])e.Data.GetData(DataFormats.FileDrop))
                if (path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) await LoadListAsync(path);
        }
        else if (e.Data.GetDataPresent(DataFormats.Text)) await AddUrlsAsync((string)e.Data.GetData(DataFormats.Text));
    }
    private void OnPreview(object sender, RoutedEventArgs e) { if (!IsUpdating && !_closing) _previewTask = PreviewSelectedAsync(SelectedItem, force: true); }
    private void OnOpenBook(object sender, RoutedEventArgs e) { if (SelectedItem is not null) OpenBook(SelectedItem.Files.FirstOrDefault()); }
    private void OnQueueDoubleClick(object sender, MouseButtonEventArgs e) { if (SelectedItem?.Status == QueueStatus.Completed) OpenBook(SelectedItem.Files.FirstOrDefault()); }
    private void OnShowBook(object sender, RoutedEventArgs e) { if (SelectedItem is not null) OpenBookFolder(SelectedItem.Files.FirstOrDefault()); }
    private void OnOpenFolder(object sender, RoutedEventArgs e) => OpenFolder(OutputDirectory);
    private void OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) { CurrentStatus = "Папка ещё не создана или недоступна."; return; }
        try { var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false }; info.ArgumentList.Add(Path.GetFullPath(path)); Process.Start(info); }
        catch (Exception error) { CurrentStatus = "Не удалось открыть папку: " + error.Message; }
    }
    private void OpenBookFolder(string? path) { if (!string.IsNullOrWhiteSpace(path)) OpenFolder(Path.GetDirectoryName(path)); }
    private void OpenBook(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) { CurrentStatus = "Файл книги отсутствует или недоступен."; return; }
        if (!new[] { ".epub", ".fb2" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) { CurrentStatus = "Можно открыть только EPUB или FB2."; return; }
        try { Process.Start(new ProcessStartInfo(Path.GetFullPath(path)) { UseShellExecute = true }); }
        catch (Exception error) { CurrentStatus = "Windows не смогла открыть книгу. Выберите приложение для EPUB/FB2. " + error.Message; }
    }
    private void OnHistory(object sender, RoutedEventArgs e)
    {
        var window = new HistoryWindow(History, OpenBook, OpenBookFolder) { Owner = this };
        window.ShowDialog();
    }
    private async void OnSaveJournal(object sender, RoutedEventArgs e)
    {
        var picker = new SaveFileDialog { Title = "Сохранить журнал", Filter = "Текст (*.txt)|*.txt", FileName = "TextDownloader-journal.txt" };
        if (picker.ShowDialog(this) != true) return;
        try { await File.WriteAllTextAsync(picker.FileName, JournalText); }
        catch (Exception error)
        {
            CurrentStatus = "Не удалось сохранить журнал: " + error.Message;
            Log(CurrentStatus, DownloadLogLevel.Error);
        }
    }
    private async void OnUpdate(object sender, RoutedEventArgs e)
    {
        if (!CanOpenUpdates) return;
        IsUpdating = true;
        _saveTimer.Stop();
        try
        {
            var window = new UpdateWindow(_updateClient, _updateTargetDirectory, AppUpdateBootstrap.ScratchRoot,
                PrepareUpdateExitAsync, UpdateInstallBlockReason) { Owner = this };
            CurrentUpdateWindow = window;
            // Let the routed command return before entering the owned modal loop.
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (!_closing) window.ShowDialog();
        }
        catch (Exception error) { CurrentStatus = "Не удалось открыть обновления: " + error.Message; Log(CurrentStatus, DownloadLogLevel.Error); }
        finally
        {
            CurrentUpdateWindow = null;
            if (PendingAppUpdate is not null && !_closing) Close();
            else if (!_closing) { IsUpdating = false; ScheduleSave(); }
        }
    }

    private string? UpdateInstallBlockReason()
    {
        if (!_initialized || _closing) return "Приложение ещё не готово к перезапуску.";
        if (IsBusy || !_sessionTask.IsCompleted) return "Дождитесь завершения загрузок или закройте это окно и поставьте очередь на паузу.";
        if (!_store.CanWrite) return "Сначала восстановите повреждённые настройки и очередь. Исходное сохранение защищено.";
        if (_saveFailed) return "Сначала устраните ошибку сохранения настроек и очереди.";
        return null;
    }

    private async Task<bool> PrepareUpdateExitAsync(PreparedAppUpdate prepared, CancellationToken token)
    {
        string? blocked = UpdateInstallBlockReason();
        if (blocked is not null) throw new InvalidOperationException(blocked);
        if (!string.Equals(Path.GetFullPath(prepared.TargetDirectory).TrimEnd(Path.DirectorySeparatorChar),
            _updateTargetDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Подготовленное обновление предназначено для другой папки приложения.");
        if (!Version.TryParse(prepared.Version, out Version? version) || version <= _updateClient.CurrentVersion)
            throw new InvalidDataException("Подготовлена неподходящая версия обновления.");
        _previewStop?.Cancel();
        await _previewTask.WaitAsync(TimeSpan.FromSeconds(30), token);
        token.ThrowIfCancellationRequested();
        blocked = UpdateInstallBlockReason();
        if (blocked is not null) throw new InvalidOperationException(blocked);
        await SaveOrThrowAsync();
        token.ThrowIfCancellationRequested();
        if (_saveFailed || !_store.CanWrite || _closing || IsBusy)
            throw new IOException("Не удалось сохранить очередь и настройки перед перезапуском.");
        PendingAppUpdate = prepared;
        return true;
    }

    internal void ReportUpdateOutcome(string message)
    {
        Log(message);
        if (_store.CanWrite && !_saveFailed) CurrentStatus = message;
    }
    private void OnSplitterChanged(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) => ScheduleSave();

    private void CreateTray()
    {
        try
        {
            string iconPath = Path.Combine(AppContext.BaseDirectory, "assets", "app.ico");
            _trayIcon = File.Exists(iconPath) ? new System.Drawing.Icon(iconPath) : (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
            _tray = new Forms.NotifyIcon { Text = "Text Downloader", Visible = true, Icon = _trayIcon };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Открыть", null, (_, _) => Dispatcher.BeginInvoke(ShowFromTray));
            menu.Items.Add("Папка книг", null, (_, _) => Dispatcher.BeginInvoke(() => OpenFolder(OutputDirectory)));
            menu.Items.Add("Выход", null, (_, _) => Dispatcher.BeginInvoke(Close));
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(ShowFromTray);
            UpdateTray();
        }
        catch (Exception error) { Log("Трей недоступен: " + error.Message, DownloadLogLevel.Warning); }
    }
    private void ShowFromTray() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void UpdateTray()
    {
        if (_tray is null) return;
        string value = $"Text Downloader | готово {Items.Count(q => q.Status == QueueStatus.Completed)} | " + (IsBusy ? ActiveItem?.PageProgress ?? "загрузка" : "ожидание");
        _tray.Text = value.Length > 63 ? value[..63] : value;
    }
    private void OnStateChanged(object? sender, EventArgs e) { if (WindowState == WindowState.Minimized && MinimizeToTray && _tray is not null) Hide(); }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        Changed(nameof(CanStart));
        Changed(nameof(CanEditSettings)); Changed(nameof(CanReceiveInput)); Changed(nameof(CanCopySelectedUrl)); Changed(nameof(CanOpenUpdates));
        _saveTimer.Stop();
        _previewStop?.Cancel();
        var updateWindow = CurrentUpdateWindow;
        updateWindow?.CancelOperation();
        updateWindow?.Close();
        if (IsBusy) { _stopReason = StopReason.Pause; _sessionStop?.Cancel(); }
        try
        {
            if (updateWindow is not null) await updateWindow.OperationTask;
            await _sessionTask;
            if (PendingAppUpdate is not null)
            {
                await _previewTask.WaitAsync(TimeSpan.FromSeconds(30));
                if (!_store.CanWrite || _saveFailed) throw new IOException("Очередь и настройки не сохранены.");
                await SaveOrThrowAsync();
            }
            else await SaveSafelyAsync();
        }
        catch (Exception error)
        {
            PendingAppUpdate?.Dispose();
            PendingAppUpdate = null;
            _closing = false;
            IsUpdating = false;
            Changed(nameof(CanStart)); Changed(nameof(CanEditSettings)); Changed(nameof(CanReceiveInput)); Changed(nameof(CanCopySelectedUrl)); Changed(nameof(CanOpenUpdates));
            CurrentStatus = "Перезапуск отменён: " + error.Message;
            Log(CurrentStatus, DownloadLogLevel.Error);
            return;
        }
        if (_ownsUpdateClient && _updateClient is IDisposable ownedClient) ownedClient.Dispose();
        _tray?.Dispose();
        _tray = null;
        _trayIcon?.Dispose();
        _trayIcon = null;
        _sessionStop?.Dispose();
        _previewStop?.Dispose();
        _allowClose = true;
        // Even a synchronous save (e.g. a protected corrupt profile) must
        // finish the first Closing event before we request the final close.
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }
}
