using FluentValidation;
using MediatR;

namespace MiniMes.Application.Common.Behaviors;

// Runs every validator of the request before its handler, for all use cases at once.
// Failures throw FluentValidation.ValidationException -> GlobalExceptionHandler -> 400 with per-field errors.
public sealed class ValidationBehavior<TRequest, TResponse>(IEnumerable<IValidator<TRequest>> validators)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        if (validators.Any())
        {
            var context = new ValidationContext<TRequest>(request);
            var results = await Task.WhenAll(validators.Select(v => v.ValidateAsync(context, ct)));
            var failures = results.SelectMany(r => r.Errors).ToList();
            if (failures.Count > 0) throw new ValidationException(failures);
        }
        return await next(ct);
    }
}
