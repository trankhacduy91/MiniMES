using System.Buffers.Binary;
using System.Net;
using MiniMes.Client.Core.Api;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Realtime;
using MiniMes.Contracts.Andon;
using MiniMes.Contracts.Factory;
using MiniMes.Contracts.MasterData;
using MiniMes.Contracts.Realtime;
using WireState = MiniMes.Contracts.Machines.MachineState;

namespace MiniMes.UnitTests.Client;

// M4 step 8: the UI thread is the test thread.
internal sealed class ImmediateDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

// Stands in for the SignalR connection: the test pushes events by hand, in the order it wants.
internal sealed class FakeRealtimeClient : IRealtimeClient
{
    public event Action<MachineStateChangedDto>? MachineStateChanged;
    public event Action<MachineCounterDto>? MachineCounterUpdated;
    public event Action<MachineProcessDto>? MachineProcessUpdated;
    public event Action<AndonDto>? AndonChanged;
    public event Action<AndonDto>? AndonEscalated;
    public event Action<bool>? ConnectionChanged;
    public event Func<Task>? Resync;

    public List<string> Joined { get; } = [];
    public List<string> Left { get; } = [];
    public bool Started { get; private set; }
    public bool Disposed { get; private set; }
    public bool IsConnected { get; private set; }

    public Task StartAsync() { Started = true; return Task.CompletedTask; }
    public Task JoinLineAsync(string lineCode) { Joined.Add(lineCode); return Task.CompletedTask; }
    public Task LeaveLineAsync(string lineCode) { Left.Add(lineCode); return Task.CompletedTask; }
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }

    public void Push(MachineStateChangedDto d) => MachineStateChanged?.Invoke(d);
    public void Push(MachineCounterDto d) => MachineCounterUpdated?.Invoke(d);
    public void Push(MachineProcessDto d) => MachineProcessUpdated?.Invoke(d);
    public void Push(AndonDto d) => AndonChanged?.Invoke(d);
    public void Escalate(AndonDto d) => AndonEscalated?.Invoke(d);

    // Connection lost, then back: like the real client, joined again before Resync.
    public void Drop() { IsConnected = false; ConnectionChanged?.Invoke(false); }

    public async Task ReconnectAsync()
    {
        IsConnected = true;
        ConnectionChanged?.Invoke(true);
        if (Resync is { } r) await r();
    }

    public bool HasResyncHandler => Resync is not null;
}

internal sealed class FakeRealtimeFactory : IRealtimeClientFactory
{
    public FakeRealtimeClient Client { get; } = new();
    public IRealtimeClient Create() => Client;
}

internal sealed class FakeMachineApi : IMachineApi
{
    public static readonly LineDto L1 = new(1, 1, "L1", "Line 1", true);
    public static readonly LineDto L2 = new(2, 1, "L2", "Line 2", true);
    public static readonly DowntimeReasonDto Fault = new(6, "MCH-FAULT", "Machine fault", "Unplanned", true, true);

    public List<LineDto> Lines { get; } = [L1, L2];
    public Dictionary<string, LineStatusDto> Status { get; } = [];
    public Dictionary<string, List<AndonDto>> Open { get; } = [];
    public Dictionary<int, List<MachineDto>> MachinesByWorkCenter { get; } = [];
    public int SnapshotLoads { get; private set; }
    public Exception? Fails { get; set; }
    public List<(int MachineId, string Type)> Raised { get; } = [];
    public Func<AndonDto, AndonDto> Acknowledge { get; set; } = a => a;
    public List<(long Id, int ReasonId)> Resolved { get; } = [];
    public RaiseAndonResult? RaiseAnswer { get; set; }

    public Task<IReadOnlyList<LineDto>> GetLinesAsync(CancellationToken ct = default) => Result<IReadOnlyList<LineDto>>(Lines);
    public Task<LineDto> GetLineAsync(int id, CancellationToken ct = default) => Result(Lines.Single(l => l.Id == id));

    // Lets a test check what was true when the snapshot was read (was the line group already joined?).
    public Action<string>? OnSnapshot { get; set; }

    public Task<LineStatusDto> GetLineStatusAsync(string lineCode, CancellationToken ct = default)
    {
        SnapshotLoads++;
        OnSnapshot?.Invoke(lineCode);
        return Result(Status.GetValueOrDefault(lineCode) ?? new LineStatusDto(lineCode, []));
    }

    public Task<IReadOnlyList<MachineDto>> GetMachinesAsync(int workCenterId, CancellationToken ct = default)
        => Result<IReadOnlyList<MachineDto>>(MachinesByWorkCenter.GetValueOrDefault(workCenterId) ?? []);

    public Task<IReadOnlyList<DowntimeReasonDto>> GetDowntimeReasonsAsync(CancellationToken ct = default)
        => Result<IReadOnlyList<DowntimeReasonDto>>([Fault]);

    public Task<IReadOnlyList<AndonDto>> GetOpenAndonsAsync(string lineCode, CancellationToken ct = default)
        => Result<IReadOnlyList<AndonDto>>(Open.GetValueOrDefault(lineCode) ?? []);

    public Task<RaiseAndonResult> RaiseAndonAsync(int machineId, string type, CancellationToken ct = default)
    {
        Raised.Add((machineId, type));
        return Result(RaiseAnswer ?? new RaiseAndonResult(Call(1, machineId, "M" + machineId, type, "Raised", 10), true));
    }

    public Task<AndonDto> AcknowledgeAndonAsync(AndonDto andon, CancellationToken ct = default) => Result(Acknowledge(andon));

    public Task<AndonDto> ResolveAndonAsync(AndonDto andon, int downtimeReasonId, string? note, CancellationToken ct = default)
    {
        Resolved.Add((andon.Id, downtimeReasonId));
        return Result(andon with { Status = "Resolved", DowntimeReasonId = downtimeReasonId, RowVersion = Version(Ver(andon) + 1) });
    }

    private Task<T> Result<T>(T value) => Fails is { } ex ? Task.FromException<T>(ex) : Task.FromResult(value);

    // ---------- builders ----------

    public static MachineStatusDto Machine(int id, string code, WireState? state = WireState.Running, DateTime? since = null,
        long? counter = 100, long thisHour = 10, string source = "Mqtt")
        => new(id, code, code + " name", "PickAndPlace", source, state, since ?? T0, counter, thisHour, T0, []);

    public static AndonDto Call(long id, int machineId, string machine, string type, string status, ulong version,
        int level = 0, string line = "L1", string? ackBy = null)
        => new(id, machineId, machine, line, type, status, "system", T0, ackBy, null, null, null, null, null, null, level, Version(version));

    public static string Version(ulong v)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, v);
        return Convert.ToBase64String(bytes);
    }

    public static ulong Ver(AndonDto a) => BinaryPrimitives.ReadUInt64BigEndian(Convert.FromBase64String(a.RowVersion));

    public static readonly DateTime T0 = new(2026, 10, 27, 2, 0, 0, DateTimeKind.Utc);

    public static ApiException Conflict() => new(HttpStatusCode.Conflict, "Andon 5 was changed by someone else (now Acknowledged).");
}
