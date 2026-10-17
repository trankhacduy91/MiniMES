using MiniMes.Client.Core.Http;
using MiniMes.Contracts.Execution;

namespace MiniMes.Client.Core.Api;

// M3 step 9: production reports of one work order.
public interface IReportApi
{
    Task<FpyReportDto> GetFpyAsync(int workOrderId, CancellationToken ct = default);
    Task<WipReportDto> GetWipAsync(int workOrderId, CancellationToken ct = default);
}

public sealed class ReportApi(ApiClient api) : IReportApi
{
    public Task<FpyReportDto> GetFpyAsync(int workOrderId, CancellationToken ct = default)
        => api.GetAsync<FpyReportDto>($"api/reports/fpy?workOrderId={workOrderId}", ct);

    public Task<WipReportDto> GetWipAsync(int workOrderId, CancellationToken ct = default)
        => api.GetAsync<WipReportDto>($"api/reports/wip?workOrderId={workOrderId}", ct);
}
