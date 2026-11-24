using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using TextDownloader.Core;

namespace TextDownloader;

internal sealed class SiteAuthorizationStore(string profileDirectory)
{
    private const int MaximumFileBytes = 4 * 1024 * 1024;
    private const int MaximumRules = 200;
    private readonly string _path = Path.Combine(profileDirectory, "site-authorization.dat");

    public async Task<SiteAuthorization[]> LoadAsync()
    {
        if (!File.Exists(_path)) return [];
        await using var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, useAsync: true);
        if (file.Length is <= 0 or > MaximumFileBytes) throw new InvalidDataException("Недопустимый размер файла авторизации.");
        var encrypted = new byte[(int)file.Length];
        await file.ReadExactlyAsync(encrypted).ConfigureAwait(false);
        byte[] plaintext = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        try
        {
            var rules = JsonSerializer.Deserialize<SiteAuthorization[]>(plaintext)
                ?? throw new InvalidDataException("Файл авторизации не содержит правил.");
            Validate(rules);
            return rules;
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public async Task SaveAsync(IReadOnlyList<SiteAuthorization> rules)
    {
        Validate(rules);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(rules);
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        if (encrypted.Length > MaximumFileBytes) throw new InvalidDataException("Файл авторизации превышает 4 МиБ.");

        Directory.CreateDirectory(profileDirectory);
        string temporary = Path.Combine(profileDirectory, ".site-authorization-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, useAsync: true))
            {
                await file.WriteAsync(encrypted).ConfigureAwait(false);
                await file.FlushAsync().ConfigureAwait(false);
                file.Flush(true);
            }
            File.Move(temporary, _path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Validate(IReadOnlyList<SiteAuthorization> rules)
    {
        if (rules.Count > MaximumRules || rules.Any(rule => rule is null) ||
            rules.Select(rule => rule.Host).Distinct(StringComparer.Ordinal).Count() != rules.Count)
            throw new InvalidDataException("Список должен содержать до 200 правил с разными доменами.");
        foreach (var rule in rules) rule.Validate();
    }
}
