using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MiniMes.Client.Core.Resources;
using MiniMes.Contracts.Machines;
using MiniMes.Contracts.Realtime;

namespace MiniMes.Client.Core.ViewModels.Machines;

// [v2] Latest reading of one process tag on a tile; Bad is shown greyed, not hidden.
public sealed record ProcessReadingRow(string Tag, string Text, bool IsBad, DateTime SourceTs);

// One machine on the line dashboard (spec M4 step 8): coloured by state, output of this hour, latest process values
// and where the data comes from. Filled from the REST snapshot, then kept current by pushes. A push older than what
// is shown (it was on its way while the snapshot loaded) is ignored, never allowed to roll the tile back.
public partial class MachineTileViewModel : ObservableObject
{
    public MachineTileViewModel(MachineStatusDto s)
    {
        Id = s.Id;
        Code = s.Code;
        Name = s.Name;
        Type = s.Type;
        SourceLabel = Strings.ResourceManager.GetString("Source_" + s.DataSource, Strings.Culture) ?? s.DataSource;
        _state = s.State;
        _stateSince = s.StateSince;
        _counter = s.Counter;
        _countThisHour = s.CountThisHour;
        _lastSeenAt = s.LastSeenAt;
        foreach (var p in s.Process) Process.Add(Row(p.Tag, p.Value, p.Unit, p.Quality, p.SourceTs));
    }

    public int Id { get; }
    public string Code { get; }
    public string Name { get; }
    public string Type { get; }
    public string SourceLabel { get; }   // [v2] MQTT / OPC UA / Modbus (Machine.DataSource)

    // The view colours the tile with a DataTrigger on this value.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private MachineState? _state;

    [ObservableProperty]
    private DateTime? _stateSince;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CounterText))]
    private long? _counter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HourText))]
    private long _countThisHour;

    [ObservableProperty]
    private DateTime? _lastSeenAt;

    public ObservableCollection<ProcessReadingRow> Process { get; } = [];

    public string StateText => State?.ToString() ?? Strings.Dashboard_NoData;

    public string HourText => string.Format(CultureInfo.InvariantCulture, Strings.Dashboard_ThisHour, CountThisHour);

    public string CounterText => Counter is { } c ? string.Format(CultureInfo.InvariantCulture, Strings.Dashboard_Counter, c) : string.Empty;

    public void Apply(MachineStateChangedDto d)
    {
        if (StateSince is { } shown && d.Since < shown) return;
        State = d.State;
        StateSince = d.Since;
    }

    // Absolute values (spec 2.7): the newest push simply replaces what is shown.
    public void Apply(MachineCounterDto d)
    {
        if (LastSeenAt is { } shown && d.LastSeenAt < shown) return;
        Counter = d.Counter;
        CountThisHour = d.CountThisHour;
        LastSeenAt = d.LastSeenAt;
    }

    public void Apply(MachineProcessDto d)
    {
        var row = Row(d.Tag, d.Value, d.Unit, d.Quality, d.SourceTs);
        var i = Process.ToList().FindIndex(p => p.Tag == d.Tag);
        if (i < 0) Process.Add(row);
        else if (d.SourceTs >= Process[i].SourceTs) Process[i] = row;
    }

    private static ProcessReadingRow Row(string tag, double value, string unit, DataQuality quality, DateTime sourceTs)
        => new(tag, string.Format(CultureInfo.InvariantCulture, "{0} {1:0.#} {2}", tag, value, unit), quality == DataQuality.Bad, sourceTs);
}
