using System.Net;

namespace TextDownloader.Core;

public enum SiteAuthorizationMethod { Browser, Basic, Bearer, Cookies }

/// <summary>An optional sign-in rule for one exact HTTPS host.</summary>
public sealed record SiteAuthorization
{
    public string Host { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public SiteAuthorizationMethod Method { get; init; }
    public string UserName { get; init; } = "";
    public string Secret { get; init; } = "";
    public string SignInUrl { get; init; } = "";
    public string UserField { get; init; } = "";
    public string PasswordField { get; init; } = "";
    public string[] SignInHosts { get; init; } = [];
    public string CheckUrl { get; init; } = "";
    public DateTimeOffset? CheckedAt { get; init; }
    public bool? CheckSucceeded { get; init; }

    public static string NormalizeHost(string host)
    {
        string value = host.Trim();
        if (value.Length is 0 or > 253 || value.EndsWith('.') ||
            value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || "/\\:@?#%[]".Contains(c)) ||
            !Uri.TryCreate("https://" + value + "/", UriKind.Absolute, out var uri) || uri.HostNameType != UriHostNameType.Dns)
            throw new ArgumentException("Введите домен сайта без схемы, порта и пути.");
        return uri.IdnHost.ToLowerInvariant();
    }

    public bool Matches(Uri uri) => IsHttps(uri) && uri.IdnHost.Equals(Host, StringComparison.OrdinalIgnoreCase);

    public static bool IsHttps(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0;

    public void Validate()
    {
        if (Host != NormalizeHost(Host) || !Enum.IsDefined(Method) || Secret.Length > 16384 || UserName.Length > 512 ||
            UserName.Any(char.IsControl) || SignInHosts.Length > 8 || SignInHosts.Any(host => host != NormalizeHost(host)))
            throw new ArgumentException("Правило авторизации содержит недопустимое значение.");
        ValidateUrl(CheckUrl);
        if (Method == SiteAuthorizationMethod.Browser)
        {
            ValidateUrl(SignInUrl);
            if (UserName.Length != 0 || Secret.Length != 0)
            {
                if (UserName.Length == 0 || Secret.Length == 0 || SignInUrl.Length == 0 ||
                    !ValidField(UserField) || !ValidField(PasswordField) || UserField == PasswordField)
                    throw new ArgumentException("Для подстановки пароля укажите логин, пароль, страницу входа и имена обоих полей.");
            }
        }
        else if (Method == SiteAuthorizationMethod.Basic)
        {
            if (UserName.Length == 0 || UserName.Contains(':') || Secret.Length == 0 || Secret.Any(char.IsControl))
                throw new ArgumentException("Для HTTP Basic введите логин без двоеточия и пароль.");
        }
        else if (Method == SiteAuthorizationMethod.Bearer)
        {
            if (Secret.Length == 0 || Secret.Any(c => !char.IsAsciiLetterOrDigit(c) && !"-._~+/=".Contains(c)))
                throw new ArgumentException("Введите Bearer-токен без пробелов и префикса Bearer.");
        }
        else
        {
            _ = CreateCookies();
        }
    }

    private void ValidateUrl(string value)
    {
        if (value.Length != 0 && (value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || !Matches(uri)))
            throw new ArgumentException("Адрес входа или проверки должен использовать HTTPS и совпадать с доменом правила.");
    }

    private static bool ValidField(string field) =>
        field.Length is > 0 and <= 120 && !field.Any(char.IsControl);

    internal CookieContainer CreateCookies()
    {
        var jar = new CookieContainer();
        var origin = new Uri("https://" + Host + "/");
        try
        {
            // Explicitly treat the input as a Cookie request header, never as Set-Cookie.
            string[] fields = Secret.Split(';', StringSplitOptions.TrimEntries);
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (fields.Length > 20) throw new ArgumentException();
            foreach (string field in fields)
            {
                int separator = field.IndexOf('=');
                if (separator < 1 || field.Any(char.IsControl) || !names.Add(field[..separator]))
                    throw new ArgumentException();
                jar.Add(origin, new Cookie(field[..separator], field[(separator + 1)..], "/") { Secure = true, HttpOnly = true });
            }
            return jar;
        }
        catch (Exception error) when (error is CookieException or ArgumentException)
        {
            throw new ArgumentException("Введите cookies в формате name=value; other=value.");
        }
    }

    public override string ToString() => Host;
}
