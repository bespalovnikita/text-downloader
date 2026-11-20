using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TextDownloader.Core.Updates;

public sealed record AppPackageFile(string Path, long Size, string Sha256);
public sealed record AppPackageManifest(int Schema, string Application, string Version, List<AppPackageFile> Files);

/// <summary>Ownership and integrity rules for a published Text Downloader Windows package.</summary>
public static class AppUpdatePackage
{
    public const string ManifestName = "windows-package.json";
    public const long MaxPackageBytes = 1024L * 1024 * 1024;
    public const int MaxFiles = 2048;
    private const int MaxManifestBytes = 1024 * 1024;
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
    };
    private static readonly HashSet<string> NativeRuntimeFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "coreclr.dll", "hostpolicy.dll", "hostfxr.dll", "clrjit.dll", "clrgc.dll", "clrgcexp.dll",
        "clrcompression.dll", "mscordaccore.dll", "mscordbi.dll", "mscorrc.dll",
        "D3DCompiler_47_cor3.dll", "DirectWriteForwarder.dll", "PenImc_cor3.dll",
        "PresentationNative_cor3.dll", "vcruntime140_cor3.dll", "wpfgfx_cor3.dll",
        "PresentationCore.dll", "PresentationFramework.dll", "WindowsBase.dll", "ReachFramework.dll",
        "Accessibility.dll", "UIAutomationClient.dll", "UIAutomationClientSideProviders.dll",
        "UIAutomationProvider.dll", "UIAutomationTypes.dll", "HtmlAgilityPack.dll",
    };

    public static Version ParseVersion(string text)
    {
        if (string.IsNullOrEmpty(text) || !Regex.IsMatch(text, @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$",
            RegexOptions.CultureInvariant) || !Version.TryParse(text, out var version))
            throw new InvalidDataException("Недопустимая версия выпуска приложения.");
        return version;
    }

    public static string ValidateRelativePath(string relative)
    {
        if (string.IsNullOrEmpty(relative) || relative.Length > 240 || relative.StartsWith('/') || relative.StartsWith('\\'))
            throw new InvalidDataException("Недопустимый путь файла пакета.");
        string path = relative.Replace('\\', '/');
        foreach (string part in path.Split('/'))
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.Any(c => c < 32 || "<>:\"|?*".Contains(c)) ||
                Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw new InvalidDataException("Небезопасный путь файла пакета.");
        }
        bool topLevel = !path.Contains('/');
        bool allowed = path.Equals("TextDownloader.exe", StringComparison.OrdinalIgnoreCase) ||
            path.Equals(ManifestName, StringComparison.OrdinalIgnoreCase) ||
            path.Equals("README.md", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("THIRD_PARTY_NOTICES.md", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("assets/app.ico", StringComparison.OrdinalIgnoreCase) ||
            (topLevel && (
                path.Equals("TextDownloader.deps.json", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("TextDownloader.runtimeconfig.json", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("TextDownloader.pdb", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("TextDownloader.Core.pdb", StringComparison.OrdinalIgnoreCase) ||
                (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && (
                    path.StartsWith("TextDownloader.", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("System.", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ||
                    NativeRuntimeFiles.Contains(path))))) ||
            (path.StartsWith("docs/", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) ||
            (path.StartsWith("third-party/", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
        if (!allowed) throw new InvalidDataException("Файл не входит в поставку Text Downloader: " + path);
        return path;
    }

    public static string HashFile(string path)
    {
        AssertNoReparse(path);
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static AppPackageManifest ReadManifest(string path)
    {
        AssertNoReparse(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxManifestBytes) throw new InvalidDataException("Манифест пакета слишком велик.");
        byte[] bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        AppPackageManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<AppPackageManifest>(bytes, Json)
                ?? throw new InvalidDataException("Пустой манифест пакета.");
        }
        catch (JsonException error) { throw new InvalidDataException("Повреждён манифест пакета.", error); }
        ValidateManifest(manifest);
        return manifest;
    }

    public static void ValidateManifest(AppPackageManifest manifest)
    {
        if (manifest is null || manifest.Schema != 1 || manifest.Application != "TextDownloader" ||
            manifest.Files is null || manifest.Files.Count is < 1 or > MaxFiles)
            throw new InvalidDataException("Недопустимый манифест Text Downloader.");
        ParseVersion(manifest.Version);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in manifest.Files)
        {
            if (file is null) throw new InvalidDataException("Пустая запись файла в манифесте пакета.");
            string name = ValidateRelativePath(file.Path);
            if (name.Equals(ManifestName, StringComparison.OrdinalIgnoreCase) || !names.Add(name) || file.Size <= 0 ||
                file.Size > MaxPackageBytes || !Regex.IsMatch(file.Sha256 ?? "", @"^[0-9a-f]{64}$", RegexOptions.CultureInvariant))
                throw new InvalidDataException("Недопустимая или повторяющаяся запись файла в манифесте.");
            if (file.Size > MaxPackageBytes - total) throw new InvalidDataException("Пакет приложения слишком велик.");
            total += file.Size;
        }
        foreach (string path in names)
            for (int slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
                if (names.Contains(path[..slash])) throw new InvalidDataException("Путь пакета одновременно является файлом и каталогом.");
        if (!names.Contains("TextDownloader.exe")) throw new InvalidDataException("В пакете отсутствует TextDownloader.exe.");
    }

    public static void AssertNoReparse(string path)
    {
        string? current = Path.GetFullPath(path);
        bool leaf = true;
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                // File.GetAttributes also detects dangling links, which File.Exists hides.
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Путь обновления содержит ссылку или точку повторного анализа: " + current);
                if (!leaf && (attributes & FileAttributes.Directory) == 0)
                    throw new IOException("Родитель пути обновления не является каталогом: " + current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            leaf = false;
            current = Path.GetDirectoryName(current);
        }
    }

    public static string FilePath(string directory, string relative)
    {
        string name = ValidateRelativePath(relative);
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        string result = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Путь покинул каталог пакета.");
        AssertNoReparse(result);
        return result;
    }

    public static void ValidateDirectory(string directory, AppPackageManifest manifest)
    {
        ValidateManifest(manifest);
        directory = Path.GetFullPath(directory);
        AssertNoReparse(directory);
        var expected = new HashSet<string>(manifest.Files.Select(file => ValidateRelativePath(file.Path)), StringComparer.OrdinalIgnoreCase) { ManifestName };
        var allowedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in expected)
            for (int slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
                allowedDirectories.Add(path[..slash]);
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory)) CheckEntry(entry);
        void CheckEntry(string entry)
        {
            AssertNoReparse(entry);
            string relative = Path.GetRelativePath(directory, entry).Replace('\\', '/');
            if (Directory.Exists(entry))
            {
                if (!allowedDirectories.Contains(relative)) throw new InvalidDataException("Неожиданный каталог в пакете: " + relative);
                foreach (string child in Directory.EnumerateFileSystemEntries(entry)) CheckEntry(child);
                return;
            }
            if (!expected.Remove(relative)) throw new InvalidDataException("Неожиданный файл в пакете: " + relative);
        }
        if (expected.Count != 0) throw new InvalidDataException("В пакете отсутствуют обязательные файлы.");
        foreach (var file in manifest.Files)
        {
            string path = FilePath(directory, file.Path);
            if (new FileInfo(path).Length != file.Size || HashFile(path) != file.Sha256)
                throw new InvalidDataException("Не совпадает контрольная сумма файла пакета: " + file.Path);
        }
    }
}
