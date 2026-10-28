using MiniMes.MachineSimulator.Config;

namespace MiniMes.MachineSimulator.Machines;

// Spec M4b step 1: the peak zone of the reflow oven. One reading per second = set point + uniform noise + drift.
// Drift is what the SPC of M7 has to catch: "drift L1-REFLOW-01 0.3" = a weak cooling fan, +0.3 °C per minute.
// Its own seeded Random: the same seed gives the same curve, and the machine's Down/NG draws stay as they were in M4.
public sealed class OvenTemperature(OpcUaOptions cfg, Random rng)
{
    private static readonly TimeSpan Period = TimeSpan.FromSeconds(1);

    private double _driftPerMin, _offset;
    private DateTimeOffset _nextAt = DateTimeOffset.MinValue;

    public double Value { get; private set; } = cfg.PeakTempC;
    public double DriftPerMin => _driftPerMin;
    public bool SensorOk { get; set; } = true;   // "sensor <m> bad": the value goes on, its quality becomes BadSensorFailure

    // null = drift off and back to the set point (the fan was fixed).
    public void SetDrift(double? perMin)
    {
        _driftPerMin = perMin ?? 0;
        if (perMin is null) _offset = 0;
    }

    // Called every tick; returns true when a new reading was taken.
    public bool Update(DateTimeOffset now)
    {
        if (now < _nextAt) return false;
        if (_nextAt != DateTimeOffset.MinValue) _offset += _driftPerMin * Period.TotalMinutes;
        _nextAt = now + Period;
        var noise = (rng.NextDouble() * 2 - 1) * cfg.NoiseC;
        Value = Math.Round(cfg.PeakTempC + _offset + noise, 2);   // a thermocouple reading, not 15 decimals
        return true;
    }
}
