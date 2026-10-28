using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using MiniMes.Contracts.Auth;
using MiniMes.Contracts.Execution;
using MiniMes.Contracts.Factory;
using MiniMes.Contracts.Planning;
using MiniMes.MachineSimulator.Config;

namespace MiniMes.MachineSimulator.Stations;

// Spec M4 2.8, role 2: an automatic station takes the next board waiting at its step and scans it through the very
// API the kiosk uses (login with a badge, open a session, POST /stations/{id}/scan). Serials and verdicts come from
// the MES, never invented here, so every scan goes through the M3 route check.
// Session: the first work order of the line's dispatch list that is not waiting for its first article (user decision
// 27/10/2026); when its boards run out and another order heads the list, the station moves to that one.
// Never throws: an unreachable API reads as "no board" and the machine starves (Idle), which is what a real line does.
public sealed class AutoStationClient(HttpClient http, ApiOptions api, StationOptions station, Random rng, ILogger log)
{
    private string? _token;
    private StationDto? _station;
    private int? _sessionWoId;

    public string StationCode => station.Code;

    public async Task<string?> NextSerialAsync(CancellationToken ct)
    {
        try
        {
            if (!await EnsureSessionAsync(ct)) return null;
            var next = await WaitingAsync(ct);
            // Nothing left for this order: maybe it is done and another one heads the list now.
            if (next is null && await SwitchToDispatchHeadAsync(ct)) next = await WaitingAsync(ct);
            return next;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)   // network, 4xx/5xx, unknown station code
        {
            Forget(ex);
            return null;
        }
    }

    // null = the scan was not recorded (API unreachable); Blocked comes back as a normal answer.
    public async Task<ScanResponse?> ScanAsync(string serialNo, bool ng, CancellationToken ct)
    {
        if (_station is null) return null;
        var (result, defect) = ng && station.Mode == StationMode.AutoTest
            ? ("NG", station.DefectCodes[rng.Next(station.DefectCodes.Length)])
            : ("OK", (string?)null);
        try
        {
            var answer = await SendAsync<ScanResponse>(HttpMethod.Post, $"api/stations/{_station.Id}/scan",
                new ScanRequest(serialNo, result, defect, Source: "Machine"), ct);
            if (answer.Result == "Blocked")
                log.LogWarning("{Station} {SerialNo} blocked: {Reason} {Message}", StationCode, serialNo, answer.ReasonCode, answer.Message);
            else
                log.LogInformation("{Station} scanned {SerialNo}: {Result} {DefectCode}", StationCode, serialNo, answer.Result, defect ?? "");
            return answer;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)   // network, 4xx/5xx, unknown station code
        {
            Forget(ex);
            return null;
        }
    }

    private async Task<bool> EnsureSessionAsync(CancellationToken ct)
    {
        if (_token is null)
        {
            var login = await http.PostAsJsonAsync("api/auth/login-operator", new OperatorLoginRequest(api.Badge, station.Code), ct);
            login.EnsureSuccessStatusCode();
            _token = (await login.Content.ReadFromJsonAsync<LoginResponse>(ct))!.Token;
            _sessionWoId = null;   // a new login means a new session
        }
        _station ??= (await SendAsync<List<StationDto>>(HttpMethod.Get, "api/stations", null, ct)).Single(s => s.Code == station.Code);
        if (_sessionWoId is null) await SwitchToDispatchHeadAsync(ct);
        return _sessionWoId is not null;
    }

    // true = a session was opened on another order than before.
    private async Task<bool> SwitchToDispatchHeadAsync(CancellationToken ct)
    {
        var list = await SendAsync<List<DispatchListItemDto>>(HttpMethod.Get, $"api/lines/{_station!.LineId}/dispatch-list", null, ct);
        // A WO whose first article is still being judged only lets the sample through: that board is the kiosk's job.
        var head = list.FirstOrDefault(w => w.FaiStatus != "Pending");
        if (head is null || head.WorkOrderId == _sessionWoId) return false;

        await SendAsync<StationSessionDto>(HttpMethod.Post, $"api/stations/{_station.Id}/sessions", new OpenSessionRequest(head.WorkOrderId), ct);
        _sessionWoId = head.WorkOrderId;
        log.LogInformation("{Station} opened a session on {WoNo} as {Badge}", StationCode, head.WoNo, api.Badge);
        return true;
    }

    private async Task<string?> WaitingAsync(CancellationToken ct)
        => (await SendAsync<List<WaitingSerialDto>>(HttpMethod.Get, $"api/stations/{_station!.Id}/waiting-serials?take=1", null, ct))
            .FirstOrDefault()?.SerialNo;

    private async Task<T> SendAsync<T>(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized) _token = null;   // expired: log in again next time
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(ct))!;
    }

    // Start again from the login next time: the API may have restarted with another key, or the session was closed.
    private void Forget(Exception ex)
    {
        log.LogWarning("{Station}: API not usable ({Error}); the machine waits", StationCode, ex.Message);
        _station = null;
        (_token, _sessionWoId) = (null, null);
    }
}

// One HttpClient for every station (sockets are shared); one client object per station (its own token and session).
public sealed class StationClients(HttpClient http, SimulatorOptions options, ILoggerFactory loggers)
{
    public AutoStationClient Create(StationOptions station, Random rng)
        => new(http, options.Api!, station, rng, loggers.CreateLogger<AutoStationClient>());
}
