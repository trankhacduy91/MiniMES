using MiniMes.Client.Core.Http;
using MiniMes.Contracts.Andon;
using MiniMes.Contracts.Factory;
using MiniMes.Contracts.MasterData;
using MiniMes.Contracts.Realtime;

namespace MiniMes.Client.Core.Api;

// M4 step 8: what the line dashboard (BackOffice) and the Andon panel (Kiosk) read and do over REST.
// The live part comes over SignalR (IRealtimeClient); this is the snapshot and the commands.
public interface IMachineApi
{
    Task<IReadOnlyList<LineDto>> GetLinesAsync(CancellationToken ct = default);
    Task<LineDto> GetLineAsync(int id, CancellationToken ct = default);
    Task<LineStatusDto> GetLineStatusAsync(string lineCode, CancellationToken ct = default);
    Task<IReadOnlyList<MachineDto>> GetMachinesAsync(int workCenterId, CancellationToken ct = default);
    Task<IReadOnlyList<DowntimeReasonDto>> GetDowntimeReasonsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<AndonDto>> GetOpenAndonsAsync(string lineCode, CancellationToken ct = default);
    Task<RaiseAndonResult> RaiseAndonAsync(int machineId, string type, CancellationToken ct = default);
    Task<AndonDto> AcknowledgeAndonAsync(AndonDto andon, CancellationToken ct = default);
    Task<AndonDto> ResolveAndonAsync(AndonDto andon, int downtimeReasonId, string? note, CancellationToken ct = default);
}

public sealed class MachineApi(ApiClient api) : IMachineApi
{
    public Task<IReadOnlyList<LineDto>> GetLinesAsync(CancellationToken ct = default)
        => api.GetAsync<IReadOnlyList<LineDto>>("api/lines", ct);

    public Task<LineDto> GetLineAsync(int id, CancellationToken ct = default) => api.GetAsync<LineDto>($"api/lines/{id}", ct);

    public Task<LineStatusDto> GetLineStatusAsync(string lineCode, CancellationToken ct = default)
        => api.GetAsync<LineStatusDto>($"api/lines/{Uri.EscapeDataString(lineCode)}/status", ct);

    public Task<IReadOnlyList<MachineDto>> GetMachinesAsync(int workCenterId, CancellationToken ct = default)
        => api.GetAsync<IReadOnlyList<MachineDto>>($"api/machines?workCenterId={workCenterId}&isActive=true", ct);

    public Task<IReadOnlyList<DowntimeReasonDto>> GetDowntimeReasonsAsync(CancellationToken ct = default)
        => api.GetAsync<IReadOnlyList<DowntimeReasonDto>>("api/downtime-reasons?isActive=true", ct);

    public Task<IReadOnlyList<AndonDto>> GetOpenAndonsAsync(string lineCode, CancellationToken ct = default)
        => api.GetAsync<IReadOnlyList<AndonDto>>($"api/andon?status=open&line={Uri.EscapeDataString(lineCode)}", ct);

    public Task<RaiseAndonResult> RaiseAndonAsync(int machineId, string type, CancellationToken ct = default)
        => api.PostAsync<RaiseAndonRequest, RaiseAndonResult>("api/andon", new RaiseAndonRequest(machineId, type), ct);

    // The RowVersion the user saw: someone else was faster -> 409 (ApiException), the screen reloads.
    public Task<AndonDto> AcknowledgeAndonAsync(AndonDto andon, CancellationToken ct = default)
        => api.PostAsync<AndonTransitionRequest, AndonDto>($"api/andon/{andon.Id}/acknowledge", new AndonTransitionRequest(andon.RowVersion), ct);

    public Task<AndonDto> ResolveAndonAsync(AndonDto andon, int downtimeReasonId, string? note, CancellationToken ct = default)
        => api.PostAsync<ResolveAndonRequest, AndonDto>($"api/andon/{andon.Id}/resolve",
            new ResolveAndonRequest(downtimeReasonId, note, andon.RowVersion), ct);
}
