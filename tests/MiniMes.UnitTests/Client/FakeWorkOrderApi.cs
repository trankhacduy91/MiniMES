using MiniMes.Client.Core.Api;
using MiniMes.Contracts.Common;
using MiniMes.Contracts.Planning;

namespace MiniMes.UnitTests.Client;

// Same idea as FakeMasterDataApi: every call fails loudly unless a test overrides it.
public class FakeWorkOrderApi : IWorkOrderApi
{
    public virtual Task<PagedResult<WorkOrderSummaryDto>> GetWorkOrdersAsync(string? status, int? lineId, string? search, int page, int pageSize, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<WorkOrderDetailDto> GetWorkOrderAsync(int id, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<WorkOrderPreviewDto> PreviewAsync(PreviewWorkOrderRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<WorkOrderDetailDto> CreateAsync(CreateWorkOrderRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<WorkOrderDetailDto> UpdateAsync(int id, UpdateWorkOrderRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<WorkOrderDetailDto> TransitionAsync(int id, string action, WorkOrderTransitionRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<WorkOrderDetailDto> ChangePriorityAsync(int id, ChangePriorityRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<PagedResult<SerialDto>> GetSerialsAsync(int id, int page, int pageSize, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<byte[]> PreviewLabelAsync(int id, PreviewLabelRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<PrintLabelsResultDto> PrintLabelsAsync(int id, PrintLabelsRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<IReadOnlyList<DispatchListItemDto>> GetDispatchListAsync(int lineId, CancellationToken ct = default) => throw new NotImplementedException();
}
