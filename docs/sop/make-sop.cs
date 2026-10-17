// Writes the 4 sample SOP (work instruction) PDFs of the PCB-A routing, v1: docs/sop/PCB-A/{OP}-v1.pdf.
// RoutingStep.SopUrl of the seed points at them; the kiosk opens them in M3.
// A one-page PDF is plain text objects, so it is written by hand: no PDF library for 4 demo files.
// Run from the repo root: dotnet run docs/sop/make-sop.cs
using System.Text;

var sops = new (string Op, string Title, string[] Steps)[]
{
    ("SMT", "SMT - print, place, reflow", [
        "1. Scan the WO and check the feeder setup list matches BOM PCBA-SMT.",
        "2. Load PCB-BARE into the loader, bottom side down.",
        "3. Check solder paste on the first board under the magnifier.",
        "4. Start the line; reflow profile PCB-A-v1 must be selected on L1-REFLOW-01.",
        "5. Every 2 hours: check 5 boards for bridges and missing parts.",
    ]),
    ("ASSY", "Assembly - connector and shield", [
        "1. Wear the ESD strap and check it on the tester.",
        "2. Fit CONN-USBC; glue dot from L1-GLUE-01 must cover both tabs.",
        "3. Press SHIELD-CAN until all 4 clips click.",
        "4. Scan the board serial at station L1-ASM-01.",
    ]),
    ("TEST", "Functional test", [
        "1. Place the board on the fixture, close the lid.",
        "2. Scan the serial; the tester L1-TEST-01 runs program PCB-A-FT-v1.",
        "3. PASS: put the board on the green tray.",
        "4. FAIL: choose the defect code on the kiosk, put the board on the red tray for Repair.",
    ]),
    ("PACK", "Packing", [
        "1. Check the board has a PASS at TEST (the kiosk blocks it otherwise).",
        "2. Put the board in PACK-BAG and seal it.",
        "3. Scan the serial at L1-PACK-01, then put the bag in the carton.",
    ]),
};

var dir = Path.Combine("docs", "sop", "PCB-A");
Directory.CreateDirectory(dir);
foreach (var (op, title, steps) in sops)
{
    var path = Path.Combine(dir, $"{op}-v1.pdf");
    File.WriteAllBytes(path, OnePagePdf([$"PCB-A  /  {title}", "Work instruction, routing v1   -   SAMPLE for Mini MES", "", .. steps]));
    Console.WriteLine(path);
}

// A4 page, Helvetica, one line of text per entry (ASCII only: the built-in font has no Vietnamese glyphs).
static byte[] OnePagePdf(string[] lines)
{
    var text = new StringBuilder("BT /F1 18 Tf 50 790 Td 24 TL\n");
    for (var i = 0; i < lines.Length; i++)
    {
        if (i == 1) text.Append("/F1 11 Tf\n");
        var escaped = lines[i].Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
        text.Append($"({escaped}) Tj T*\n");
        if (i == 1) text.Append("18 TL\n");
    }
    text.Append("ET");

    string[] objects =
    [
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        $"<< /Length {Encoding.ASCII.GetByteCount(text.ToString())} >>\nstream\n{text}\nendstream",
    ];

    // The xref table holds the byte offset of every object, so build the file as ASCII and count bytes.
    var pdf = new StringBuilder("%PDF-1.4\n");
    var offsets = new List<int>();
    for (var i = 0; i < objects.Length; i++)
    {
        offsets.Add(pdf.Length);
        pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
    }
    var xref = pdf.Length;
    pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
    foreach (var o in offsets) pdf.Append($"{o:D10} 00000 n \n");
    pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    return Encoding.ASCII.GetBytes(pdf.ToString());
}
