namespace MiniMes.Contracts.Common;

// Client-side view of the API's ProblemDetails (RFC 9457) body. Every API error has this shape,
// so the WPF ApiClient needs a single piece of code to read errors.
public sealed record ProblemDto(
    string? Title,
    int? Status,
    string? Detail,
    string? CorrelationId,
    IReadOnlyDictionary<string, string[]>? Errors); // only for 400 validation errors
