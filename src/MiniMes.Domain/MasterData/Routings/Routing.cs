using MiniMes.Domain.Common;

namespace MiniMes.Domain.MasterData.Routings;

// The ordered operations a product goes through. Same versioning as BOM (spec M1 1.2.6).
public class Routing : AuditableEntity, IAudited
{
    public int Id { get; private set; }
    public int ItemId { get; private set; }
    public int Version { get; private set; }
    public VersionStatus Status { get; private set; } = VersionStatus.Draft;
    public DateTime? EffectiveFrom { get; private set; }
    public DateTime? EffectiveTo { get; private set; }

    public List<RoutingStep> Steps { get; private set; } = [];

    private Routing() { }

    public static Routing CreateDraft(int itemId, int version)
    {
        if (version < 1) throw new DomainException("Routing version starts at 1.");
        return new Routing { ItemId = itemId, Version = version };
    }

    public RoutingStep AddStep(int seq, string operationCode, int workCenterId, decimal idealCycleTimeSec,
        bool isInspection, bool isMandatory, string? sopUrl = null)
    {
        EnsureDraft();
        // Duplicate Seq would leave route check (M3) unable to tell which step comes first.
        if (Steps.Any(s => s.Seq == seq))
            throw new DomainException($"Step {seq} already exists in this routing.");

        var step = new RoutingStep(seq, operationCode, workCenterId, idealCycleTimeSec, isInspection, isMandatory, sopUrl);
        Steps.Add(step);
        return step;
    }

    public void ClearSteps()
    {
        EnsureDraft();
        Steps.Clear();
    }

    // Same validity rule as Bom.Approve: previous is the open Approved version, closed where this one starts.
    public void Approve(DateTime effectiveFromUtc, Routing? previous)
    {
        EnsureDraft();
        if (Steps.Count == 0) throw new DomainException("A routing needs at least one step.");
        if (previous is not null)
        {
            if (previous.ItemId != ItemId || previous.Status != VersionStatus.Approved || previous.EffectiveTo is not null)
                throw new InvalidOperationException("previous must be the open Approved routing of the same item.");
            if (effectiveFromUtc <= previous.EffectiveFrom)
                throw new DomainException(
                    $"EffectiveFrom must be after the start of version {previous.Version} ({previous.EffectiveFrom:yyyy-MM-dd HH:mm} UTC).");
            previous.EffectiveTo = effectiveFromUtc;
        }

        Status = VersionStatus.Approved;
        EffectiveFrom = effectiveFromUtc;
    }

    private void EnsureDraft()
    {
        if (Status != VersionStatus.Draft)
            throw new DomainException($"Routing version {Version} is {Status} and locked; create a new version to change it.");
    }
}
