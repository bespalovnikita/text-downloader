using System.Net;
using System.Net.Http.Headers;

namespace TextDownloader.Core;

internal static class StoryHttpAuthorization
{
    public static HttpClient CreateClient(SiteAuthorization? rule, Func<HttpClientHandler, HttpMessageHandler>? wrap = null)
    {
        if (rule is { Enabled: true }) rule.Validate();
        HttpMessageHandler transport = rule is { Enabled: true, Method: not SiteAuthorizationMethod.Browser }
            ? new ScopedTransport(rule, wrap)
            : WrapTransport(CreateTransport(), wrap);
        return new HttpClient(transport) { Timeout = TimeSpan.FromSeconds(30) };
    }

    private static HttpMessageHandler WrapTransport(HttpClientHandler transport, Func<HttpClientHandler, HttpMessageHandler>? wrap) =>
        wrap?.Invoke(transport) ?? transport;

    private static HttpClientHandler CreateTransport() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = true,
        AutomaticDecompression = DecompressionMethods.All
    };

    private sealed class ScopedTransport : HttpMessageHandler
    {
        private readonly SiteAuthorization _rule;
        private readonly HttpMessageInvoker _anonymous;
        private readonly HttpMessageInvoker _authenticated;

        public ScopedTransport(SiteAuthorization rule, Func<HttpClientHandler, HttpMessageHandler>? wrap)
        {
            _rule = rule;
            var transport = CreateTransport();
            if (rule.Method == SiteAuthorizationMethod.Basic)
                transport.Credentials = new HostCredentials(rule);
            if (rule.Method == SiteAuthorizationMethod.Cookies)
                transport.CookieContainer = rule.CreateCookies();
            _authenticated = new HttpMessageInvoker(WrapTransport(transport, wrap));
            _anonymous = new HttpMessageInvoker(WrapTransport(CreateTransport(), wrap));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri is not { } url || !_rule.Matches(url))
                return await _anonymous.SendAsync(request, token).ConfigureAwait(false);
            if (_rule.Method == SiteAuthorizationMethod.Bearer)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _rule.Secret);
            var response = await _authenticated.SendAsync(request, token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                int status = (int)response.StatusCode;
                response.Dispose();
                throw new InvalidDataException($"Сайт отклонил доступ (HTTP {status}). Проверьте правило авторизации.");
            }
            return response;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _authenticated.Dispose();
                _anonymous.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class HostCredentials(SiteAuthorization rule) : ICredentials
    {
        public NetworkCredential? GetCredential(Uri uri, string authType) =>
            rule.Matches(uri) && authType.Equals("Basic", StringComparison.OrdinalIgnoreCase)
                ? new NetworkCredential(rule.UserName, rule.Secret)
                : null;
    }
}
