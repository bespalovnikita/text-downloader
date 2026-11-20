using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using TextDownloader.Core.Updates;

namespace TextDownloader.Updates;

public partial class UpdateWindow : Window, INotifyPropertyChanged
{
    private readonly IAppUpdateClient _client;
    private readonly string _targetDirectory, _scratchRoot;
    private readonly Func<PreparedAppUpdate, CancellationToken, Task<bool>> _requestInstall;
    private readonly Func<string?> _installBlockReason;
    private CancellationTokenSource? _operationStop;
    private Task _operationTask = Task.CompletedTask;
    private bool _isBusy, _isHandingOff, _allowClose, _closeRequested, _handedOff;
    private AppRelease? _release;
    private string _status = "Получаем сведения об обновлениях…";
    private long _completedBytes;
    private long? _totalBytes;

    public string CurrentVersion => _client.CurrentVersion.ToString(3);
    public string AvailableVersion => Release?.Version.ToString(3) ?? "—";
    public AppRelease? Release { get => _release; private set { _release = value; Changed(); Changed(nameof(AvailableVersion)); RefreshInstallEligibility(); } }
    public string Status { get => _status; private set { _status = value; Changed(); } }
    public bool IsBusy { get => _isBusy; private set { _isBusy = value; Changed(); Changed(nameof(CanCheck)); Changed(nameof(CanCancel)); Changed(nameof(IsIndeterminate)); RefreshInstallEligibility(); } }
    public bool CanCheck => !IsBusy && !_handedOff;
    public bool CanCancel => IsBusy && !_isHandingOff;
    public bool CanInstall => !IsBusy && !_handedOff && Release is not null && Release.Version > _client.CurrentVersion && _installBlockReason() is null;
    public string InstallBlockReason => Release is not null ? _installBlockReason() ?? "" : "";
    public bool IsIndeterminate => IsBusy && _totalBytes is not > 0;
    public double Percent => _totalBytes is > 0 ? Math.Min(100, 100d * _completedBytes / _totalBytes.Value) : 0;
    public string TransferText => _completedBytes <= 0 ? "" : _totalBytes is > 0
        ? $"{_completedBytes / 1048576d:F1} из {_totalBytes.Value / 1048576d:F1} МБ"
        : $"{_completedBytes / 1048576d:F1} МБ";
    public Task OperationTask => _operationTask;
    public event PropertyChangedEventHandler? PropertyChanged;

    public UpdateWindow(IAppUpdateClient client, string targetDirectory, string scratchRoot,
        Func<PreparedAppUpdate, CancellationToken, Task<bool>> requestInstall, Func<string?>? installBlockReason = null)
    {
        _client = client;
        _targetDirectory = targetDirectory;
        _scratchRoot = scratchRoot;
        _requestInstall = requestInstall;
        _installBlockReason = installBlockReason ?? (() => null);
        InitializeComponent();
        DataContext = this;
        Loaded += (_, _) => { if (!_closeRequested) _operationTask = CheckAsync(); };
        Closed += (_, _) => _operationStop?.Dispose();
    }

    public void RefreshInstallEligibility()
    {
        Changed(nameof(CanInstall));
        Changed(nameof(InstallBlockReason));
    }

    public void CancelOperation() => _operationStop?.Cancel();

    private CancellationToken BeginOperation()
    {
        _operationStop?.Dispose();
        _operationStop = new CancellationTokenSource();
        _completedBytes = 0; _totalBytes = null;
        Changed(nameof(Percent)); Changed(nameof(TransferText));
        IsBusy = true;
        return _operationStop.Token;
    }

    private async Task CheckAsync()
    {
        if (!CanCheck) return;
        var token = BeginOperation();
        Release = null;
        Status = "Проверяем обновления…";
        try
        {
            Release = await _client.CheckAsync(token);
            token.ThrowIfCancellationRequested();
            Status = Release is null ? "Установлена актуальная версия." : "Обновление готово к загрузке. После установки приложение перезапустится.";
        }
        catch (OperationCanceledException) { Status = token.IsCancellationRequested ? "Проверка отменена." : "Сервер не ответил вовремя. Повторите проверку позднее."; }
        catch (Exception error) { Status = "Не удалось проверить обновления: " + error.Message; }
        finally { IsBusy = false; }
    }

    private async Task InstallAsync()
    {
        if (!CanInstall || Release is null) return;
        AppRelease release = Release;
        var token = BeginOperation();
        PreparedAppUpdate? prepared = null;
        Status = "Скачиваем обновление…";
        try
        {
            var progress = new Progress<UpdateProgress>(value =>
            {
                if (!IsBusy || _isHandingOff || token.IsCancellationRequested) return;
                Status = value.Message;
                _completedBytes = value.CompletedBytes;
                _totalBytes = value.TotalBytes;
                Changed(nameof(Percent)); Changed(nameof(TransferText)); Changed(nameof(IsIndeterminate));
            });
            prepared = await _client.PrepareAsync(release, _targetDirectory, _scratchRoot, progress, token);
            token.ThrowIfCancellationRequested();
            _isHandingOff = true;
            Changed(nameof(CanCancel));
            Status = "Сохраняем очередь и настройки перед перезапуском…";
            if (!await _requestInstall(prepared, token))
                throw new InvalidOperationException("Не удалось подготовить перезапуск. Приложение осталось открытым.");
            prepared = null; // MainWindow now owns the prepared operation until App.OnExit hands it to the helper.
            _handedOff = true;
            _allowClose = true;
            Close();
        }
        catch (OperationCanceledException) { Status = token.IsCancellationRequested ? "Обновление отменено." : "Загрузка заняла слишком много времени. Повторите попытку позднее."; }
        catch (Exception error) { Status = "Не удалось установить обновление: " + error.Message; }
        finally
        {
            prepared?.Dispose();
            _isHandingOff = false;
            IsBusy = false;
        }
    }

    private void OnCheck(object sender, RoutedEventArgs e) { if (CanCheck) _operationTask = CheckAsync(); }
    private void OnCancel(object sender, RoutedEventArgs e) => CancelOperation();
    private void OnInstall(object sender, RoutedEventArgs e) { if (CanInstall) _operationTask = InstallAsync(); }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || _handedOff || !IsBusy) return;
        e.Cancel = true;
        if (_closeRequested) return;
        _closeRequested = true;
        CancelOperation();
        try { await _operationTask; }
        finally { _allowClose = true; _ = Dispatcher.BeginInvoke(new Action(Close)); }
    }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
