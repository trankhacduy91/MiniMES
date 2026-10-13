using System.Globalization;
using ClosedXML.Excel;
using MiniMes.Contracts.MasterData;

namespace MiniMes.Application.MasterData.Import;

public sealed record ItemRow(int Row, string Code, string Name, string Type, string Uom,
    bool IsSerialized, bool IsLotControlled, bool SkipIqc);

public sealed record BomLineRow(int Row, string ParentCode, string ComponentCode, decimal QtyPer, decimal ScrapRate, int OperationSeq);

public sealed record RoutingStepRow(int Row, string ItemCode, int Seq, string OperationCode, string WorkCenterCode,
    decimal IdealCycleTimeSec, bool IsInspection, bool IsMandatory, string? SopUrl);

public sealed record MasterDataFile(
    IReadOnlyList<ItemRow> Items, IReadOnlyList<BomLineRow> BomLines, IReadOnlyList<RoutingStepRow> RoutingSteps);

// Turns the 3-sheet workbook into typed rows (spec M1 2.10). It only checks what a cell can be
// (text, number, yes/no); business rules run later, in the import use case, with the normal validators.
public static class ExcelMasterDataReader
{
    public const string ItemsSheet = "Items";
    public const string BomLinesSheet = "BomLines";
    public const string RoutingStepsSheet = "RoutingSteps";
    public const int MaxRowsPerSheet = 5000;

    public static readonly string[] ItemColumns = ["Code", "Name", "Type", "Uom", "IsSerialized", "IsLotControlled", "SkipIqc"];
    public static readonly string[] BomLineColumns = ["ParentCode", "ComponentCode", "QtyPer", "ScrapRate", "OperationSeq"];
    public static readonly string[] RoutingStepColumns =
        ["ItemCode", "Seq", "OperationCode", "WorkCenterCode", "IdealCycleTimeSec", "IsInspection", "IsMandatory", "SopUrl"];

    // Columns holding codes: the template formats them as Text so Excel does not turn "0402" into 402.
    public static readonly string[] CodeColumns = ["Code", "ParentCode", "ComponentCode", "ItemCode", "OperationCode", "WorkCenterCode"];

    public static MasterDataFile Read(Stream file, List<ImportErrorDto> errors)
    {
        XLWorkbook wb;
        try
        {
            wb = new XLWorkbook(file);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            errors.Add(new("File", 0, "", "The file is not a valid .xlsx workbook."));
            return new MasterDataFile([], [], []);
        }

        using (wb)
        {
            return new MasterDataFile(
                ReadSheet(wb, ItemsSheet, ItemColumns, errors, s => s.Code() is { } code
                    ? new ItemRow(s.Row, code, s.Text("Name"), s.Text("Type"), s.Text("Uom"),
                        s.Bool("IsSerialized"), s.Bool("IsLotControlled"), s.Bool("SkipIqc"))
                    : null),
                ReadSheet(wb, BomLinesSheet, BomLineColumns, errors, s =>
                    s.Code("ParentCode") is { } parent && s.Code("ComponentCode") is { } component
                        ? new BomLineRow(s.Row, parent, component, s.Decimal("QtyPer"), s.Decimal("ScrapRate", 0m), s.Int("OperationSeq"))
                        : null),
                ReadSheet(wb, RoutingStepsSheet, RoutingStepColumns, errors, s =>
                    s.Code("ItemCode") is { } item
                        ? new RoutingStepRow(s.Row, item, s.Int("Seq"), s.Code("OperationCode") ?? "", s.Code("WorkCenterCode") ?? "",
                            s.Decimal("IdealCycleTimeSec"), s.Bool("IsInspection"), s.Bool("IsMandatory", true),
                            s.Text("SopUrl") is { Length: > 0 } url ? url : null)
                        : null));
        }
    }

    private static List<T> ReadSheet<T>(XLWorkbook wb, string sheet, string[] columns, List<ImportErrorDto> errors,
        Func<RowReader, T?> map) where T : class
    {
        if (!wb.TryGetWorksheet(sheet, out var ws))
        {
            errors.Add(new(sheet, 0, "", $"Sheet '{sheet}' is missing; download the template."));
            return [];
        }

        var header = ws.Row(1).CellsUsed()
            .GroupBy(c => c.GetString().Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Address.ColumnNumber, StringComparer.OrdinalIgnoreCase);
        var missing = columns.Where(c => !header.ContainsKey(c)).ToList();
        if (missing.Count > 0)
        {
            errors.Add(new(sheet, 1, string.Join(", ", missing), "Missing column(s) in the header row."));
            return [];
        }

        // RowsUsed skips blank rows in the middle of the sheet.
        var rows = ws.RowsUsed().Where(r => r.RowNumber() > 1).ToList();
        if (rows.Count > MaxRowsPerSheet)
        {
            errors.Add(new(sheet, 0, "", $"At most {MaxRowsPerSheet} rows per sheet; split the file."));
            return [];
        }

        var result = new List<T>();
        foreach (var r in rows)
            if (map(new RowReader(sheet, r, header, errors)) is { } row)
                result.Add(row);
        return result;
    }

    // Reads one cell at a time and records a precise error (sheet, row, column) for anything unreadable.
    private sealed class RowReader(string sheet, IXLRow row, Dictionary<string, int> header, List<ImportErrorDto> errors)
    {
        public int Row => row.RowNumber();

        private IXLCell Cell(string column) => row.Cell(header[column]);

        // GetString() returns what the cell shows, so a Text-formatted "0402" stays "0402".
        public string Text(string column) => Cell(column).GetString().Trim();

        // Codes: trimmed and upper-cased (spec 2.10 trap 3). null + error when empty.
        public string? Code(string column = "Code")
        {
            var value = Text(column).ToUpperInvariant();
            if (value.Length > 0) return value;
            Error(column, $"{column} is required.");
            return null;
        }

        // "0,0006" (Vietnamese Excel), "0.0006" and a real number cell all read as 0.0006 (spec 2.10 trap 2).
        public decimal Decimal(string column, decimal? whenEmpty = null)
        {
            var cell = Cell(column);
            if (cell.DataType == XLDataType.Number) return (decimal)cell.GetDouble();

            var text = cell.GetString().Trim();
            if (text.Length == 0 && whenEmpty is { } fallback) return fallback;
            // A lone comma is the Vietnamese decimal comma. Thousands separators are not accepted at all,
            // otherwise invariant culture would read "0,0006" as 6. No culture lookup: same result on every machine.
            if (!text.Contains('.')) text = text.Replace(',', '.');
            const NumberStyles styles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;
            if (decimal.TryParse(text, styles, CultureInfo.InvariantCulture, out var v))
                return v;
            Error(column, $"'{text}' is not a number.");
            return 0m;
        }

        public int Int(string column)
        {
            var d = Decimal(column);
            if (d == Math.Truncate(d)) return (int)d;
            Error(column, $"'{d}' must be a whole number.");
            return 0;
        }

        public bool Bool(string column, bool whenEmpty = false)
        {
            var cell = Cell(column);
            if (cell.DataType == XLDataType.Boolean) return cell.GetBoolean();
            switch (cell.GetString().Trim().ToUpperInvariant())
            {
                case "": return whenEmpty;
                case "TRUE" or "YES" or "Y" or "X" or "1": return true;
                case "FALSE" or "NO" or "N" or "0": return false;
                default:
                    Error(column, $"'{cell.GetString()}' is not TRUE/FALSE.");
                    return false;
            }
        }

        private void Error(string column, string message) => errors.Add(new(sheet, Row, column, message));
    }
}
