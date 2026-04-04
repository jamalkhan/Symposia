namespace InboxWeb;

public sealed class RequestSecurityService
{
    private const string CsrfHeaderName = "X-Symposia-Csrf";
    private readonly InboxWebOptions _options;
    private readonly ILogger<RequestSecurityService> _logger;

    public RequestSecurityService(InboxWebOptions options, ILogger<RequestSecurityService> logger)
    {
        _options = options;
        _logger = logger;
    }

    public void SetCsrfCookie(HttpResponse response, string csrfToken)
    {
        response.Cookies.Append(_options.CsrfCookieName, csrfToken, new CookieOptions
        {
            HttpOnly = false,
            IsEssential = true,
            SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax,
            Secure = !string.IsNullOrWhiteSpace(_options.TlsCertificatePath),
            MaxAge = TimeSpan.FromDays(7)
        });
    }

    public void ClearCsrfCookie(HttpResponse response)
    {
        response.Cookies.Delete(_options.CsrfCookieName);
    }

    public bool IsValid(HttpRequest request)
    {
        if (!request.Cookies.TryGetValue(_options.CsrfCookieName, out var cookieToken) ||
            string.IsNullOrWhiteSpace(cookieToken))
        {
            _logger.LogWarning("CSRF validation failed because the cookie token was missing");
            return false;
        }

        if (!request.Headers.TryGetValue(CsrfHeaderName, out var headerToken) ||
            string.IsNullOrWhiteSpace(headerToken))
        {
            _logger.LogWarning("CSRF validation failed because the header token was missing");
            return false;
        }

        var headerValue = headerToken.ToString();
        if (!string.Equals(cookieToken, headerValue, StringComparison.Ordinal))
        {
            _logger.LogWarning("CSRF validation failed because the header token did not match the cookie token");
            return false;
        }

        return true;
    }
}
