using System.Net.Http.Headers;
using System.Text;
using MiniMes.Application.Labels;

namespace MiniMes.Infrastructure.Labels;

// Labelary (labelary.com/service.html): POST the ZPL to
// /v1/printers/{density}/labels/{width}x{height inches}/{label index}/ with Accept: image/png -> the label as PNG.
// The HttpClient comes from IHttpClientFactory (registered in the Api project), base address api.labelary.com.
public sealed class LabelaryPreviewClient(HttpClient http) : ILabelPreview
{
    public const string BaseAddress = "https://api.labelary.com/";

    public async Task<byte[]> RenderPngAsync(string zpl, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"v1/printers/{ZplLabelBuilder.LabelaryDensity}/labels/{ZplLabelBuilder.LabelaryInches}/0/")
        {
            // Raw ZPL as the body, declared as a form post (what curl --data sends); text/plain -> 415.
            Content = new StringContent(zpl, Encoding.ASCII, "application/x-www-form-urlencoded"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/png"));

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)   // Labelary explains bad ZPL or rate limits in the body
            throw new InvalidOperationException(
                $"Label preview failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(ct)}");
        return await response.Content.ReadAsByteArrayAsync(ct);
    }
}
