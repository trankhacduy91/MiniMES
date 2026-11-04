using MiniMes.Client.Core.Http;
using MiniMes.Contracts.Quality;

namespace MiniMes.Client.Core.Api;

// M7 step 9: quality Hold / Release (QC, Supervisor, Admin).
public interface IHoldApi
{
    Task<IReadOnlyList<HoldBatchDto>> GetBatchesAsync(bool activeOnly = true, CancellationToken ct = default);
    Task<HoldPreviewDto> PreviewAsync(HoldTargetsRequest request, CancellationToken ct = default);
    Task<HoldResultDto> HoldAsync(HoldTargetsRequest request, CancellationToken ct = default);
    Task<ReleaseResultDto> ReleaseAsync(Guid batchId, string note, CancellationToken ct = default);
}

public sealed class HoldApi(ApiClient api) : IHoldApi
{
    private const string Base = "api/quality/holds";

    public Task<IReadOnlyList<HoldBatchDto>> GetBatchesAsync(bool activeOnly = true, CancellationToken ct = default)
        => api.GetAsync<IReadOnlyList<HoldBatchDto>>($"{Base}?activeOnly={(activeOnly ? "true" : "false")}", ct);

    public Task<HoldPreviewDto> PreviewAsync(HoldTargetsRequest request, CancellationToken ct = default)
        => api.PostAsync<HoldTargetsRequest, HoldPreviewDto>($"{Base}/preview", request, ct);

    public Task<HoldResultDto> HoldAsync(HoldTargetsRequest request, CancellationToken ct = default)
        => api.PostAsync<HoldTargetsRequest, HoldResultDto>(Base, request, ct);

    public Task<ReleaseResultDto> ReleaseAsync(Guid batchId, string note, CancellationToken ct = default)
        => api.PostAsync<ReleaseHoldRequest, ReleaseResultDto>($"{Base}/{batchId}/release", new ReleaseHoldRequest(note), ct);
}
