using MiniMes.Contracts.Machines;

namespace MiniMes.EdgeGateway.Drivers;

// Spec M4b 2.1: one driver per machine; a new protocol (S7, MC Protocol...) = one more class, the pipeline does not change.
// Unlike the spec's two events, a driver writes everything into ONE channel (DeviceEvents): the last value read and
// "connection lost" keep their order, and the library's thread only does a TryWrite, never an await.
public interface IDeviceDriver
{
    string Machine { get; }
    string Protocol { get; }

    // Connects, reads, reconnects with backoff on its own; returns only when ct is cancelled.
    Task RunAsync(CancellationToken ct);
}

// What every driver produces, whatever the protocol.
public abstract record DeviceEvent(string Machine);

// One raw reading: Value as the device gave it (double, uint, int...), Quality from the OPC UA StatusCode or the Modbus
// read (rule G8), SourceTsUtc = when it was measured (rule G9).
public sealed record RawValue(string Machine, string Tag, object? Value, DataQuality Quality, DateTime SourceTsUtc, string Source)
    : DeviceEvent(Machine);

// The gateway lost or regained the machine (not the machine stopping: rule G7).
public sealed record ConnectionChanged(string Machine, bool Connected, string? Reason = null) : DeviceEvent(Machine);
