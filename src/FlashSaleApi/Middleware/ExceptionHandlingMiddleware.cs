using Microsoft.AspNetCore.Mvc;
using System.Net;

namespace FlashSaleApi.Middleware;

/// <summary>
/// Global exception handling middleware.
///
/// Catches all unhandled exceptions and returns a structured JSON response
/// following RFC 7807 (Problem Details for HTTP APIs) via ASP.NET Core's
/// built-in <see cref="ProblemDetails"/> format.
///
/// This prevents accidentally leaking stack traces to external callers in
/// production while still providing useful error messages.
/// </summary>
public class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception for {Method} {Path}", context.Request.Method, context.Request.Path);
            await HandleExceptionAsync(context, ex);
        }
    }

    private static async Task HandleExceptionAsync(HttpContext context, Exception ex)
    {
        var (statusCode, title) = ex switch
        {
            KeyNotFoundException         => (HttpStatusCode.NotFound,           "Resource Not Found"),
            ArgumentException            => (HttpStatusCode.BadRequest,         "Bad Request"),
            InvalidOperationException    => (HttpStatusCode.Conflict,           "Operation Not Allowed"),
            UnauthorizedAccessException  => (HttpStatusCode.Unauthorized,       "Unauthorized"),
            _                            => (HttpStatusCode.InternalServerError,"An unexpected error occurred")
        };

        var problem = new ProblemDetails
        {
            Status = (int)statusCode,
            Title  = title,
            Detail = ex.Message,
            Instance = context.Request.Path
        };

        context.Response.ContentType = "application/problem+json";
        context.Response.StatusCode  = (int)statusCode;

        await context.Response.WriteAsJsonAsync(problem);
    }
}

/// <summary>Extension method for clean middleware registration in Program.cs.</summary>
public static class ExceptionHandlingMiddlewareExtensions
{
    public static IApplicationBuilder UseGlobalExceptionHandling(this IApplicationBuilder app)
        => app.UseMiddleware<ExceptionHandlingMiddleware>();
}
