using System.Text.Json;
using CimsApp.Core;

namespace CimsApp.Middleware;

public class ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        try { await next(ctx); }
        catch (Exception ex) { await HandleAsync(ctx, ex, logger); }
    }

    private static async Task HandleAsync(HttpContext ctx, Exception ex, ILogger logger)
    {
        // Only intercept API routes
        if (!ctx.Request.Path.StartsWithSegments("/api"))
        { logger.LogError(ex, "Unhandled error"); throw ex; }

        ctx.Response.ContentType = "application/json";
        object body = ex switch
        {
            ValidationException ve => Build(400, "VALIDATION_ERROR", ve.Message, ve.Errors),
            AppException ae        => Build(ae.StatusCode, ae.Code, ae.Message),
            _                      => Build(500, "INTERNAL_ERROR", "An unexpected error occurred"),
        };
        if (ex is not AppException) logger.LogError(ex, "Unhandled error on {Path}", ctx.Request.Path);
        ctx.Response.StatusCode = ex is AppException a ? a.StatusCode : 500;
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(body, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    private static object Build(int status, string code, string message, object? details = null) =>
        new { success = false, error = new { code, message, details } };
}
