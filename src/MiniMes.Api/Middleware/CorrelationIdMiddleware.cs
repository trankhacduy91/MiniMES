using System.Text.RegularExpressions;
using Serilog.Context;

namespace MiniMes.Api.Middleware;

// Puts one id on every log line of a request and echoes it back so users can read it to IT.
public sealed partial class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";
    public const string ItemKey = "CorrelationId";

    // Accept the client's id only if it is short and harmless (it goes into logs).
    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex SafeId();

    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers[HeaderName].FirstOrDefault();
        var id = incoming is not null && SafeId().IsMatch(incoming) ? incoming : Guid.NewGuid().ToString("N");

        // The exception handler clears response headers, so keep a copy it can restore.
        context.Items[ItemKey] = id;
        context.Response.Headers[HeaderName] = id;
        using (LogContext.PushProperty("CorrelationId", id))
        {
            await next(context);
        }
    }
}
