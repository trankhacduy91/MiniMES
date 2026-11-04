using MiniMes.Client.Core.Http;
using MiniMes.Contracts.Quality;

namespace MiniMes.Client.Core.Api;

// [v2] M7 step 8b: first article inspection. Readers: QC, Supervisor, Admin; the result: QC, Admin.
public interface IFaiApi
{
    Task<IReadOnlyList<FaiQueueItemDto>> GetQueueAsync(CancellationToken ct = default);
    Task<FaiWorksheetDto> GetWorksheetAsync(int workOrderId, CancellationToken ct = default);
    Task<FaiWorksheetDto> RecordAsync(int workOrderId, RecordFaiRequest request, CancellationToken ct = default);
}

public sealed class FaiApi(ApiClient api) : IFaiApi
{
    private const string Base = "api/fai";

    public Task<IReadOnlyList<FaiQueueItemDto>> GetQueueAsync(CancellationToken ct = default)
        => api.GetAsync<IReadOnlyList<FaiQueueItemDto>>($"{Base}/queue", ct);

    public Task<FaiWorksheetDto> GetWorksheetAsync(int workOrderId, CancellationToken ct = default)
        => api.GetAsync<FaiWorksheetDto>($"{Base}/work-orders/{workOrderId}", ct);

    public Task<FaiWorksheetDto> RecordAsync(int workOrderId, RecordFaiRequest request, CancellationToken ct = default)
        => api.PostAsync<RecordFaiRequest, FaiWorksheetDto>($"{Base}/work-orders/{workOrderId}/result", request, ct);
}
