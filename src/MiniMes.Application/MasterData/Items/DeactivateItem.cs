using MediatR;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;

namespace MiniMes.Application.MasterData.Items;

// No hard delete: an inactive item disappears from pick lists for new BOMs/WOs but old BOMs, lots and
// serials still show it (spec M1 rules 13-14). Idempotent.
public sealed record DeactivateItemCommand(int Id) : IRequest;

public sealed class DeactivateItemHandler(IAppDbContext db) : IRequestHandler<DeactivateItemCommand>
{
    public async Task Handle(DeactivateItemCommand cmd, CancellationToken ct)
    {
        var item = await db.Items.SingleOrDefaultAsync(i => i.Id == cmd.Id, ct)
                   ?? throw new KeyNotFoundException($"Item {cmd.Id} not found.");

        item.SetActive(false);
        await db.SaveChangesAsync(ct);
    }
}
