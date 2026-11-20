using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using TextDownloader.Core;
using Forms = System.Windows.Forms;

namespace TextDownloader;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly AppStore _store;
    private readonly Func<IStorySource> _sourceFactory;
    private readonly bool _enableTray;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<string> _journal = new();
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

    private QueueItem? _selectedItem;
    public QueueItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!Set(ref _selectedItem, value)) return;
            if (_initialized) _previewTask = PreviewSelectedAsync(value);
        }
    }
    private QueueItem? _activeItem;
    public QueueItem? ActiveItem { get => _activeItem; private set => Set(ref _activeItem, value); }
    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set { if (Set(ref _isBusy, value)) { Changed(nameof(CanStart)); Changed(nameof(CanEditSettings)); Changed(nameof(StartLabel)); } }
    }
    public bool CanStart => _initialized && !IsBusy && !_closing && _store.CanWrite && !_saveFailed;
    public bool CanEditSettings => !IsBusy && !_closing;
    public string StartLabel => Items.Any(q => q.Status == QueueStatus.Paused) ? "▶ Продолжить очередь" : "▶ Скачать очередь";
    public string QueueCountText => $"{Items.Count} · готово {Items.Count(q => q.Status == QueueStatus.Completed)}";
    public BookFormat Format { get => _settings.Format; set { _settings.Format = value; Changed(); ScheduleSave(); } }
    public string OutputDirectory { get => _settings.OutputDirectory; set { _settings.OutputDirectory = value; Changed(); ScheduleSave(); } }
    public bool ConfirmAdult { get => _settings.ConfirmAdult; set { _settings.ConfirmAdult = value; Changed(); ScheduleSave(); } }
    public bool RefreshCache { get => _settings.RefreshCache; set { _settings.RefreshCache = value; Changed(); ScheduleSave(); } }
    public bool MinimizeToTray { get => _settings.MinimizeToTray; set { _settings.MinimizeToTray = value; Changed(); ScheduleSave(); } }
    private string _currentStatus = "Добавьте ссылку на страницу чтения Sefan.";
    public string CurrentStatus { get => _currentStatus; private set => Set(ref _currentStatus, value); }
    private string _previewTitle = "Выберите рассказ";
    public string PreviewTitle { get => _previewTitle; private set => Set(ref _previewTitle, value); }
    private string _previewMetadata = "Sefan · EPUB / FB2";
    public string PreviewMetadata { get => _previewMetadata; private set => Set(ref _previewMetadata, value); }
    private string _previewStatus = "Сведения загружаются при выборе строки.";
    public string PreviewStatus { get => _previewStatus; private set => Set(ref _previewStatus, value); }
    private string _previewText = "";
    public string PreviewText { get => _previewText; private set => Set(ref _previewText, value); }
    public string JournalText => string.Join(Environment.NewLine, _journal);
    public event PropertyChangedEventHandler? PropertyChanged;

    public MainWindow(string profileDirectory, Func<IStorySource>? sourceFactory = null, bool enableTray = true)
    {
        _store = new AppStore(profileDirectory);
        _sourceFactory = sourceFactory ?? (() => new SefanSource());
        _enableTray = enableTray;
        InitializeComponent();
        DataContext = this;
        Items.CollectionChanged += (_, _) => { Changed(nameof(QueueCountText)); Changed(nameof(StartLabel)); ScheduleSave(); };
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
            foreach (QueueItem item in saved.Queue) Attach(item);
            foreach (HistoryEntry entry in saved.History) History.Add(entry);
            QueueColumn.Width = new GridLength(Clamp(_settings.QueueWidth, 250, Math.Max(250, ActualWidth * .35)));
            PreviewColumn.Width = new GridLength(Clamp(_settings.PreviewWidth, 250, Math.Max(250, ActualWidth * .35)));
            foreach (string property in new[] { nameof(Format), nameof(OutputDirectory), nameof(ConfirmAdult), nameof(RefreshCache), nameof(MinimizeToTray) }) Changed(property);
            CurrentStatus = Items.Count > 0 ? "Очередь восстановлена. Загрузка начнётся после нажатия «Скачать»." : "Добавьте ссылку на страницу чтения Sefan.";
            Log($"Text Downloader {typeof(MainWindow).Assembly.GetName().Version}; профиль: {_store.DirectoryPath}");
        }
        catch (Exception error)
        {
            CurrentStatus = "Не удалось прочитать сохранение. Исходный файл защищён от перезаписи. " + error.Message;
            Log(CurrentStatus);
        }
        finally
        {
            _initialized = true;
            Changed(nameof(CanStart));
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
        if (raw.Length > 1024 * 1024) { CurrentStatus = "Список ссылок слишком велик (не более 1 МиБ)."; return; }
        var candidates = Regex.Matches(raw, "https?://[^\\s<>\\\"']+", RegexOptions.IgnoreCase).Select(m => m.Value.TrimEnd(')', ',', ';')).ToArray();
        if (candidates.Length == 0) { CurrentStatus = "Вставьте HTTPS-ссылку на страницу рассказа."; return; }
        if (Items.Count + candidates.Length > 10000) { CurrentStatus = "Очередь ограничена 10 000 ссылками."; return; }
        int added = 0, rejected = 0;
        QueueItem? first = null;
        using IStorySource source = _sourceFactory();
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
            catch (Exception error) { rejected++; Log("Ссылка отклонена: " + error.Message); }
        }
        CurrentStatus = $"Добавлено: {added}." + (rejected > 0 ? $" Не поддерживаются: {rejected}. Нужна ссылка chitat_название_1.html." : "");
        Log(CurrentStatus);
        if (first is not null) SelectedItem = first;
        await SaveSafelyAsync();
    }

    private void Attach(QueueItem item)
    {
        item.PropertyChanged += (_, _) => { Changed(nameof(QueueCountText)); Changed(nameof(StartLabel)); ScheduleSave(); };
        Items.Add(item);
    }

    private async Task PreviewSelectedAsync(QueueItem? item)
    {
        _previewStop?.Cancel();
        _previewStop?.Dispose();
        _previewStop = new CancellationTokenSource();
        CancellationToken token = _previewStop.Token;
        PreviewTitle = item?.DisplayTitle ?? "Выберите рассказ";
        PreviewMetadata = "Sefan · EPUB / FB2";
        PreviewText = "";
        PreviewStatus = item is null ? "" : "Получаем сведения…";
        if (item is null) return;
        bool confirmAdult = ConfirmAdult;
        try
        {
            StoryPage page = await Task.Run(async () =>
            {
                using IStorySource source = _sourceFactory();
                var downloader = new StoryDownloader(source, _store.CacheDirectory);
                return await downloader.PreviewAsync(new Uri(item.Url), confirmAdult, token);
            }, token);
            if (token.IsCancellationRequested || SelectedItem != item) return;
            item.Title = page.Title;
            PreviewTitle = page.Title;
            PreviewMetadata = (string.IsNullOrWhiteSpace(page.Author) ? "Автор не указан" : page.Author) +
                (page.TotalPages is > 0 ? $" · страниц: {page.TotalPages}" : " · число страниц определится при загрузке");
            PreviewText = page.Fragment.Replace("\r\n", "\n").Trim();
            if (PreviewText.Length > 3000) PreviewText = PreviewText[..3000] + "…";
            PreviewStatus = "Начало рассказа. Файлы появятся после завершения загрузки.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (SelectedItem != item || token.IsCancellationRequested) return;
            PreviewStatus = error.Message;
            Log("Предпросмотр: " + error.Message);
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
        _sessionTask = RunSessionAsync(pending, Path.GetFullPath(OutputDirectory), Format, ConfirmAdult, RefreshCache, _sessionStop.Token);
        await _sessionTask;
    }

    private async Task RunSessionAsync(QueueItem[] pending, string directory, BookFormat format, bool confirmAdult, bool refresh, CancellationToken token)
    {
        try
        {
            await SaveOrThrowAsync();
            foreach (QueueItem item in pending)
            {
                if (token.IsCancellationRequested) break;
                if (!Items.Contains(item)) continue;
                ActiveItem = item;
                item.Status = QueueStatus.Running;
                item.Message = "Получаем страницы…";
                CurrentStatus = item.DisplayTitle;
                var progress = new Progress<DownloadProgress>(p =>
                {
                    if (item.Status != QueueStatus.Running) return;
                    item.CompletedPages = p.CompletedPages;
                    item.TotalPages = p.TotalPages;
                    item.Message = p.Message;
                    CurrentStatus = $"{item.DisplayTitle}: {p.CompletedPages}/{p.TotalPages?.ToString() ?? "?"} · {p.Message}";
                    UpdateTray();
                });
                try
                {
                    await SaveOrThrowAsync();
                    var result = await Task.Run(async () =>
                    {
                        using IStorySource source = _sourceFactory();
                        var downloader = new StoryDownloader(source, _store.CacheDirectory);
                        BookDocument book = await downloader.DownloadAsync(new Uri(item.Url), confirmAdult, refresh, progress, token);
                        token.ThrowIfCancellationRequested();
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
                    item.Status = QueueStatus.Completed;
                    try { await SaveOrThrowAsync(); }
                    catch
                    {
                        History.Remove(entry);
                        item.Status = QueueStatus.Failed;
                        item.Message = "Книга сохранена, но историю записать не удалось. " + string.Join("; ", item.Files);
                        throw;
                    }
                    Log($"Готово: {item.Title}; {result.Book.PageCount} страниц; " + string.Join("; ", result.Files));
                    if (SelectedItem == item)
                    {
                        PreviewTitle = result.Book.Title;
                        PreviewMetadata = (result.Book.Author ?? "Автор не указан") + $" · страниц: {result.Book.PageCount}";
                        PreviewText = string.Join("\n\n", result.Book.Paragraphs.Take(12));
                        if (PreviewText.Length > 5000) PreviewText = PreviewText[..5000] + "…";
                        PreviewStatus = "Книга готова. Нажмите «Открыть книгу».";
                    }
                }
                catch (OperationCanceledException)
                {
                    item.Status = _stopReason == StopReason.Pause ? QueueStatus.Paused : QueueStatus.Cancelled;
                    item.Message = item.Status == QueueStatus.Paused ? "Пауза. Продолжение использует загруженные страницы." : "Загрузка отменена. Страницы сохранены в кеше.";
                    Log(item.DisplayTitle + ": " + item.Message);
                    break;
                }
                catch (Exception error)
                {
                    item.Status = QueueStatus.Failed;
                    if (!item.Message.StartsWith("Книга сохранена", StringComparison.Ordinal)) item.Message = error.Message;
                    CurrentStatus = item.DisplayTitle + ": " + item.Message;
                    Log("Ошибка: " + item.DisplayTitle + ": " + error.Message);
                    if (_saveFailed) break;
                }
            }
        }
        catch (Exception error)
        {
            CurrentStatus = "Очередь остановлена: " + error.Message;
            Log(CurrentStatus);
        }
        finally
        {
            IsBusy = false;
            ActiveItem = null;
            CurrentStatus = _stopReason == StopReason.Pause ? "Пауза. Нажмите «Продолжить очередь»." :
                _stopReason == StopReason.Cancel ? "Загрузка отменена." : _saveFailed ? "Ошибка сохранения состояния. Проверьте журнал и права на папку профиля." :
                $"Очередь завершена. Готово: {Items.Count(q => q.Status == QueueStatus.Completed)}, ошибок: {Items.Count(q => q.Status == QueueStatus.Failed)}.";
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
        catch (Exception error) { CurrentStatus = "Не удалось сохранить состояние: " + error.Message; Log(CurrentStatus); }
    }
    private void ScheduleSave() { if (_initialized && !_closing && _store.CanWrite) { _saveTimer.Stop(); _saveTimer.Start(); } }

    private void Log(string message)
    {
        _journal.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + message);
        if (_journal.Count > 300) _journal.RemoveAt(0);
        Changed(nameof(JournalText));
    }
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
    private async void OnUrlKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await AddUrlsAsync(UrlInput.Text); UrlInput.Clear(); } }
    private async void OnStart(object sender, RoutedEventArgs e) => await StartAsync();
    private void OnPause(object sender, RoutedEventArgs e) => Pause();
    private void OnCancel(object sender, RoutedEventArgs e) => Cancel();
    private void OnRetry(object sender, RoutedEventArgs e)
    {
        foreach (QueueItem item in Items.Where(q => q.Status is QueueStatus.Failed or QueueStatus.Cancelled)) { item.Status = QueueStatus.Queued; item.Message = "Повторная загрузка готова к запуску."; }
        CurrentStatus = "Нажмите «Скачать очередь», чтобы повторить задания.";
        ScheduleSave();
    }
    private void OnRepeatSelected(object sender, RoutedEventArgs e)
    {
        if (IsBusy) return;
        var selected = QueueList.SelectedItems.Cast<QueueItem>().ToArray();
        if (selected.Length == 0 && SelectedItem is not null) selected = new[] { SelectedItem };
        foreach (QueueItem item in selected)
        {
            item.Status = QueueStatus.Queued;
            item.Message = "Повторная загрузка готова к запуску. Прежние книги сохранены.";
        }
        CurrentStatus = "Нажмите «Скачать очередь». Для обновлённого текста включите «Скачать заново (без кеша)».";
        ScheduleSave();
    }
    private void OnRemove(object sender, RoutedEventArgs e)
    {
        var selected = QueueList.SelectedItems.Cast<QueueItem>().ToArray();
        if (selected.Length == 0 && SelectedItem is not null) selected = new[] { SelectedItem };
        foreach (QueueItem item in selected.Where(item => item.Status != QueueStatus.Running)) Items.Remove(item);
        if (SelectedItem is not null && !Items.Contains(SelectedItem)) SelectedItem = null;
    }
    private void OnClearCompleted(object sender, RoutedEventArgs e)
    {
        foreach (QueueItem item in Items.Where(q => q.Status == QueueStatus.Completed).ToArray()) Items.Remove(item);
        if (SelectedItem is not null && !Items.Contains(SelectedItem)) SelectedItem = null;
    }
    private void OnBrowse(object sender, RoutedEventArgs e)
    {
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
    private void OnPreview(object sender, RoutedEventArgs e) => _previewTask = PreviewSelectedAsync(SelectedItem);
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
        catch (Exception error) { CurrentStatus = "Не удалось сохранить журнал: " + error.Message; }
    }
    private async void OnUpdate(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        try { await Updates.UpdateClient.CheckAsync(this, CancellationToken.None); }
        catch (Exception error) { CurrentStatus = "Не удалось проверить обновление: " + error.Message; Log(CurrentStatus); }
        finally { UpdateButton.IsEnabled = true; }
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
        catch (Exception error) { Log("Трей недоступен: " + error.Message); }
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
        _saveTimer.Stop();
        _previewStop?.Cancel();
        if (IsBusy) { _stopReason = StopReason.Pause; _sessionStop?.Cancel(); }
        try { await _sessionTask; await SaveSafelyAsync(); }
        finally
        {
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
}
