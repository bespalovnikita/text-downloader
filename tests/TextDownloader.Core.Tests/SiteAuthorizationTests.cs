using System.Net;
using TextDownloader.Core;

internal static class SiteAuthorizationTests
{
    public static async Task RunAsync()
    {
        const string host = "reader.example";
        var target = new Uri("https://reader.example/part");
        var otherOrigins = new Uri[]
        {
            new("https://www.reader.example/part"), new("https://reader.example.evil.example/part"),
            new("http://reader.example/part"), new("https://reader.example:8443/part"),
            new("https://user@reader.example/part")
        };
        Equal("reader.example", SiteAuthorization.NormalizeHost(" Reader.Example "));
        Equal("xn--e1afmkfd.xn--p1ai", SiteAuthorization.NormalizeHost("пример.рф"));
        Reject(() => SiteAuthorization.NormalizeHost("reader.example:8443"));
        Reject(() => SiteAuthorization.NormalizeHost("reader.example@outside.example"));

        var rules = new SiteAuthorization[]
        {
            new() { Host = host, Method = SiteAuthorizationMethod.Basic, UserName = "sample-user", Secret = "sample-password" },
            new() { Host = host, Method = SiteAuthorizationMethod.Bearer, Secret = "sample-token" },
            new() { Host = host, Method = SiteAuthorizationMethod.Cookies, Secret = "session=sample; prefs=normal" }
        };
        foreach (var rule in rules)
        {
            var sent = new List<Request>();
            using var client = StoryHttpAuthorization.CreateClient(rule, transport => new RecordTransport(transport, sent));
            async Task FetchAsync(Uri address) { using var response = await client.GetAsync(address); }
            await FetchAsync(target);
            foreach (var origin in otherOrigins) await FetchAsync(origin);
            Equal(otherOrigins.Length + 1, sent.Count);
            foreach (var request in sent.Skip(1))
            {
                Equal<string?>(null, request.Authorization);
                Equal<string?>(null, request.BasicUser);
                Equal("", request.Cookies);
                Equal(false, request.AutoRedirect);
            }
            Equal(false, sent[0].AutoRedirect);
            switch (rule.Method)
            {
                case SiteAuthorizationMethod.Basic:
                    Equal(rule.UserName, sent[0].BasicUser);
                    Equal<string?>(null, sent[0].Authorization);
                    break;
                case SiteAuthorizationMethod.Bearer:
                    Equal("Bearer " + rule.Secret, sent[0].Authorization);
                    Equal<string?>(null, sent[0].BasicUser);
                    break;
                case SiteAuthorizationMethod.Cookies:
                    Equal(rule.Secret, sent[0].Cookies);
                    break;
            }
        }

        var disabled = new List<Request>();
        using var ordinary = StoryHttpAuthorization.CreateClient(rules[1] with { Enabled = false },
            transport => new RecordTransport(transport, disabled));
        using var result = await ordinary.GetAsync(target);
        Equal<string?>(null, disabled.Single().Authorization);

        Reject(() => (rules[1] with { Secret = "a\r\nheader: value" }).Validate());
        Reject(() => (rules[2] with { Secret = "session=one; session=two" }).Validate());
        Reject(() => (rules[0] with { UserName = "sample:wrong" }).Validate());
        var browser = new SiteAuthorization { Host = host, SignInUrl = "https://reader.example/login", UserName = "sample",
            Secret = "sample-password", UserField = "user", PasswordField = "password" };
        browser.Validate();
        Reject(() => (browser with { SignInUrl = "https://www.reader.example/login" }).Validate());
        Reject(() => (browser with { CheckUrl = "http://reader.example/part" }).Validate());
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException("Unexpected rule behavior.");
    }

    private static void Reject(Action validate)
    {
        try { validate(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("An invalid authorization rule was accepted.");
    }

    private sealed record Request(string? Authorization, string? BasicUser, string Cookies, bool AutoRedirect);

    private sealed class RecordTransport(HttpClientHandler configured, List<Request> sent) : DelegatingHandler(configured)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken cancellationToken)
        {
            Uri uri = message.RequestUri!;
            sent.Add(new Request(message.Headers.Authorization?.ToString(),
                configured.Credentials?.GetCredential(uri, "Basic")?.UserName,
                configured.CookieContainer.GetCookieHeader(uri), configured.AllowAutoRedirect));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
