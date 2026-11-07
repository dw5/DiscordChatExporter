using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace DiscordChatExporter.Web.Security;

// The app holds a Discord token that can delete messages, so every API and file request must prove
// it comes from the page this process handed the key to. Defends against other local programs and,
// with host filtering, against DNS-rebinding from web pages.
public sealed class AccessGate
{
    public const string CookieName = "dce_access";
    public const string RequestHeader = "X-DCE-Request";

    private readonly byte[] _keyBytes;

    public AccessGate(string key)
    {
        Key = key;
        _keyBytes = System.Text.Encoding.UTF8.GetBytes(key);
    }

    public string Key { get; }

    public static AccessGate CreateOrFromEnvironment()
    {
        var fromEnv = Environment.GetEnvironmentVariable("DCE_ACCESS_KEY");
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return new AccessGate(fromEnv);

        var bytes = RandomNumberGenerator.GetBytes(32);
        return new AccessGate(Convert.ToHexStringLower(bytes));
    }

    public bool Matches(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate))
            return false;

        var candidateBytes = System.Text.Encoding.UTF8.GetBytes(candidate);
        return CryptographicOperations.FixedTimeEquals(candidateBytes, _keyBytes);
    }
}

public sealed class AccessGateMiddleware(RequestDelegate next, AccessGate gate)
{
    public async Task Invoke(HttpContext context)
    {
        var path = context.Request.Path;

        // The entry point consumes ?key=... , sets the cookie, and redirects to a clean URL.
        if (path == "/" && context.Request.Query.TryGetValue("key", out var key))
        {
            if (gate.Matches(key))
            {
                context.Response.Cookies.Append(
                    AccessGate.CookieName,
                    gate.Key,
                    new CookieOptions
                    {
                        HttpOnly = true,
                        SameSite = SameSiteMode.Strict,
                        Secure = context.Request.IsHttps,
                        MaxAge = TimeSpan.FromDays(30),
                    }
                );
                context.Response.Redirect("/");
                return;
            }

            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var isProtected = path.StartsWithSegments("/api") || path.StartsWithSegments("/files");

        if (isProtected)
        {
            if (!gate.Matches(context.Request.Cookies[AccessGate.CookieName]))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            // CSRF: state-changing requests must carry the custom header (not possible cross-origin
            // without a successful CORS preflight, which we never grant) and a same-origin Origin.
            var method = context.Request.Method;
            var isStateChanging =
                HttpMethods.IsPost(method)
                || HttpMethods.IsPut(method)
                || HttpMethods.IsDelete(method)
                || HttpMethods.IsPatch(method);

            if (isStateChanging && path.StartsWithSegments("/api"))
            {
                if (!context.Request.Headers.ContainsKey(AccessGate.RequestHeader))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }

                var origin = context.Request.Headers.Origin.ToString();
                if (!string.IsNullOrEmpty(origin))
                {
                    var expected = $"{context.Request.Scheme}://{context.Request.Host}";
                    if (!string.Equals(origin, expected, StringComparison.OrdinalIgnoreCase))
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return;
                    }
                }
            }
        }

        await next(context);
    }
}
