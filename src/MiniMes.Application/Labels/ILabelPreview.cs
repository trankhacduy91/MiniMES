namespace MiniMes.Application.Labels;

// Renders ZPL to a PNG without a printer (Labelary web service in Infrastructure). An external service:
// used for ONE sample label at a time, never for a whole order (rate limits, and customer data stays here).
public interface ILabelPreview
{
    Task<byte[]> RenderPngAsync(string zpl, CancellationToken ct);
}
