using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MiniMes.Client.Core;
using MiniMes.Client.Core.Api;
using MiniMes.Client.Core.Auth;
using MiniMes.Client.Core.Kiosk;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.ViewModels.Kiosk;
using MiniMes.Contracts.Auth;
using MiniMes.Contracts.Execution;
using MiniMes.Contracts.Factory;
using MiniMes.Contracts.MasterData;
using MiniMes.Contracts.Planning;

namespace MiniMes.UnitTests.Client;

// Shared fakes of the kiosk screens (M3 steps 6-7): an in-memory station API, navigation and sounds.
internal sealed class FakeExecution : IExecutionApi
{
    public static readonly StationDto Asm = new(2, LineId: 1, "L1-ASM-01", "Assembly", "Assembly", true, WorkCenterId: 2);

    public IReadOnlyList<StationDto> Stations { get; init; } = [Asm];
    public Exception? StationsFail { get; init; }
    public Exception? OpenFails { get; init; }
    public Exception? CloseFails { get; init; }
    public List<(int StationId, int WoId)> Opened { get; } = [];
    public List<(int StationId, long SessionId)> Closed { get; } = [];

    // Scan: answer per serial; default OK -> next TEST. A test may hand back its own task to hold the answer.
    public Func<ScanRequest, CancellationToken, Task<ScanResponse>> Scan { get; set; }
        = (_, _) => Task.FromResult(new ScanResponse("Ok", "OK", null, 30, "TEST", false));
    public List<ScanRequest> Scans { get; } = [];
    public bool Online { get; set; } = true;
    public byte[] Sop { get; set; } = "%PDF-1.4"u8.ToArray();
    public string? SopAsked { get; private set; }

    public Task<IReadOnlyList<StationDto>> GetStationsAsync(CancellationToken ct = default)
        => StationsFail is null ? Task.FromResult(Stations) : Task.FromException<IReadOnlyList<StationDto>>(StationsFail);

    public Task<StationSessionDto> OpenSessionAsync(int stationId, int workOrderId, CancellationToken ct = default)
    {
        if (OpenFails is not null) return Task.FromException<StationSessionDto>(OpenFails);
        Opened.Add((stationId, workOrderId));
        return Task.FromResult(new StationSessionDto(77, stationId, "L1-ASM-01", workOrderId, "WO", "EMP00123", DateTime.UtcNow, null));
    }

    public Task CloseSessionAsync(int stationId, long sessionId, CancellationToken ct = default)
    {
        Closed.Add((stationId, sessionId));
        return CloseFails is null ? Task.CompletedTask : Task.FromException(CloseFails);
    }

    public Task<ScanResponse> ScanAsync(int stationId, ScanRequest request, CancellationToken ct = default)
    {
        Scans.Add(request);
        return Scan(request, ct);
    }

    public Task<bool> PingAsync(CancellationToken ct = default) => Task.FromResult(Online);

    public Task<byte[]> GetSopAsync(string sopUrl, CancellationToken ct = default)
    {
        SopAsked = sopUrl;
        return Task.FromResult(Sop);
    }

    public static readonly DefectCodeDto Bridge = new(1, "SLD-BRG", "Solder bridge", "Solder", true);
    public static readonly DefectCodeDto Missing = new(5, "CMP-MIS", "Missing component", "Component", true);
    public int DefectCodeLoads { get; private set; }

    public Task<IReadOnlyList<DefectCodeDto>> GetDefectCodesAsync(CancellationToken ct = default)
    {
        DefectCodeLoads++;
        return Task.FromResult<IReadOnlyList<DefectCodeDto>>([Bridge, Missing]);
    }

    // Repair: default = back to the failed TEST step.
    public Func<RepairRequest, Task<RepairResponse>> Repair { get; set; }
        = r => Task.FromResult(new RepairResponse(r.SerialNo, "InProcess", r.ReturnToSeq ?? 30, r.ReturnToSeq is null ? "TEST" : "SMT"));
    public List<RepairRequest> Repairs { get; } = [];

    public Task<RepairResponse> RepairAsync(int stationId, RepairRequest request, CancellationToken ct = default)
    {
        Repairs.Add(request);
        return Repair(request);
    }
}

