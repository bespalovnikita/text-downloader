using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TextDownloader.Core.Updates;

namespace TextDownloader.Updates;

public sealed record AppRelease(Version Version, string Tag, Uri ArchiveUri, Uri ChecksumsUri, long? ArchiveBytes = null);
public sealed record UpdateProgress(string Message, long CompletedBytes = 0, long? TotalBytes = null);

public interface IAppUpdateClient
{
    Version CurrentVersion { get; }
    Task<AppRelease?> CheckAsync(CancellationToken cancellationToken);
    Task<PreparedAppUpdate> PrepareAsync(AppRelease release, string currentDirectory, string scratchRoot,
        IProgress<UpdateProgress>? progress, CancellationToken cancellationToken);
}

/// <summary>Only official stable releases; an installer receives a fully verified, prepared package.</summary>
public sealed class UpdateClient : IAppUpdateClient, IDisposable
{
    public const string Repository = "bespalovnikita/text-downloader";
    public const string ArchiveName = "TextDownloader-Windows-x64.zip";
    public const string ReleasesUrl = "https://github.com/" + Repository + "/releases";
    private const long MaxArchiveBytes = 256L * 1024 * 1024;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    public Version CurrentVersion { get; }

    public UpdateClient(HttpClient? http = null, Version? currentVersion = null)
    {
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        var assembly = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 1, 0);
        CurrentVersion = currentVersion ?? new Version(assembly.Major, assembly.Minor, Math.Max(0, assembly.Build));
    }

    public async Task<AppRelease?> CheckAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        var token = deadline.Token;
        var uri = new Uri($"https://api.github.com/repos/{Repository}/releases/latest");
        using var request = Request(uri, json: true);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        EnsureResponse(response, uri, asset: false);
        using var buffer = new MemoryStream();
        await CopyLimitedAsync(response, buffer, 1024 * 1024, null, token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        var release = document.RootElement;
        if (release.ValueKind != JsonValueKind.Object ||
            !release.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False ||
            !release.TryGetProperty("prerelease", out var prerelease) || prerelease.ValueKind != JsonValueKind.False)
            throw new InvalidDataException("Релиз не является стабильной опубликованной версией.");
        string tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!Regex.IsMatch(tag, @"^v\d+\.\d+\.\d+$", RegexOptions.CultureInvariant) ||
            !Version.TryParse(tag[1..], out Version? version))
            throw new InvalidDataException("GitHub сообщил неподдерживаемый номер версии.");
        if (version <= CurrentVersion) return null;
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("В релизе нет Windows-пакета.");
        Uri? archive = null, checksum = null;
        long? archiveBytes = null;
        foreach (var asset in assets.EnumerateArray())
        {
            string? name = asset.GetProperty("name").GetString();
            if (name is not ArchiveName and not "SHA256SUMS.txt") continue;
            Uri assetUri = OfficialAsset(asset.GetProperty("browser_download_url").GetString(), tag, name);
            if (name == ArchiveName)
            {
                if (archive is not null) throw new InvalidDataException("В релизе повторяется Windows-пакет.");
                archive = assetUri;
                if (asset.TryGetProperty("size", out var size) && size.TryGetInt64(out long count))
                {
                    if (count <= 0 || count > MaxArchiveBytes) throw new InvalidDataException("Недопустимый размер Windows-пакета.");
                    archiveBytes = count;
                }
            }
            else
            {
                if (checksum is not null) throw new InvalidDataException("В релизе повторяется файл проверки целостности.");
                checksum = assetUri;
            }
        }
        if (archive is null || checksum is null)
            throw new InvalidDataException("В релизе нет полного Windows-пакета и файла проверки целостности.");
        return new AppRelease(version, tag, archive, checksum, archiveBytes);
    }

    public async Task<PreparedAppUpdate> PrepareAsync(AppRelease release, string currentDirectory,
        string scratchRoot, IProgress<UpdateProgress>? progress, CancellationToken cancellationToken)
    {
        if (release.Version <= CurrentVersion || release.Tag != "v" + release.Version.ToString(3))
            throw new InvalidDataException("Можно установить только более новую стабильную версию.");
        OfficialAsset(release.ArchiveUri.AbsoluteUri, release.Tag, ArchiveName);
        OfficialAsset(release.ChecksumsUri.AbsoluteUri, release.Tag, "SHA256SUMS.txt");
        if (release.ArchiveBytes is <= 0 or > MaxArchiveBytes)
            throw new InvalidDataException("Недопустимый размер Windows-пакета.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        var token = deadline.Token;
        string directory = Path.Combine(Path.GetFullPath(scratchRoot), "download-" + Guid.NewGuid().ToString("N"));
        PreparedAppUpdate? prepared = null;
        bool handedOff = false;
        try
        {
            progress?.Report(new UpdateProgress("Получаем сведения об обновлении…"));
            string expectedHash;
            using (var request = Request(release.ChecksumsUri))
            using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
            {
                EnsureResponse(response, release.ChecksumsUri, asset: true);
                using var buffer = new MemoryStream();
                await CopyLimitedAsync(response, buffer, 64 * 1024, null, token).ConfigureAwait(false);
                string sums = new UTF8Encoding(false, true).GetString(buffer.ToArray()).TrimStart('\ufeff');
                var matches = Regex.Matches(sums, @"^([a-fA-F0-9]{64})[ \t]+\*?" + Regex.Escape(ArchiveName) + @"[ \t]*\r?$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
                if (matches.Count != 1) throw new InvalidDataException("Не удалось проверить целостность обновления.");
                expectedHash = matches[0].Groups[1].Value;
            }
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(directory);
            string archive = Path.Combine(directory, ArchiveName);
            using (var request = Request(release.ArchiveUri))
            using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
            {
                EnsureResponse(response, release.ArchiveUri, asset: true);
                long? length = response.Content.Headers.ContentLength ?? release.ArchiveBytes;
                if (release.ArchiveBytes is { } declared && response.Content.Headers.ContentLength is { } actual && actual != declared)
                    throw new InvalidDataException("Размер скачиваемого обновления не соответствует релизу.");
                await using var file = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                await CopyLimitedAsync(response, file, MaxArchiveBytes,
                    bytes => progress?.Report(new UpdateProgress("Скачиваем обновление…", bytes, length)), token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            if (release.ArchiveBytes is { } size && new FileInfo(archive).Length != size)
                throw new InvalidDataException("Обновление скачано не полностью.");
            progress?.Report(new UpdateProgress("Проверяем обновление…"));
            await using (var stream = File.OpenRead(archive))
            {
                string actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
                if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Проверка целостности обновления не пройдена. Повторите загрузку позднее.");
            }
            progress?.Report(new UpdateProgress("Подготавливаем установку…"));
            prepared = await Task.Run(() => AppUpdateInstaller.Prepare(archive, currentDirectory,
                scratchRoot, release.Version.ToString(3), token), token).ConfigureAwait(false);
            await Task.Run(() => ValidateExecutable(prepared.StageDirectory, release.Version), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            handedOff = true;
            return prepared;
        }
        finally
        {
            if (!handedOff) prepared?.Dispose();
            try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private HttpRequestMessage Request(Uri uri, bool json = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("TextDownloader/" + CurrentVersion.ToString(3));
        request.Headers.Accept.ParseAdd(json ? "application/vnd.github+json" : "application/octet-stream");
        return request;
    }

    private static Uri OfficialAsset(string? value, string tag, string name)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.Host != "github.com" || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.AbsolutePath != $"/{Repository}/releases/download/{tag}/{name}" ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidDataException("Адрес обновления не принадлежит официальному релизу Text Downloader.");
        return uri;
    }

    private static void EnsureResponse(HttpResponseMessage response, Uri requested, bool asset)
    {
        Uri final = response.RequestMessage?.RequestUri ?? requested;
        string[] allowed = asset
            ? new[] { "github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com", "github-releases.githubusercontent.com" }
            : new[] { "api.github.com" };
        if (final.Scheme != "https" || !final.IsDefaultPort || !string.IsNullOrEmpty(final.UserInfo) || !allowed.Contains(final.Host, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Сервер перенаправил обновление на неподдерживаемый адрес.");
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new HttpRequestException("GitHub временно ограничил число проверок. Повторите попытку позднее.");
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new HttpRequestException("Опубликованный релиз пока недоступен. Повторите проверку позднее.");
        response.EnsureSuccessStatusCode();
    }

    private static async Task CopyLimitedAsync(HttpResponseMessage response, Stream output, long limit,
        Action<long>? report, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Размер обновления превышает допустимый.");
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            total += read;
            if (total > limit) throw new InvalidDataException("Размер обновления превышает допустимый.");
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            report?.Invoke(total);
        }
    }

    private static void ValidateExecutable(string directory, Version expected)
    {
        var file = FileVersionInfo.GetVersionInfo(Path.Combine(directory, "TextDownloader.exe"));
        if (string.IsNullOrWhiteSpace(file.FileVersion) || file.FileMajorPart != expected.Major ||
            file.FileMinorPart != expected.Minor || file.FileBuildPart != expected.Build)
            throw new InvalidDataException("Версия приложения в обновлении не соответствует опубликованному релизу.");
    }

    public void Dispose() { if (_ownsHttp) _http.Dispose(); }
}
