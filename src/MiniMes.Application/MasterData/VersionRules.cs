using FluentValidation;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Domain.Common;
using MiniMes.Domain.MasterData.Items;

namespace MiniMes.Application.MasterData;

// Shared by BOM and Routing versions (same lifecycle, spec M1 1.2.6).
internal static class VersionRules
{
    // An explicit EffectiveFrom may schedule a change, never rewrite the past: work orders already
    // released under the old version must stay explainable.
    public static IRuleBuilderOptions<T, DateTime?> NotInThePast<T>(this IRuleBuilder<T, DateTime?> rule, TimeProvider time)
        => rule.Must(at => at is null || at.Value.ToUniversalTime() >= time.GetUtcNow().UtcDateTime)
            .WithMessage("EffectiveFromUtc cannot be in the past; leave it empty to approve from now.");

    // Raw materials are bought, not made: they have no BOM or routing.
    public static async Task<Item> LoadParentItemAsync(this IAppDbContext db, int itemId, string what, CancellationToken ct)
    {
        var item = await db.Items.AsNoTracking().SingleOrDefaultAsync(i => i.Id == itemId, ct)
                   ?? throw new KeyNotFoundException($"Item {itemId} not found.");
        if (!item.IsActive)
            throw new DomainException($"Item '{item.Code}' is inactive; it cannot get a new {what}.");
        if (item.Type == ItemType.Raw)
            throw new DomainException($"Item '{item.Code}' is a Raw material; only Semi/Finished items have a {what}.");
        return item;
    }
}
