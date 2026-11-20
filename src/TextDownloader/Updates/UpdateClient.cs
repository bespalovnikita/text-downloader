using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace TextDownloader.Updates;

/// <summary>Official release checks and checksum-verified Windows package download.</summary>
public static class UpdateClient
{
    public const string Repository = "bespalovnikita/text-downloader";
    public const string ArchiveName = "TextDownloader-Windows-x64.zip";
    public const string ReleasesUrl = "https://github.com/" + Repository + "/releases";
    private static int checking;

    public static async Task CheckAsync(Window owner, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref checking, 1) != 0) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        var token = deadline.Token;
        string? temporary = null;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"TextDownloader/{Assembly.GetExecutingAssembly().GetName().Version}");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var response = await http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", token);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                Show(owner, "Опубликованный релиз пока недоступен. Позднее повторите проверку.");
                return;
            }
            response.EnsureSuccessStatusCode();
            await using var metadata = await response.Content.ReadAsStreamAsync(token);
            using var json = await JsonDocument.ParseAsync(metadata, cancellationToken: token);
            var release = json.RootElement;
            string tag = release.GetProperty("tag_name").GetString() ?? "";
            if (!Regex.IsMatch(tag, @"^v\d+\.\d+\.\d+$") ||
                !Version.TryParse(tag[1..], out var latest))
                throw new InvalidDataException("Неподдерживаемая версия релиза.");
            var assembly = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 1, 0);
            var installed = new Version(assembly.Major, assembly.Minor, Math.Max(0, assembly.Build));
            if (latest <= installed)
            {
                Show(owner, $"Установлена актуальная версия {installed}.");
                return;
            }
            string? archiveUrl = null, checksumsUrl = null;
            foreach (var asset in release.GetProperty("assets").EnumerateArray())
            {
                string? name = asset.GetProperty("name").GetString();
                string? url = asset.GetProperty("browser_download_url").GetString();
                if (name == ArchiveName) archiveUrl = OfficialAsset(url, tag, name);
                if (name == "SHA256SUMS.txt") checksumsUrl = OfficialAsset(url, tag, name);
            }
            if (archiveUrl is null || checksumsUrl is null)
                throw new InvalidDataException("В релизе нет полного Windows-пакета и контрольной суммы.");
            if (!owner.IsLoaded || System.Windows.MessageBox.Show(owner,
                $"Доступна версия {latest}. Текущая версия: {installed}.\n\nСкачать и проверить Windows ZIP? После загрузки его можно распаковать в новую папку и запустить TextDownloader.exe.",
                "Обновление Text Downloader", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
                return;

            string checksumText;
            using (var checksumResponse = await http.GetAsync(checksumsUrl, HttpCompletionOption.ResponseHeadersRead, token))
            {
                checksumResponse.EnsureSuccessStatusCode();
                await using var input = await checksumResponse.Content.ReadAsStreamAsync(token);
                using var buffer = new MemoryStream();
                await CopyLimitedAsync(input, buffer, 64 * 1024, token);
                checksumText = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
            }
            var matches = Regex.Matches(checksumText,
                @"^([a-fA-F0-9]{64})\s+\*?" + Regex.Escape(ArchiveName) + @"\s*$", RegexOptions.Multiline);
            if (matches.Count != 1) throw new InvalidDataException("Некорректный SHA256SUMS.txt.");
            string expected = matches[0].Groups[1].Value;
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TextDownloader", "Updates", tag, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, ArchiveName + ".partial");
            using (var download = await http.GetAsync(archiveUrl, HttpCompletionOption.ResponseHeadersRead, token))
            {
                download.EnsureSuccessStatusCode();
                await using var input = await download.Content.ReadAsStreamAsync(token);
                await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                await CopyLimitedAsync(input, output, 256L * 1024 * 1024, token);
            }
            await using (var input = File.OpenRead(temporary))
            {
                string actual = Convert.ToHexString(await SHA256.HashDataAsync(input, token));
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Контрольная сумма ZIP не совпала. Файл удалён.");
            }
            using (var zip = ZipFile.OpenRead(temporary))
            {
                foreach (var entry in zip.Entries)
                {
                    string path = entry.FullName.Replace('\\', '/');
                    if (path.StartsWith('/') || path.Contains(':') || path.Split('/').Any(p => p == ".."))
                        throw new InvalidDataException("ZIP содержит небезопасный путь.");
                }
                if (zip.GetEntry("TextDownloader.exe") is null || zip.GetEntry("windows-package.json") is null)
                    throw new InvalidDataException("В ZIP отсутствуют обязательные файлы приложения.");
            }
            token.ThrowIfCancellationRequested();
            string completed = Path.Combine(directory, ArchiveName);
            File.Move(temporary, completed);
            temporary = null;
            if (owner.IsLoaded && System.Windows.MessageBox.Show(owner,
                $"Архив версии {latest} загружен, SHA-256 проверен.\n\n{completed}\n\nЗакройте приложение и распакуйте архив в новую папку. Настройки и история сохранятся. Открыть папку архива?",
                "Обновление Text Downloader", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", directory) { UseShellExecute = true });
        }
        catch (OperationCanceledException)
        {
            if (!cancellationToken.IsCancellationRequested) Show(owner, "Проверка обновления отменена по таймауту.");
        }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or InvalidOperationException or KeyNotFoundException or UnauthorizedAccessException)
        {
            Show(owner, "Не удалось получить обновление: " + error.Message);
        }
        finally
        {
            if (temporary is not null) try { File.Delete(temporary); } catch (IOException) { }
            Interlocked.Exchange(ref checking, 0);
        }
    }

    private static string OfficialAsset(string? value, string tag, string name)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.Host != "github.com" || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.AbsolutePath != $"/{Repository}/releases/download/{tag}/{name}" ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidDataException("Адрес пакета не принадлежит официальному релизу Text Downloader.");
        return uri.AbsoluteUri;
    }

    private static async Task CopyLimitedAsync(Stream input, Stream output, long limit, CancellationToken token)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token)) != 0)
        {
            total += read;
            if (total > limit) throw new InvalidDataException("Размер файла обновления превышает допустимый.");
            await output.WriteAsync(buffer.AsMemory(0, read), token);
        }
    }

    private static void Show(Window owner, string message)
    {
        if (owner.IsLoaded) System.Windows.MessageBox.Show(owner, message, "Обновление Text Downloader", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
