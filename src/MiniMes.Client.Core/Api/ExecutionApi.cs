using MiniMes.Client.Core.Http;
using MiniMes.Contracts.Execution;
using MiniMes.Contracts.Factory;

namespace MiniMes.Client.Core.Api;

// Typed wrapper over the M3 station endpoints used by the kiosk.
public interface IExecutionApi
{
    Task<IReadOnlyList<StationDto>> GetStationsAsync(CancellationToken ct = default);
    Task<StationSessionDto> OpenSessionAsync(int stationId, int workOrderId, CancellationToken ct = default);
    Task CloseSessionAsync(int stationId, long sessionId, CancellationToken ct = default);
}

public sealed class ExecutionApi(ApiClient api) : IExecutionApi
{
    private const string Base = "api/stations";

    public Task<IReadOnlyList<StationDto>> GetStationsAsync(CancellationToken ct = default)
        => api.GetAsync<IReadOnlyList<StationDto>>(Base, ct);

    public Task<StationSessionDto> OpenSessionAsync(int stationId, int workOrderId, CancellationToken ct = default)
        => api.PostAsync<OpenSessionRequest, StationSessionDto>($"{Base}/{stationId}/sessions", new OpenSessionRequest(workOrderId), ct);

    public Task CloseSessionAsync(int stationId, long sessionId, CancellationToken ct = default)
        => api.DeleteAsync($"{Base}/{stationId}/sessions/{sessionId}", ct);
}
