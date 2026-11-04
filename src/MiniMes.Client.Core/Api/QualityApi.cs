using MiniMes.Client.Core.Http;
using MiniMes.Contracts.Quality;

namespace MiniMes.Client.Core.Api;

// M7 step 7: what the control chart reads (QC, Supervisor, Admin).
public interface IQualityApi
{
    Task<IReadOnlyList<InspectionPlanDto>> GetPlansAsync(CancellationToken ct = default);
    Task<SpcChartDto> GetChartAsync(int planId, int take, CancellationToken ct = default);
}

public sealed class QualityApi(ApiClient api) : IQualityApi
{
    private const string Base = "api/quality";

    public Task<IReadOnlyList<InspectionPlanDto>> GetPlansAsync(CancellationToken ct = default)
        => api.GetAsync<IReadOnlyList<InspectionPlanDto>>($"{Base}/plans", ct);

    public Task<SpcChartDto> GetChartAsync(int planId, int take, CancellationToken ct = default)
        => api.GetAsync<SpcChartDto>($"{Base}/spc/{planId}/chart?take={take}", ct);
}
