using System;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;

namespace TextDownloader;

public enum QueueStatusFilter { All, Current, Completed, NeedsAttention }

public partial class MainWindow
{
    public ListCollectionView QueueView { get; }

    private string _queueSearchText = "";
    public string QueueSearchText
    {
        get => _queueSearchText;
        set
        {
            if (Set(ref _queueSearchText, value)) RefreshQueueView();
        }
    }

    private QueueStatusFilter _statusFilter;
    public QueueStatusFilter StatusFilter
    {
        get => _statusFilter;
        set
        {
            if (Set(ref _statusFilter, value)) RefreshQueueView();
        }
    }

    public bool HasQueueFilters => StatusFilter != QueueStatusFilter.All || !string.IsNullOrWhiteSpace(QueueSearchText);
    public bool HasVisibleQueueItems => !QueueView.IsEmpty;
    public string QueueCountText => !HasQueueItems ? "" : HasQueueFilters
        ? $"Показано {QueueView.Count} из {Items.Count}"
        : $"Всего {Items.Count} · готово {Items.Count(item => item.Status == QueueStatus.Completed)}";

    private ListCollectionView CreateQueueView()
    {
        var view = new ListCollectionView(Items);
        view.LiveFilteringProperties.Add(nameof(QueueItem.DisplayTitle));
        view.LiveFilteringProperties.Add(nameof(QueueItem.Status));
        ((INotifyCollectionChanged)view).CollectionChanged += (_, _) =>
        {
            Changed(nameof(HasVisibleQueueItems));
            if (HasQueueFilters) Changed(nameof(QueueCountText));
        };
        return view;
    }

    private void RefreshQueueView()
    {
        string query = QueueSearchText.Trim();
        QueueStatusFilter status = StatusFilter;
        bool filtered = HasQueueFilters;
        using (QueueView.DeferRefresh())
        {
            QueueView.Filter = filtered ? value => value is QueueItem item && MatchesStatus(item.Status, status) &&
                (query.Length == 0 || item.DisplayTitle.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    item.Url.Contains(query, StringComparison.OrdinalIgnoreCase)) : null;
            QueueView.IsLiveFiltering = filtered;
        }
        Changed(nameof(HasQueueFilters));
        Changed(nameof(QueueCountText));
    }

    private static bool MatchesStatus(QueueStatus status, QueueStatusFilter filter) => filter switch
    {
        QueueStatusFilter.Current => status is QueueStatus.Queued or QueueStatus.Running or QueueStatus.Paused,
        QueueStatusFilter.Completed => status == QueueStatus.Completed,
        QueueStatusFilter.NeedsAttention => status is QueueStatus.Failed or QueueStatus.Cancelled,
        _ => true
    };

    private void OnCanFindQueue(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = CanReceiveInput && HasQueueItems;
        e.Handled = true;
    }

    private void OnFindQueue(object sender, ExecutedRoutedEventArgs e)
    {
        QueueSearchInput.Focus();
        QueueSearchInput.SelectAll();
        e.Handled = true;
    }

    private void OnResetQueueFilters(object sender, RoutedEventArgs e)
    {
        QueueSearchInput.Focus();
        _queueSearchText = "";
        _statusFilter = QueueStatusFilter.All;
        Changed(nameof(QueueSearchText));
        Changed(nameof(StatusFilter));
        RefreshQueueView();
    }
}
