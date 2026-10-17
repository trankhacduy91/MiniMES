namespace MiniMes.Domain.Machines;

// Delta = products made since the previous count message. IsBaseline: first value ever seen for the machine
// (only sets the reference, the history before it does not belong to this hour). IsReset: the counter went down.
public readonly record struct CounterDelta(long Delta, bool IsReset, bool IsBaseline);

// Spec M4 2.3: machines send a cumulative counter (like an odometer), not "+1". A lost message costs nothing:
// the next one carries the new total.
public static class CounterMath
{
    public static CounterDelta Compute(long? last, long current)
    {
        if (last is null) return new(0, false, true);
        if (current >= last) return new(current - last.Value, false, false);
        // Lower than before: the PLC restarted, the operator reset it at shift start, or a 16-bit register
        // wrapped at 65535. Since the reset the machine made `current` boards; those between the last message
        // and the reset are lost (accepted and documented).
        return new(current, true, false);
    }
}
