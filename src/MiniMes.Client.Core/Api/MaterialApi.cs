using System.Globalization;
using System.Text;
using MiniMes.Client.Core.Http;
using MiniMes.Contracts.Common;
using MiniMes.Contracts.Material;

namespace MiniMes.Client.Core.Api;

// M6: material lots (receiving, labels).
public interface IMaterialApi
{
    Task<ReceiveLotsResultDto> ReceiveAsync(ReceiveLotsRequest request, CancellationToken ct = default);
    Task<PagedResult<MaterialLotDto>> GetLotsAsync(string? search, string? status, int page, int pageSize, CancellationToken ct = default);
    Task<string> GetLabelAsync(int lotId, CancellationToken ct = default);
    Task<byte[]> PreviewLabelAsync(int lotId, CancellationToken ct = default);
}

public sealed class MaterialApi(ApiClient api) : IMaterialApi
{
    private const string Lots = "api/material/lots";

    public Task<ReceiveLotsResultDto> ReceiveAsync(ReceiveLotsRequest request, CancellationToken ct = default)
        => api.PostAsync<ReceiveLotsRequest, ReceiveLotsResultDto>(Lots, request, ct);

    public Task<PagedResult<MaterialLotDto>> GetLotsAsync(string? search, string? status, int page, int pageSize, CancellationToken ct = default)
    {
        var q = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"{Lots}?page={page}&pageSize={pageSize}"));
        if (!string.IsNullOrWhiteSpace(search)) q.Append(CultureInfo.InvariantCulture, $"&search={Uri.EscapeDataString(search.Trim())}");
        if (!string.IsNullOrWhiteSpace(status)) q.Append(CultureInfo.InvariantCulture, $"&status={status}");
        return api.GetAsync<PagedResult<MaterialLotDto>>(q.ToString(), ct);
    }

    public async Task<string> GetLabelAsync(int lotId, CancellationToken ct = default)
        => Encoding.UTF8.GetString(await api.GetBytesAsync($"{Lots}/{lotId}/label", ct));

    public Task<byte[]> PreviewLabelAsync(int lotId, CancellationToken ct = default)
        => api.GetBytesAsync($"{Lots}/{lotId}/label/preview", ct);
}
