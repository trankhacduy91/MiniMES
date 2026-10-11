using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using MiniMes.Domain.Common;

namespace MiniMes.Api.Middleware;

// The single place where exceptions become HTTP responses (ProblemDetails, RFC 9457).
// M1 adds ValidationException -> 400.
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var (status, title) = exception switch
        {
            DomainException => (StatusCodes.Status422UnprocessableEntity, "Business rule violated"),
            ConflictException => (StatusCodes.Status409Conflict, "Conflict"),
            KeyNotFoundException => (StatusCodes.Status404NotFound, "Not found"),
            _ => (StatusCodes.Status500InternalServerError, "Unexpected error"),
        };

        if (status == StatusCodes.Status500InternalServerError)
            logger.LogError(exception, "Unhandled exception on {Method} {Path}", context.Request.Method, context.Request.Path);

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            // Never leak internals (table names, SQL) for 500.
            Detail = status == StatusCodes.Status500InternalServerError
                ? "An unexpected error occurred."
                : exception.Message,
            Instance = context.Request.Path,
        };
        var correlationId = context.Items[CorrelationIdMiddleware.ItemKey]?.ToString() ?? context.TraceIdentifier;
        problem.Extensions["correlationId"] = correlationId;

        context.Response.StatusCode = status;
        context.Response.Headers[CorrelationIdMiddleware.HeaderName] = correlationId;
        await context.Response.WriteAsJsonAsync(problem, ct);
        return true;
    }
}
