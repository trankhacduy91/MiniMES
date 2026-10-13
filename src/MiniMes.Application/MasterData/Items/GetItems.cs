using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Contracts.Common;
using MiniMes.Contracts.MasterData;
using MiniMes.Domain.MasterData.Items;

namespace MiniMes.Application.MasterData.Items;

public sealed record GetItemsQuery(string? Search, string? Type, bool? IsActive, int Page, int PageSize)
    : IRequest<PagedResult<ItemDto>>;

public sealed class GetItemsValidator : AbstractValidator<GetItemsQuery>
{
    public GetItemsValidator()
    {
        RuleFor(x => x.Type).IsEnumName(typeof(ItemType)).When(x => !string.IsNullOrEmpty(x.Type))
            .WithMessage("Type must be Raw, Semi or Finished.");
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, ItemFilter.MaxPageSize);
    }
}

public sealed class GetItemsHandler(IAppDbContext db) : IRequestHandler<GetItemsQuery, PagedResult<ItemDto>>
{
    public async Task<PagedResult<ItemDto>> Handle(GetItemsQuery q, CancellationToken ct)
    {
        var query = db.Items.AsNoTracking(); // read-only: no change tracking cost
        if (!string.IsNullOrWhiteSpace(q.Search))
            query = query.Where(i => i.Code.Contains(q.Search) || i.Name.Contains(q.Search));
        if (!string.IsNullOrEmpty(q.Type))
        {
            var type = Enum.Parse<ItemType>(q.Type);
            query = query.Where(i => i.Type == type);
        }
        if (q.IsActive is { } active)
            query = query.Where(i => i.IsActive == active);

        var total = await query.CountAsync(ct);
        var page = await query.OrderBy(i => i.Code)
            .Skip((q.Page - 1) * q.PageSize).Take(q.PageSize)
            .ToListAsync(ct);

        return new PagedResult<ItemDto>(page.Select(i => i.ToDto()).ToList(), q.Page, q.PageSize, total);
    }
}
