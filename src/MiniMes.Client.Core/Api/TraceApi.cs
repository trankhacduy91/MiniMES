using MiniMes.Client.Core.Http;
using MiniMes.Contracts.Traceability;

namespace MiniMes.Client.Core.Api;

// M6 step 8: traceability (QC, Supervisor, Admin).
public interface ITraceApi
{
    // Either lotNo, or supplierLot + itemCode.
    Task<ForwardTraceResultDto> GetForwardAsync(string? lotNo, string? supplierLot, string? itemCode, CancellationToken ct = default);
    Task<byte[]> GetForwardExcelAsync(string? lotNo, string? supplierLot, string? itemCode, CancellationToken ct = default);
    Task<IReadOnlyList<TraceNodeDto>> GetChildrenAsync(string kind, string key, CancellationToken ct = default);
    Task<HoldLotResultDto> HoldLotAsync(string lotNo, CancellationToken ct = default);
}

public sealed class TraceApi(ApiClient api) : ITraceApi
{
    private const string Base = "api/trace";

    public Task<ForwardTraceResultDto> GetForwardAsync(string? lotNo, string? supplierLot, string? itemCode, CancellationToken ct = default)
        => api.GetAsync<ForwardTraceResultDto>($"{Base}/forward{Query(lotNo, supplierLot, itemCode)}", ct);

    public Task<byte[]> GetForwardExcelAsync(string? lotNo, string? supplierLot, string? itemCode, CancellationToken ct = default)
        => api.GetBytesAsync($"{Base}/forward.xlsx{Query(lotNo, supplierLot, itemCode)}", ct);

    public Task<IReadOnlyList<TraceNodeDto>> GetChildrenAsync(string kind, string key, CancellationToken ct = default)
        => api.GetAsync<IReadOnlyList<TraceNodeDto>>($"{Base}/nodes/{kind}/{Uri.EscapeDataString(key)}/children", ct);

    public Task<HoldLotResultDto> HoldLotAsync(string lotNo, CancellationToken ct = default)
        => api.PostAsync<HoldLotRequest, HoldLotResultDto>($"{Base}/hold-lot", new HoldLotRequest(lotNo), ct);

    private static string Query(string? lotNo, string? supplierLot, string? itemCode)
    {
        var parts = new[] { ("lotNo", lotNo), ("supplierLot", supplierLot), ("itemCode", itemCode) }
            .Where(p => !string.IsNullOrWhiteSpace(p.Item2))
            .Select(p => $"{p.Item1}={Uri.EscapeDataString(p.Item2!.Trim())}");
        return "?" + string.Join("&", parts);
    }
}
