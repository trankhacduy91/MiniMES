using MiniMes.Client.Core.Http;
using MiniMes.Contracts.Common;
using MiniMes.Contracts.Planning;

namespace MiniMes.Client.Core.Api;

// Typed wrapper over the M2 endpoints. Every status change sends the RowVersion the screen loaded:
// if someone changed the order in between, the API answers 409 and the screen reloads.
public interface IWorkOrderApi
{
    Task<PagedResult<WorkOrderSummaryDto>> GetWorkOrdersAsync(string? status, int? lineId, string? search, int page, int pageSize, CancellationToken ct = default);
    Task<WorkOrderDetailDto> GetWorkOrderAsync(int id, CancellationToken ct = default);
    Task<WorkOrderPreviewDto> PreviewAsync(PreviewWorkOrderRequest request, CancellationToken ct = default);
    Task<WorkOrderDetailDto> CreateAsync(CreateWorkOrderRequest request, CancellationToken ct = default);
    Task<WorkOrderDetailDto> UpdateAsync(int id, UpdateWorkOrderRequest request, CancellationToken ct = default);

    // action = release | hold | resume | complete | close | cancel
    Task<WorkOrderDetailDto> TransitionAsync(int id, string action, WorkOrderTransitionRequest request, CancellationToken ct = default);
    Task<WorkOrderDetailDto> ChangePriorityAsync(int id, ChangePriorityRequest request, CancellationToken ct = default);

    Task<PagedResult<SerialDto>> GetSerialsAsync(int id, int page, int pageSize, CancellationToken ct = default);
    Task<byte[]> PreviewLabelAsync(int id, PreviewLabelRequest request, CancellationToken ct = default);
    Task<PrintLabelsResultDto> PrintLabelsAsync(int id, PrintLabelsRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<DispatchListItemDto>> GetDispatchListAsync(int lineId, CancellationToken ct = default);
}

public sealed class WorkOrderApi(ApiClient api) : IWorkOrderApi
{
    private const string Base = "api/work-orders";

    public Task<PagedResult<WorkOrderSummaryDto>> GetWorkOrdersAsync(string? status, int? lineId, string? search, int page, int pageSize, CancellationToken ct = default)
    {
        var query = new List<string> { $"page={page}", $"pageSize={pageSize}" };
        if (status is not null) query.Add($"status={Uri.EscapeDataString(status)}");
        if (lineId is not null) query.Add($"lineId={lineId}");
        if (search is not null) query.Add($"search={Uri.EscapeDataString(search)}");
        return api.GetAsync<PagedResult<WorkOrderSummaryDto>>($"{Base}?{string.Join('&', query)}", ct);
    }

    public Task<WorkOrderDetailDto> GetWorkOrderAsync(int id, CancellationToken ct = default)
        => api.GetAsync<WorkOrderDetailDto>($"{Base}/{id}", ct);

    public Task<WorkOrderPreviewDto> PreviewAsync(PreviewWorkOrderRequest request, CancellationToken ct = default)
        => api.PostAsync<PreviewWorkOrderRequest, WorkOrderPreviewDto>($"{Base}/preview", request, ct);

    public Task<WorkOrderDetailDto> CreateAsync(CreateWorkOrderRequest request, CancellationToken ct = default)
        => api.PostAsync<CreateWorkOrderRequest, WorkOrderDetailDto>(Base, request, ct);

    public Task<WorkOrderDetailDto> UpdateAsync(int id, UpdateWorkOrderRequest request, CancellationToken ct = default)
        => api.PutAsync<UpdateWorkOrderRequest, WorkOrderDetailDto>($"{Base}/{id}", request, ct);

    public Task<WorkOrderDetailDto> TransitionAsync(int id, string action, WorkOrderTransitionRequest request, CancellationToken ct = default)
        => api.PostAsync<WorkOrderTransitionRequest, WorkOrderDetailDto>($"{Base}/{id}/{action}", request, ct);

    public Task<WorkOrderDetailDto> ChangePriorityAsync(int id, ChangePriorityRequest request, CancellationToken ct = default)
        => api.PutAsync<ChangePriorityRequest, WorkOrderDetailDto>($"{Base}/{id}/priority", request, ct);

    public Task<PagedResult<SerialDto>> GetSerialsAsync(int id, int page, int pageSize, CancellationToken ct = default)
        => api.GetAsync<PagedResult<SerialDto>>($"{Base}/{id}/serials?page={page}&pageSize={pageSize}", ct);

    public Task<byte[]> PreviewLabelAsync(int id, PreviewLabelRequest request, CancellationToken ct = default)
        => api.PostForBytesAsync($"{Base}/{id}/labels/preview", request, ct);

    public Task<PrintLabelsResultDto> PrintLabelsAsync(int id, PrintLabelsRequest request, CancellationToken ct = default)
        => api.PostAsync<PrintLabelsRequest, PrintLabelsResultDto>($"{Base}/{id}/labels/print", request, ct);

    public Task<IReadOnlyList<DispatchListItemDto>> GetDispatchListAsync(int lineId, CancellationToken ct = default)
        => api.GetAsync<IReadOnlyList<DispatchListItemDto>>($"api/lines/{lineId}/dispatch-list", ct);
}
