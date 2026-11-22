using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using TextDownloader.Core;

namespace TextDownloader;

public enum QueueStatus { Queued, Running, Paused, Cancelled, Failed, Completed }

public sealed class QueueItem : INotifyPropertyChanged
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Url { get; set; } = "";
    public Guid? CacheRunId { get; set; }
    public bool ResumeDownload { get; set; }
    public Uri? ReplacementPageUrl { get; set; }
    private DownloadCheckpoint? _checkpoint;
    public DownloadCheckpoint? Checkpoint
    {
        get => _checkpoint;
        set
        {
            if (_checkpoint == value) return;
            _checkpoint = value;
            Changed();
        }
    }
    private string _title = "";
    public string Title { get => _title; set { _title = value; Changed(); Changed(nameof(DisplayTitle)); } }
    [JsonIgnore] public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? Url : Title;
    private QueueStatus _status;
    public QueueStatus Status { get => _status; set { _status = value; Changed(); Changed(nameof(StatusText)); } }
    [JsonIgnore] public string StatusText => Status switch
    {
        QueueStatus.Queued => "Ожидает", QueueStatus.Running => "Загрузка", QueueStatus.Paused => "Пауза",
        QueueStatus.Cancelled => "Отменено", QueueStatus.Failed => "Ошибка", QueueStatus.Completed => "Готово", _ => ""
    };
    private string _message = "";
    public string Message { get => _message; set { _message = value; Changed(); } }
    private int _completedPages;
    public int CompletedPages { get => _completedPages; set { _completedPages = value; Changed(); Changed(nameof(PageProgress)); Changed(nameof(Percent)); } }
    private int? _totalPages;
    public int? TotalPages { get => _totalPages; set { _totalPages = value; Changed(); Changed(nameof(PageProgress)); Changed(nameof(Percent)); } }
    public List<string> Files { get; set; } = new();
    [JsonIgnore] public string PageProgress => TotalPages is > 0 ? $"{CompletedPages} / {TotalPages}" : CompletedPages > 0 ? $"{CompletedPages} / ?" : "—";
    [JsonIgnore] public double Percent => Status == QueueStatus.Completed ? 100 : TotalPages is > 0 ? Math.Min(100, 100d * CompletedPages / TotalPages.Value) : 0;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class HistoryEntry
{
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTimeOffset CompletedAt { get; set; }
    public int Pages { get; set; }
    public List<string> Files { get; set; } = new();
    [JsonIgnore] public string LocalDate => CompletedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
}

public sealed class Settings
{
    public string OutputDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TextDownloader");
    public BookFormat Format { get; set; } = BookFormat.Both;
    public bool ConfirmAdult { get; set; }
    public bool RefreshCache { get; set; }
    public const decimal MinimumPageDelaySeconds = 0.1m;
    public const decimal MaximumPageDelaySeconds = (decimal)long.MaxValue / TimeSpan.TicksPerSecond;
    public decimal PageDelaySeconds { get; set; } = 1m;
    public TimeSpan GetPageDelay() => TimeSpan.FromTicks((long)(PageDelaySeconds * TimeSpan.TicksPerSecond));
    public DownloadLogLevel JournalLevel { get; set; } = DownloadLogLevel.Information;
    public bool MinimizeToTray { get; set; } = true;
    public double QueueWidth { get; set; } = 350;
    public double PreviewWidth { get; set; } = 350;
}

public sealed class StateSnapshot
{
    public int Version { get; set; } = 1;
    public Settings Settings { get; set; } = new();
    public List<QueueItem> Queue { get; set; } = new();
    public List<HistoryEntry> History { get; set; } = new();
}

public sealed class AppStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly SemaphoreSlim _save = new(1, 1);
    public string DirectoryPath { get; }
    public string StatePath => Path.Combine(DirectoryPath, "session.json");
    public string CacheDirectory => Path.Combine(DirectoryPath, "cache");
    public bool CanWrite { get; private set; } = true;
    public AppStore(string directory) => DirectoryPath = Path.GetFullPath(directory);

    public async Task<StateSnapshot> LoadAsync()
    {
        if (!File.Exists(StatePath)) return new StateSnapshot();
        try
        {
            var info = new FileInfo(StatePath);
            if (info.Length > 16 * 1024 * 1024) throw new InvalidDataException("Сохранённая очередь превышает 16 МиБ.");
            await using var stream = new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            var state = await JsonSerializer.DeserializeAsync<StateSnapshot>(stream, Json) ?? throw new InvalidDataException("Пустое сохранение.");
            if (state.Version != 1 || state.Settings is null || state.Queue is null || state.History is null || state.Queue.Count > 10000)
                throw new InvalidDataException("Неизвестная или повреждённая версия данных.");
            if (!Enum.IsDefined(state.Settings.Format) || string.IsNullOrWhiteSpace(state.Settings.OutputDirectory) ||
                state.Queue.Any(q => q is null || !Uri.TryCreate(q.Url, UriKind.Absolute, out _) || q.Files is null || !Enum.IsDefined(q.Status)) ||
                state.History.Any(h => h is null || h.Files is null))
                throw new InvalidDataException("Сохранение содержит недопустимые значения.");
            if (state.Settings.PageDelaySeconds is < Settings.MinimumPageDelaySeconds or > Settings.MaximumPageDelaySeconds)
                state.Settings.PageDelaySeconds = 1m;
            if (!Enum.IsDefined(state.Settings.JournalLevel))
                state.Settings.JournalLevel = DownloadLogLevel.Information;
            foreach (var item in state.Queue.Where(q => q.Status == QueueStatus.Running))
            {
                item.Status = QueueStatus.Paused;
                item.Message = "Прервано закрытием приложения. Можно продолжить из кеша.";
            }
            return state;
        }
        catch
        {
            CanWrite = false;
            throw;
        }
    }

    public async Task SaveAsync(StateSnapshot state)
    {
        if (!CanWrite) throw new IOException("Повреждённое сохранение защищено от перезаписи: " + StatePath);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(state, Json);
        if (bytes.Length > 16 * 1024 * 1024) throw new IOException("Очередь и история превышают предел сохранения 16 МиБ.");
        await _save.WaitAsync();
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            temporary = Path.Combine(DirectoryPath, ".session-" + Guid.NewGuid().ToString("N") + ".tmp");
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                await stream.WriteAsync(bytes);
                await stream.FlushAsync();
                stream.Flush(true);
            }
            File.Move(temporary, StatePath, true);
        }
        finally
        {
            if (temporary is not null) try { File.Delete(temporary); } catch (IOException) { }
            _save.Release();
        }
    }
}
