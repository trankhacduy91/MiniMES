namespace MiniMes.Contracts.MasterData;

// BOM and Routing share the Draft -> Approved lifecycle (spec M1 1.2.6).
// EffectiveFromUtc null = now; a later time schedules the change (e.g. from next Monday's first shift).
public sealed record ApproveVersionRequest(DateTime? EffectiveFromUtc = null);

// CopyFromLatest: the new draft starts as a copy of the highest existing version (the usual "small change").
public sealed record CreateDraftRequest(int ItemId, bool CopyFromLatest = true);
