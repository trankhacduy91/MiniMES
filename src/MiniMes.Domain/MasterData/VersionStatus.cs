namespace MiniMes.Domain.MasterData;

// Lifecycle shared by BOM and Routing versions. Approved = locked: a change needs a new version,
// so work orders and serials keep pointing at the exact recipe they were made with.
public enum VersionStatus
{
    Draft,
    Approved,
}
