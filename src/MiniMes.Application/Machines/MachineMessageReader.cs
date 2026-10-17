using System.Text.Json;
using MiniMes.Contracts.Machines;

namespace MiniMes.Application.Machines;

// What arrived on one topic, once the contract checks passed (spec M4 2.3). Status is the raw "online"/"offline"
// payload of the status topic (not JSON); every other kind carries a MachineMessage.
public sealed record MachineInput(ParsedTopic Topic, MachineMessage? Message, string? Status);

public sealed record MachineReadResult(MachineInput? Input, string? RejectReason)
{
    public bool IsAccepted => Input is not null;
}

// "Receive right, drop right": a pure function from (topic, payload) to an accepted input or a reason to drop it.
// Bad input is dropped and logged, never thrown: one broken device must not stop the ingest of all the others.
public static class MachineMessageReader
{
    public const string Online = "online";
    public const string Offline = "offline";

    private static readonly HashSet<string> Kinds =
        [MachineKinds.State, MachineKinds.Count, MachineKinds.Alarm, MachineKinds.Heartbeat, MachineKinds.Status, MachineKinds.Process];

    public static MachineReadResult Read(string topic, string payload)
    {
        if (!MachineTopic.TryParse(topic, out var t)) return Reject("topic is not minimes/{site}/{area}/{line}/{machine}/{kind}");
        if (!Kinds.Contains(t.Kind)) return Reject($"unknown kind '{t.Kind}'");

        if (t.Kind == MachineKinds.Status)
            return payload is Online or Offline
                ? new(new MachineInput(t, null, payload), null)
                : Reject($"status must be '{Online}' or '{Offline}'");

        MachineMessage? msg;
        try { msg = JsonSerializer.Deserialize<MachineMessage>(payload, MachineJson.Options); }
        catch (JsonException ex) { return Reject($"invalid JSON ({ex.Message})"); }
        if (msg is null) return Reject("empty payload");

        if (msg.MsgId == Guid.Empty) return Reject("msgId is missing");
        if (msg.Machine != t.Machine) return Reject($"machine '{msg.Machine}' does not match the topic");

        var error = t.Kind switch
        {
            // Offline is inferred by the server (no heartbeat, LWT); a machine claiming it is a bug on its side (spec 2.5).
            MachineKinds.State or MachineKinds.Heartbeat when msg.State is null => "state is missing",
            MachineKinds.State or MachineKinds.Heartbeat when msg.State == MachineState.Offline => "a machine cannot report Offline",
            MachineKinds.Count when msg.Counter is null => "counter is missing",
            MachineKinds.Count when msg.Counter < 0 => "counter is negative",
            MachineKinds.Process when msg.Tag != t.Tag => $"tag '{msg.Tag}' does not match the topic",
            MachineKinds.Process when msg.Value is null => "value is missing",
            _ => null,
        };
        return error is null ? new(new MachineInput(t, msg, null), null) : Reject(error);
    }

    private static MachineReadResult Reject(string reason) => new(null, reason);
}
