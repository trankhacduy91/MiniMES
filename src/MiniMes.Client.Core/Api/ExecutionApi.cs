using MiniMes.Client.Core.Http;
using MiniMes.Contracts.Execution;
using MiniMes.Contracts.Factory;
using MiniMes.Contracts.MasterData;

namespace MiniMes.Client.Core.Api;

// Typed wrapper over the M3 station endpoints used by the kiosk.
public interface IExecutionApi
{
    Task<IReadOnlyList<StationDto>> GetStationsAsync(CancellationToken ct = default);
    Task<StationSessionDto> OpenSessionAsync(int stationId, int workOrderId, CancellationToken ct = default);
    Task CloseSessionAsync(int stationId, long sessionId, CancellationToken ct = default);
    Task<ScanResponse> ScanAsync(int stationId, ScanRequest request, CancellationToken ct = default);

    // GET /health: true when the API answers. Never throws (the kiosk polls it every few seconds).
    Task<bool> PingAsync(CancellationToken ct = default);

    // sopUrl as stored in the routing ("/sop/PCB-A/SMT-v1.pdf"), served under /api.
    Task<byte[]> GetSopAsync(string sopUrl, CancellationToken ct = default);

    // Step 8: the NG panel of a test station and the repair form.
    Task<IReadOnlyList<DefectCodeDto>> GetDefectCodesAsync(CancellationToken ct = default);
    Task<RepairResponse> RepairAsync(int stationId, RepairRequest request, CancellationToken ct = default);
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

    public Task<ScanResponse> ScanAsync(int stationId, ScanRequest request, CancellationToken ct = default)
        => api.PostAsync<ScanRequest, ScanResponse>($"{Base}/{stationId}/scan", request, ct);

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            await api.GetBytesAsync("health", ct);
            return true;
        }
        catch (Exception ex) when (ex is ApiException or OperationCanceledException)
        {
            return false;
        }
    }

    public Task<byte[]> GetSopAsync(string sopUrl, CancellationToken ct = default)
        => api.GetBytesAsync("api/" + sopUrl.TrimStart('/'), ct);

    public Task<IReadOnlyList<DefectCodeDto>> GetDefectCodesAsync(CancellationToken ct = default)
        => api.GetAsync<IReadOnlyList<DefectCodeDto>>("api/defect-codes?isActive=true", ct);

    public Task<RepairResponse> RepairAsync(int stationId, RepairRequest request, CancellationToken ct = default)
        => api.PostAsync<RepairRequest, RepairResponse>($"{Base}/{stationId}/repair", request, ct);
}
