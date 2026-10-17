namespace MiniMes.Contracts.Planning;

// POST /api/work-orders/{id}/labels/preview -> image/png of one label. No serial = the first one of the WO.
public sealed record PreviewLabelRequest(string? SerialNo = null);

// POST /api/work-orders/{id}/labels/print. No serials = every label of the WO (except scrapped serials).
// A serial printed before is a reprint: Reason is then required, and the log gets PrintNo 2, 3...
public sealed record PrintLabelsRequest(IReadOnlyList<string>? SerialNos = null, string? Reason = null);

// Zpl = all labels, ready to send to a Zebra printer (TCP 9100) or save as a .zpl file.
public sealed record PrintLabelsResultDto(int Printed, int Reprinted, string Zpl);
