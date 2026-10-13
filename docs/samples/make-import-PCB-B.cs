#:package ClosedXML@0.105.1
// Regenerates import-PCB-B.xlsx (spec M1 step 8b): `dotnet run docs/samples/make-import-PCB-B.cs`.
// PCB-B is a second model: its own new items, plus components that PCB-A already uses (RES, CAP,
// SOLDER-PASTE) and the Line 1 work centers, which the import finds in the database.
using ClosedXML.Excel;

using var wb = new XLWorkbook();

Sheet("Items", ["Code", "Name", "Type", "Uom", "IsSerialized", "IsLotControlled", "SkipIqc"],
[
    ["PCB-B", "Controller board B", "Finished", "pcs", "TRUE", "FALSE", "FALSE"],
    ["PCBB-SMT", "PCB-B after SMT", "Semi", "pcs", "FALSE", "FALSE", "FALSE"],
    ["PCB-BARE-B", "Bare PCB for PCB-B", "Raw", "pcs", "FALSE", "TRUE", "FALSE"],
    ["IC-MCU-B", "MCU for PCB-B", "Raw", "pcs", "FALSE", "TRUE", "FALSE"],   // row 5: test (b) breaks its Uom
    ["CONN-JST-4P", "JST connector 4 pin", "Raw", "pcs", "FALSE", "TRUE", "FALSE"],
]);

Sheet("BomLines", ["ParentCode", "ComponentCode", "QtyPer", "ScrapRate", "OperationSeq"],
[
    ["PCB-B", "PCBB-SMT", 1, 0.01, 20],
    ["PCB-B", "CONN-JST-4P", 1, 0.005, 20],
    ["PCBB-SMT", "PCB-BARE-B", 1, 0.005, 10],
    ["PCBB-SMT", "RES-10K-0402", 8, 0.02, 10],
    ["PCBB-SMT", "CAP-100N-0402", 12, 0.02, 10],
    ["PCBB-SMT", "IC-MCU-B", 1, 0.005, 10],
    ["PCBB-SMT", "SOLDER-PASTE", 0.0005, 0.05, 10],
]);

Sheet("RoutingSteps", ["ItemCode", "Seq", "OperationCode", "WorkCenterCode", "IdealCycleTimeSec", "IsInspection", "IsMandatory", "SopUrl"],
[
    ["PCB-B", 10, "SMT", "WC-SMT", 28, "FALSE", "TRUE", ""],
    ["PCB-B", 20, "ASSY", "WC-ASSY", 50, "FALSE", "TRUE", ""],
    ["PCB-B", 30, "TEST", "WC-TEST", 40, "TRUE", "TRUE", ""],
    ["PCB-B", 40, "PACK", "WC-PACK", 20, "FALSE", "TRUE", ""],
]);

var path = Path.Combine(Path.GetDirectoryName(AppContext.GetData("EntryPointFilePath") as string ?? ".")!, "import-PCB-B.xlsx");
wb.SaveAs(path);
Console.WriteLine($"Wrote {path}");

void Sheet(string name, string[] columns, object[][] rows)
{
    var ws = wb.AddWorksheet(name);
    string[] codeColumns = ["Code", "ParentCode", "ComponentCode", "ItemCode", "OperationCode", "WorkCenterCode"];
    for (var c = 0; c < columns.Length; c++)
    {
        if (codeColumns.Contains(columns[c])) ws.Column(c + 1).Style.NumberFormat.Format = "@";
        ws.Cell(1, c + 1).Value = columns[c];
        for (var r = 0; r < rows.Length; r++)
            ws.Cell(r + 2, c + 1).Value = rows[r][c] switch
            {
                int i => i,
                double d => d,
                var o => o.ToString(),
            };
    }
    ws.Row(1).Style.Font.Bold = true;
    ws.SheetView.FreezeRows(1);
    ws.Columns().Width = 18;
}
