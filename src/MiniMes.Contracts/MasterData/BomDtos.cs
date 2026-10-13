namespace MiniMes.Contracts.MasterData;

public sealed record BomSummaryDto(
    int Id, int ItemId, string ItemCode, int Version, string Status,
    DateTime? EffectiveFrom, DateTime? EffectiveTo, int LineCount);

public sealed record BomLineDto(
    int ComponentItemId, string ComponentCode, string ComponentName, string Uom,
    decimal QtyPer, decimal ScrapRate, int OperationSeq);

public sealed record BomDto(
    int Id, int ItemId, string ItemCode, int Version, string Status,
    DateTime? EffectiveFrom, DateTime? EffectiveTo, IReadOnlyList<BomLineDto> Lines);

// ScrapRate is a fraction: 2% = 0.02.
public sealed record BomLineInput(int ComponentItemId, decimal QtyPer, decimal ScrapRate, int OperationSeq);

// The full list of lines: saving replaces every line of the draft.
public sealed record SaveBomLinesRequest(IReadOnlyList<BomLineInput> Lines);
