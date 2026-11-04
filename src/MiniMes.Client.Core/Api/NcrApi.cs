using MiniMes.Client.Core.Http;
using MiniMes.Contracts.Quality;

namespace MiniMes.Client.Core.Api;

// M7 step 8: nonconformance reports (QC, Supervisor, Admin). Every transition sends the RowVersion on screen.
public interface INcrApi
{
    Task<IReadOnlyList<NcrListItemDto>> GetListAsync(string? status, CancellationToken ct = default);
    Task<NcrDto> GetAsync(int id, CancellationToken ct = default);
    Task<NcrDto> CreateAsync(CreateNcrRequest request, CancellationToken ct = default);
    Task<NcrDto> StartReviewAsync(int id, string rowVersion, CancellationToken ct = default);
    Task<NcrDto> ProposeAsync(int id, ProposeDispositionRequest request, CancellationToken ct = default);
    Task<NcrDto> ApproveAsync(int id, string rowVersion, CancellationToken ct = default);
    Task<NcrDto> RejectProposalAsync(int id, string rowVersion, CancellationToken ct = default);
    Task<NcrDto> CloseAsync(int id, CloseNcrRequest request, CancellationToken ct = default);
    Task<NcrDto> CancelAsync(int id, CancelNcrRequest request, CancellationToken ct = default);
}

public sealed class NcrApi(ApiClient api) : INcrApi
{
    private const string Base = "api/ncrs";

    public Task<IReadOnlyList<NcrListItemDto>> GetListAsync(string? status, CancellationToken ct = default)
        => api.GetAsync<IReadOnlyList<NcrListItemDto>>(status is null ? Base : $"{Base}?status={Uri.EscapeDataString(status)}", ct);

    public Task<NcrDto> GetAsync(int id, CancellationToken ct = default) => api.GetAsync<NcrDto>($"{Base}/{id}", ct);

    public Task<NcrDto> CreateAsync(CreateNcrRequest request, CancellationToken ct = default)
        => api.PostAsync<CreateNcrRequest, NcrDto>(Base, request, ct);

    public Task<NcrDto> StartReviewAsync(int id, string rowVersion, CancellationToken ct = default)
        => api.PostAsync<NcrTransitionRequest, NcrDto>($"{Base}/{id}/start-review", new(rowVersion), ct);

    public Task<NcrDto> ProposeAsync(int id, ProposeDispositionRequest request, CancellationToken ct = default)
        => api.PostAsync<ProposeDispositionRequest, NcrDto>($"{Base}/{id}/propose", request, ct);

    public Task<NcrDto> ApproveAsync(int id, string rowVersion, CancellationToken ct = default)
        => api.PostAsync<NcrTransitionRequest, NcrDto>($"{Base}/{id}/approve", new(rowVersion), ct);

    public Task<NcrDto> RejectProposalAsync(int id, string rowVersion, CancellationToken ct = default)
        => api.PostAsync<NcrTransitionRequest, NcrDto>($"{Base}/{id}/reject-proposal", new(rowVersion), ct);

    public Task<NcrDto> CloseAsync(int id, CloseNcrRequest request, CancellationToken ct = default)
        => api.PostAsync<CloseNcrRequest, NcrDto>($"{Base}/{id}/close", request, ct);

    public Task<NcrDto> CancelAsync(int id, CancelNcrRequest request, CancellationToken ct = default)
        => api.PostAsync<CancelNcrRequest, NcrDto>($"{Base}/{id}/cancel", request, ct);
}
