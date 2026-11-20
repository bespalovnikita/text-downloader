using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;

namespace TextDownloader;

public partial class App : Application
{
    private Mutex? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string profile = Environment.GetEnvironmentVariable("TD_DATA_DIRECTORY") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TextDownloader");
        profile = Path.GetFullPath(profile);
        string profileId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profile.ToUpperInvariant())))[..24];
        _instance = new Mutex(false, @"Local\TextDownloader." + profileId);
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
        var window = new MainWindow(profile);
        MainWindow = window;
        window.Show();
        if (e.Args.Length > 0)
            _ = window.ReceiveArgumentsAsync(e.Args);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _instance?.ReleaseMutex(); } catch (ApplicationException) { }
        _instance?.Dispose();
        base.OnExit(e);
    }
}
