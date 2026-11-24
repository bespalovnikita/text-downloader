using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using TextDownloader;
using TextDownloader.Core;

internal static class SiteAuthorizationUiTests
{
    public static async Task RunAsync(string profile)
    {
        var store = new SiteAuthorizationStore(profile);
        var first = new SiteAuthorization
        {
            Host = "one.example", Method = SiteAuthorizationMethod.Basic, UserName = "first-user", Secret = "synthetic-first-password",
            CheckUrl = "https://one.example/part"
        };
        var second = new SiteAuthorization
        {
            Host = "two.example", Method = SiteAuthorizationMethod.Basic, UserName = "second-user", Secret = "synthetic-second-password"
        };
        Assert((await store.LoadAsync()).Length == 0, "A new profile should have no authorization rules.");
        await store.SaveAsync([first, second]);
        string encrypted = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Path.Combine(profile, "site-authorization.dat")));
        Assert(!encrypted.Contains(first.Secret) && !encrypted.Contains(second.Secret) && !encrypted.Contains(first.UserName),
            "Authorization values were stored unencrypted.");

        SiteAuthorization? checkedRule = null;
        var dialog = new SiteAuthorizationWindow(store, await store.LoadAsync(), null, (rule, _, _) =>
        {
            checkedRule = rule;
            return Task.CompletedTask;
        });
        dialog.Show();
        try
        {
            Assert(((PasswordBox)dialog.FindName("SecretInput")).Password.Length == 0, "The editor revealed a saved password.");
            ((CheckBox)dialog.FindName("EnabledRule")).IsChecked = false;
            Click(dialog, "CheckRule");
            Assert(checkedRule is { Enabled: true } && checkedRule.Secret == first.Secret, "Check did not use the chosen rule.");
            Click(dialog, "SaveRule");
            await UntilAsync(() => ((TextBlock)dialog.FindName("Status")).Text == "Правило сохранено.");
            var saved = await store.LoadAsync();
            Assert(saved[0] is { Enabled: false, CheckSucceeded: true } && saved[0].Secret == first.Secret &&
                saved[1].Secret == second.Secret, "Editing a rule changed unrelated credentials or lost status.");

            Click(dialog, "NewRule");
            ((TextBox)dialog.FindName("HostInput")).Text = "three.example";
            Click(dialog, "SaveRule");
            await UntilAsync(() => dialog.Rules.Any(rule => rule.Host == "three.example"));
            Click(dialog, "RemoveRule");
            await UntilAsync(() => dialog.Rules.Length == 2);
            Assert((await store.LoadAsync()).All(rule => rule.Host != "three.example"), "Removing a rule did not persist.");
        }
        finally { dialog.Close(); }
        Console.WriteLine("PASS: site rule editor, DPAPI storage, check status and independent credentials");
    }

    private static void Click(Window window, string name)
    {
        var button = (Button)window.FindName(name);
        Assert(button.IsEnabled, "A required rule control is disabled.");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static async Task UntilAsync(Func<bool> done)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!done()) await Task.Delay(25, timeout.Token);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
