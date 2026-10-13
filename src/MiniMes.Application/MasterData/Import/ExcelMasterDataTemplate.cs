using ClosedXML.Excel;
using MediatR;

namespace MiniMes.Application.MasterData.Import;

// The template is generated from the same column lists the reader uses, so the two cannot drift apart.
public sealed record GetImportTemplateQuery : IRequest<byte[]>;

public sealed class GetImportTemplateHandler : IRequestHandler<GetImportTemplateQuery, byte[]>
{
    public Task<byte[]> Handle(GetImportTemplateQuery q, CancellationToken ct) => Task.FromResult(ExcelMasterDataTemplate.Create());
}

public static class ExcelMasterDataTemplate
{
    public static byte[] Create()
    {
        using var wb = new XLWorkbook();
        AddSheet(wb, ExcelMasterDataReader.ItemsSheet, ExcelMasterDataReader.ItemColumns,
            ["RES-1K-0402", "Resistor 1k 0402", "Raw", "pcs", "FALSE", "TRUE", "FALSE"]);
        AddSheet(wb, ExcelMasterDataReader.BomLinesSheet, ExcelMasterDataReader.BomLineColumns,
            ["PCB-X", "RES-1K-0402", "12", "0.02", "10"]);
        AddSheet(wb, ExcelMasterDataReader.RoutingStepsSheet, ExcelMasterDataReader.RoutingStepColumns,
            ["PCB-X", "10", "SMT", "WC-SMT", "30", "FALSE", "TRUE", ""]);

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    // Row 1 = header, row 2 = one example row (delete it before importing).
    private static void AddSheet(XLWorkbook wb, string name, string[] columns, string[] example)
    {
        var ws = wb.AddWorksheet(name);
        for (var i = 0; i < columns.Length; i++)
        {
            var column = ws.Column(i + 1);
            if (ExcelMasterDataReader.CodeColumns.Contains(columns[i]))
                column.Style.NumberFormat.Format = "@"; // Text: keeps leading zeros and "1E5"-like codes
            ws.Cell(1, i + 1).Value = columns[i];
            ws.Cell(2, i + 1).Value = example[i];
        }
        ws.Row(1).Style.Font.Bold = true;
        ws.SheetView.FreezeRows(1);
        ws.Columns().Width = 18; // fixed: AdjustToContents needs system fonts, missing on the Linux CI runner
    }
}
