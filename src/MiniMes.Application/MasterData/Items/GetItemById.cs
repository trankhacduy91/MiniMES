using MediatR;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Contracts.MasterData;

namespace MiniMes.Application.MasterData.Items;

public sealed record GetItemByIdQuery(int Id) : IRequest<ItemDto>;

public sealed class GetItemByIdHandler(IAppDbContext db) : IRequestHandler<GetItemByIdQuery, ItemDto>
{
    public async Task<ItemDto> Handle(GetItemByIdQuery q, CancellationToken ct)
    {
        var item = await db.Items.AsNoTracking().SingleOrDefaultAsync(i => i.Id == q.Id, ct)
                   ?? throw new KeyNotFoundException($"Item {q.Id} not found.");
        return item.ToDto();
    }
}
