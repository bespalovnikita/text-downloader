using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using TextDownloader.Core;

namespace TextDownloader;

public partial class MainWindow
{
    private const int MaxJournalEntries = 50_000;
    private readonly ObservableCollection<JournalEntry> _journal = new();
    private sealed record JournalEntry(DownloadLogLevel Level, Guid? ItemId, string Line);

    public ICollectionView JournalView { get; }
    public string JournalText => string.Join(Environment.NewLine, JournalView.Cast<JournalEntry>().Select(entry => entry.Line));

    public DownloadLogLevel JournalLevel
    {
        get => _settings.JournalLevel;
        set
        {
            if (_settings.JournalLevel == value || !Enum.IsDefined(value)) return;
            _settings.JournalLevel = value;
            Changed();
            JournalView.Refresh();
            ScheduleSave();
        }
    }

    private bool _journalSelectedOnly;
    public bool JournalSelectedOnly
    {
        get => _journalSelectedOnly;
        set
        {
            if (Set(ref _journalSelectedOnly, value)) JournalView.Refresh();
        }
    }

    private ICollectionView CreateJournalView()
    {
        var view = CollectionViewSource.GetDefaultView(_journal);
        view.Filter = value => value is JournalEntry entry && entry.Level >= JournalLevel &&
            (!JournalSelectedOnly || SelectedItem is { } selected && entry.ItemId == selected.Id);
        return view;
    }

    private void Log(string message, DownloadLogLevel level = DownloadLogLevel.Information, QueueItem? item = null)
    {
        string label = level switch
        {
            DownloadLogLevel.Detail => "ПОДРОБНО",
            DownloadLogLevel.Warning => "ПРЕДУПРЕЖДЕНИЕ",
            DownloadLogLevel.Error => "ОШИБКА",
            _ => "ИНФО",
        };
        string task = item is null ? "" : $"[{item.DisplayTitle}] ";
        string line = $"{DateTime.Now:HH:mm:ss.fff} [{label}] {task}{message}";
        if (_journal.Count == MaxJournalEntries) _journal.RemoveAt(0);
        _journal.Add(new JournalEntry(level, item?.Id, line));
    }

    private IProgress<DownloadLogEvent> SourceJournal(QueueItem item, string prefix = "") =>
        new Progress<DownloadLogEvent>(entry => Log(prefix + entry.Message, entry.Level, item));

    private void OnCanCopyJournal(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = sender is ListBox { SelectedItems.Count: > 0 };
        e.Handled = true;
    }

    private void OnCopyJournal(object sender, ExecutedRoutedEventArgs e)
    {
        if (sender is not ListBox journal) return;
        string text = string.Join(Environment.NewLine, journal.SelectedItems.Cast<JournalEntry>().Select(entry => entry.Line));
        if (text.Length == 0) return;
        try { Clipboard.SetText(text); }
        catch (ExternalException) { CurrentStatus = "Буфер обмена занят. Попробуйте скопировать журнал ещё раз."; }
        e.Handled = true;
    }
}
