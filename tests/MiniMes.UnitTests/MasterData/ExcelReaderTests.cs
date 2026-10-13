using ClosedXML.Excel;
using MiniMes.Application.MasterData.Import;
using MiniMes.Contracts.MasterData;

namespace MiniMes.UnitTests.MasterData;

// Workbooks are built in memory with ClosedXML: no file on disk, no database.
public class ExcelReaderTests
{
    private static MemoryStream Workbook(Action<XLWorkbook> fill, bool withAllSheets = true)
    {
        using var wb = new XLWorkbook();
        if (withAllSheets)
        {
            Header(wb.AddWorksheet(ExcelMasterDataReader.ItemsSheet), ExcelMasterDataReader.ItemColumns);
            Header(wb.AddWorksheet(ExcelMasterDataReader.BomLinesSheet), ExcelMasterDataReader.BomLineColumns);
            Header(wb.AddWorksheet(ExcelMasterDataReader.RoutingStepsSheet), ExcelMasterDataReader.RoutingStepColumns);
        }
        fill(wb);
        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;
        return ms;
    }

    private static void Header(IXLWorksheet ws, string[] columns)
    {
        for (var i = 0; i < columns.Length; i++) ws.Cell(1, i + 1).Value = columns[i];
    }

    private static (MasterDataFile File, List<ImportErrorDto> Errors) Read(Action<XLWorkbook> fill, bool withAllSheets = true)
    {
        var errors = new List<ImportErrorDto>();
        return (ExcelMasterDataReader.Read(Workbook(fill, withAllSheets), errors), errors);
    }

    private static void BomRow(IXLWorksheet ws, int row, XLCellValue qtyPer)
    {
        ws.Cell(row, 1).Value = "PCBB-SMT";
        ws.Cell(row, 2).Value = "SOLDER-PASTE";
        ws.Cell(row, 3).Value = qtyPer;
        ws.Cell(row, 4).Value = 0.05;
        ws.Cell(row, 5).Value = 10;
    }

    // (d) of spec step 8b: Vietnamese decimal comma, invariant point, and a real number cell.
    [Fact]
    public void Decimal_comma_point_and_number_cell_all_read_as_0_0006()
    {
        var (file, errors) = Read(wb =>
        {
            var ws = wb.Worksheet(ExcelMasterDataReader.BomLinesSheet);
            BomRow(ws, 2, "0,0006");
            BomRow(ws, 3, "0.0006");
            BomRow(ws, 4, 0.0006);
        });

        Assert.Empty(errors);
        Assert.All(file.BomLines, l => Assert.Equal(0.0006m, l.QtyPer));
    }

    // A comma is read as the Vietnamese decimal comma, never as a thousands separator ("1,5" = 1.5, not 15).
    [Fact]
    public void Comma_is_never_a_thousands_separator()
    {
        var (file, _) = Read(wb => BomRow(wb.Worksheet(ExcelMasterDataReader.BomLinesSheet), 2, "1,5"));

        Assert.Equal(1.5m, file.BomLines.Single().QtyPer);
    }

    [Fact]
    public void Codes_are_trimmed_upper_cased_and_keep_leading_zeros()
    {
        var (file, errors) = Read(wb =>
        {
            var ws = wb.Worksheet(ExcelMasterDataReader.ItemsSheet);
            ws.Cell(2, 1).Value = "  res-0402 ";
            ws.Cell(2, 2).Value = "Resistor";
            ws.Cell(2, 3).Value = "Raw";
            ws.Cell(2, 4).Value = "pcs";
            ws.Cell(3, 1).Value = "0402";          // text cell: stays "0402", not 402
            ws.Cell(3, 3).Value = "Raw";
        });

        Assert.Empty(errors);
        Assert.Equal(["RES-0402", "0402"], file.Items.Select(i => i.Code));
    }

    [Fact]
    public void Blank_rows_in_the_middle_are_skipped_and_row_numbers_are_the_excel_ones()
    {
        var (file, _) = Read(wb =>
        {
            var ws = wb.Worksheet(ExcelMasterDataReader.ItemsSheet);
            ws.Cell(2, 1).Value = "A";
            ws.Cell(5, 1).Value = "B";
        });

        Assert.Equal([2, 5], file.Items.Select(i => i.Row));
    }

    [Theory]
    [InlineData("TRUE", true)]
    [InlineData("yes", true)]
    [InlineData("x", true)]
    [InlineData("0", false)]
    [InlineData("", false)]
    public void Yes_no_cells_accept_common_spellings(string cell, bool expected)
    {
        var (file, errors) = Read(wb =>
        {
            var ws = wb.Worksheet(ExcelMasterDataReader.ItemsSheet);
            ws.Cell(2, 1).Value = "A";
            ws.Cell(2, 5).Value = cell;
        });

        Assert.Empty(errors);
        Assert.Equal(expected, file.Items.Single().IsSerialized);
    }

    [Fact]
    public void Unreadable_cells_are_reported_with_sheet_row_and_column()
    {
        var (_, errors) = Read(wb =>
        {
            var ws = wb.Worksheet(ExcelMasterDataReader.ItemsSheet);
            ws.Cell(2, 1).Value = "A";
            ws.Cell(2, 5).Value = "maybe";
            var bom = wb.Worksheet(ExcelMasterDataReader.BomLinesSheet);
            BomRow(bom, 3, "abc");
        });

        Assert.Contains(new ImportErrorDto("Items", 2, "IsSerialized", "'maybe' is not TRUE/FALSE."), errors);
        Assert.Contains(errors, e => (e.Sheet, e.Row, e.Column) == ("BomLines", 3, "QtyPer"));
    }

    [Fact]
    public void Missing_sheet_or_column_is_one_clear_error()
    {
        var (_, errors) = Read(wb =>
        {
            var ws = wb.AddWorksheet(ExcelMasterDataReader.ItemsSheet);
            ws.Cell(1, 1).Value = "Code"; // other columns missing
        }, withAllSheets: false);

        Assert.Contains(errors, e => e.Sheet == "Items" && e.Row == 1 && e.Column.Contains("Uom"));
        Assert.Contains(errors, e => e.Sheet == "BomLines" && e.Message.Contains("missing"));
    }

    [Fact]
    public void Not_an_xlsx_file_is_reported_instead_of_crashing()
    {
        var errors = new List<ImportErrorDto>();

        ExcelMasterDataReader.Read(new MemoryStream("hello"u8.ToArray()), errors);

        Assert.Equal("File", Assert.Single(errors).Sheet);
    }

    // The template and the reader share the column lists: an untouched template reads without errors.
    [Fact]
    public void Template_reads_back_without_errors()
    {
        var errors = new List<ImportErrorDto>();

        var file = ExcelMasterDataReader.Read(new MemoryStream(ExcelMasterDataTemplate.Create()), errors);

        Assert.Empty(errors);
        Assert.Equal((1, 1, 1), (file.Items.Count, file.BomLines.Count, file.RoutingSteps.Count));
    }
}
