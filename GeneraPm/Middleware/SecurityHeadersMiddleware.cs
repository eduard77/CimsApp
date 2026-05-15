namespace GeneraPm.Middleware;

/// <summary>
/// T-S19-02 / OWASP A05 fix-forward. Sets a small set of
/// hardening response headers on every request:
///
/// - <c>X-Content-Type-Options: nosniff</c> blocks MIME sniffing.
/// - <c>X-Frame-Options: DENY</c> blocks clickjacking via iframe
///   embedding (no legitimate use case for embedding Genera PM).
/// - <c>Referrer-Policy: no-referrer</c> prevents URL leakage
///   to external resources.
/// - <c>Content-Security-Policy</c> starter policy: default-src
///   self with relaxations for MudBlazor's inline styles + Blazor's
///   interop scripts. Tightening (eliminate 'unsafe-inline' once
///   styles / scripts are nonced) is v1.1 / B-119.
///
/// HSTS itself lives in <see cref="Microsoft.AspNetCore.Builder.HstsBuilderExtensions"/>
/// and is added in Program.cs via <c>app.UseHsts()</c> (production
/// only — UseHsts skips Development by design).
/// </summary>
public class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext ctx)
    {
        var h = ctx.Response.Headers;
        h["X-Content-Type-Options"] = "nosniff";
        h["X-Frame-Options"]        = "DENY";
        h["Referrer-Policy"]        = "no-referrer";
        h["Content-Security-Policy"] = string.Join("; ", new[]
        {
            "default-src 'self'",
            "connect-src 'self' wss: ws:", // SignalR (S14)
            "img-src 'self' data:",
            "style-src 'self' 'unsafe-inline'", // MudBlazor inline styles
            "script-src 'self' 'unsafe-inline'", // Blazor interop
            "font-src 'self' data:",
            "frame-ancestors 'none'",
            "base-uri 'self'",
            "form-action 'self'",
        });
        await _next(ctx);
    }
}