internal sealed class FakeDispatch(params DispatchListItemDto[] list) : FakeWorkOrderApi
{
    public int? AskedLine { get; private set; }
    public WorkOrderDetailDto? Detail { get; set; }

    public override Task<IReadOnlyList<DispatchListItemDto>> GetDispatchListAsync(int lineId, CancellationToken ct = default)
    {
        AskedLine = lineId;
        return Task.FromResult<IReadOnlyList<DispatchListItemDto>>(list);
    }

    public override Task<WorkOrderDetailDto> GetWorkOrderAsync(int id, CancellationToken ct = default)
        => Detail is null ? throw new NotImplementedException() : Task.FromResult(Detail);
}

internal sealed class FakeKioskNavigation : IKioskNavigation
{
    public string Last { get; private set; } = string.Empty;
    public void ShowLogin() => Last = "Login";
    public void ShowHome() => Last = "Home";
    public void ShowScan() => Last = "Scan";
}

internal sealed class FakeKioskAuth(ITokenStore tokens) : IAuthClient
{
    public Task<LoginResponse> LoginAsync(string employeeCode, string password, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<LoginResponse> LoginOperatorAsync(string employeeCode, string stationCode, CancellationToken ct = default) => throw new NotImplementedException();
    public void Logout() => tokens.Clear();
}

internal sealed class FakeFeedback : IKioskFeedback
{
    public List<ScanState> Played { get; } = [];
    public void Play(ScanState state) => Played.Add(state);
}

// One kiosk: its fakes and the two screens built on them.
internal sealed class FakeKiosk
{
    public static DispatchListItemDto Wo(int id, string woNo, int priority, string? fai = "Pending")
        => new(id, woNo, "PCB-A", 200, 12, priority, new DateTime(2026, 10, 19, 1, 0, 0, DateTimeKind.Utc), "Released", fai);

    public IExecutionApi Execution { get; init; } = new FakeExecution();
    public FakeExecution Fake => (FakeExecution)Execution;
    public FakeDispatch Dispatch { get; init; } = new(Wo(5, "WO-2610-00005", 1), Wo(3, "WO-2610-00003", 5));
    public KioskContext Context { get; } = new();
    public FakeKioskNavigation Navigation { get; } = new();
    public TokenStore Tokens { get; } = new();
    public FakeFeedback Feedback { get; } = new();
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 19, 1, 0, 0, TimeSpan.Zero));

    public FakeKiosk() => Tokens.Set(new LoginResponse("t", DateTime.UtcNow, "EMP00123", "Nguyen Van Nam", ["Operator"], "L1-ASM-01"));

    public StationSelectViewModel Select(string station = "L1-ASM-01")
        => new(Execution, Dispatch, Context, Navigation, new FakeKioskAuth(Tokens), Tokens,
            Options.Create(new ClientOptions { ApiBaseUrl = "http://x/", StationCode = station }));

    public ScanViewModel Scan() => new(Execution, Dispatch, Context, Navigation, new FakeKioskAuth(Tokens), Tokens, Feedback, Time,
        new AndonPanelViewModel(Machines, Realtime, new ImmediateDispatcher(), Context));

    // M4 step 8: the Andon panel of the scan screen.
    public FakeMachineApi Machines { get; } = new();
    public FakeRealtimeFactory Realtime { get; } = new();

    public RepairViewModel Repair() => new(Execution, Dispatch, Context, Navigation, new FakeKioskAuth(Tokens), Tokens, Feedback, Time);

    public static readonly StationDto Test = new(3, LineId: 1, "L1-TEST-01", "Test", "Test", true, WorkCenterId: 3);
    public static readonly StationDto RepairStation = new(5, LineId: 1, "L1-REPAIR", "Repair", "Repair", true, WorkCenterId: null);

    // As after the badge login + Start: station and session known.
    public FakeKiosk Started(StationDto? station = null)
    {
        Context.SetStation(station ?? FakeExecution.Asm);
        Context.Start(new StationSessionDto(77, 2, "L1-ASM-01", 5, "WO-2610-00005", "EMP00123", DateTime.UtcNow, null), Wo(5, "WO-2610-00005", 1));
        return this;
    }
}
