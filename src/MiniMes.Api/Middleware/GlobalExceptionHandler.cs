using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using FluentValidation;
using MiniMes.Domain.Common;

namespace MiniMes.Api.Middleware;

// The single place where exceptions become HTTP responses (ProblemDetails, RFC 9457).
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var (status, title) = exception switch
        {
            ValidationException => (StatusCodes.Status400BadRequest, "Validation failed"),
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
            Detail = status switch
            {
                StatusCodes.Status500InternalServerError => "An unexpected error occurred.",
                StatusCodes.Status400BadRequest => "One or more fields are invalid.",
                _ => exception.Message,
            },
            Instance = context.Request.Path,
        };
        var correlationId = context.Items[CorrelationIdMiddleware.ItemKey]?.ToString() ?? context.TraceIdentifier;
        problem.Extensions["correlationId"] = correlationId;
        // Same "errors" shape as ASP.NET's own model-binding 400, keyed by field so the UI can show
        // each message next to its input.
        if (exception is ValidationException validation)
            problem.Extensions["errors"] = validation.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());

        context.Response.StatusCode = status;
        context.Response.Headers[CorrelationIdMiddleware.HeaderName] = correlationId;
        // RFC 9457 media type, so clients can tell an error body from a normal JSON body.
        await context.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json", ct);
        return true;
    }
}
