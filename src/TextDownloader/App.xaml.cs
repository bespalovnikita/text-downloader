using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using TextDownloader.Updates;

namespace TextDownloader;

public partial class App : Application
{
    private Mutex? _instance;
    private string? _profileDirectory;
    private string? _mutexName;
    private MainWindow? _window;

    internal static string InstanceMutexName(string profileDirectory)
    {
        string profile = Path.GetFullPath(profileDirectory);
        string profileId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profile.ToUpperInvariant())))[..24];
        return @"Local\TextDownloader." + profileId;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (AppUpdateBootstrap.TryRun(e.Args)) { Shutdown(); return; }
        base.OnStartup(e);
        string profile = Environment.GetEnvironmentVariable("TD_DATA_DIRECTORY") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TextDownloader");
        profile = Path.GetFullPath(profile);
        _profileDirectory = profile;
        _mutexName = InstanceMutexName(profile);
        _instance = new Mutex(false, _mutexName);
        bool first;
        try { first = _instance.WaitOne(0); }
        catch (AbandonedMutexException) { first = true; }
        if (!first)
        {
            MessageBox.Show("Text Downloader уже запущен. Откройте его окно или значок в трее.", "Text Downloader", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "Ошибка Text Downloader", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        try
        {
            if (AppUpdateBootstrap.TryStartRecovery(AppContext.BaseDirectory, _mutexName, profile))
            { Shutdown(); return; }
            var outcome = AppUpdateBootstrap.ReadResult(e.Args, AppContext.BaseDirectory);
            AppUpdateBootstrap.ScheduleCompletedCleanup();
            var window = new MainWindow(profile);
            _window = window;
            MainWindow = window;
            window.Show();
            if (e.Args.Length > 0) _ = window.ReceiveArgumentsAsync(e.Args);
            if (outcome is not null) _ = ReportOutcomeAsync(window, outcome);
        }
        catch (Exception error)
        {
            MessageBox.Show("Не удалось запустить приложение или восстановить обновление. Сохраните файлы восстановления.\n\n" + error.Message,
                "Text Downloader", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static async Task ReportOutcomeAsync(MainWindow window, AppUpdateResult outcome)
    {
        await window.Ready;
        if (!window.IsLoaded) return;
        string message = outcome.Recovered
            ? "Прерванное обновление отменено. Предыдущая версия приложения восстановлена."
            : outcome.Success
                ? $"Приложение обновлено до версии {outcome.Version}."
                : "Обновление не удалось. Предыдущая версия восстановлена. " + outcome.Error;
        window.ReportUpdateOutcome(message);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _instance?.ReleaseMutex(); } catch (ApplicationException) { }
        _instance?.Dispose();
        if (_window?.PendingAppUpdate is { } prepared && _mutexName is not null)
        {
            try
            {
                // The window has closed and its checkpoint is durable. Perform
                // the large helper copy off the dispatcher before process exit.
                using var helper = Task.Run(() => AppUpdateBootstrap.StartHelper(prepared,
                    _mutexName, profileDirectory: _profileDirectory)).GetAwaiter().GetResult();
            }
            catch (Exception error)
            {
                prepared.Dispose();
                MessageBox.Show("Не удалось начать установку обновления. Прежняя версия сохранена.\n\n" + error.Message,
                    "Text Downloader", MessageBoxButton.OK, MessageBoxImage.Warning);
                try { using var restarted = AppUpdateBootstrap.Restart(AppContext.BaseDirectory, profileDirectory: _profileDirectory); }
                catch (Exception restartError)
                { MessageBox.Show("Запустите TextDownloader.exe вручную.\n\n" + restartError.Message, "Text Downloader", MessageBoxButton.OK, MessageBoxImage.Warning); }
            }
        }
        base.OnExit(e);
    }
}
