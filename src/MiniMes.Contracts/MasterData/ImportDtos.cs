namespace MiniMes.Contracts.MasterData;

// Row is the Excel row number (1 = header), so the user can jump straight to the cell. Row 0 = whole file/sheet.
public sealed record ImportErrorDto(string Sheet, int Row, string Column, string Message);

// A rejected import is a normal business answer (200), not an exception: Committed = false and the full
// error list, so the user can fix every problem in one go (spec M1 1.4 rule 15).
public sealed record ImportResultDto(
    bool DryRun, bool Committed, int ItemCount, int BomCount, int RoutingCount, IReadOnlyList<ImportErrorDto> Errors);
