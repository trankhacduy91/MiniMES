using System.Buffers.Binary;
using CommunityToolkit.Mvvm.ComponentModel;
using MiniMes.Client.Core.Resources;
using MiniMes.Contracts.Andon;
using MiniMes.Contracts.MasterData;

namespace MiniMes.Client.Core.ViewModels.Machines;

public static class AndonVersions
{
    // SQL Server rowversion: 8 bytes, big-endian, grows with every write of the database. Comparing two versions of
    // one call tells which is newer, so a late push or an old snapshot never rolls a row back.
    public static ulong Of(AndonDto dto)
    {
        var bytes = Convert.FromBase64String(dto.RowVersion);
        return bytes.Length == 8 ? BinaryPrimitives.ReadUInt64BigEndian(bytes) : 0;
    }

    public static bool IsOpen(AndonDto dto) => dto.Status is "Raised" or "Acknowledged";
}

// One open call in the dashboard's Andon list. Acknowledge / Resolve live on the dashboard (they call the API);
// the row only holds the call and the reason the supervisor picked for it.
public partial class AndonRowViewModel(AndonDto dto, TimeProvider time) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status), nameof(AckText), nameof(Level), nameof(IsEscalated), nameof(IsRaised))]
    private AndonDto _dto = dto;

    // Picked in the row's ComboBox; Resolve refuses without it (M5 builds the stop Pareto from it).
    [ObservableProperty]
    private DowntimeReasonDto? _selectedReason;

    public long Id => Dto.Id;
    public string Machine => Dto.Machine;
    public string Type => Dto.Type;
    public string RaisedBy => Dto.RaisedBy;
    public string RaisedAt => ToLocal(Dto.RaisedAt, time).ToString("HH:mm:ss");
    public string Status => Dto.Status;
    public bool IsRaised => Dto.Status == "Raised";
    public string AckText => Dto.AckBy is { } by ? string.Format(Strings.Dashboard_AckBy, by) : string.Empty;
    public int Level => Dto.EscalationLevel;

    // [v2] Level >= 1: nobody acknowledged in time; the view makes the row blink red.
    public bool IsEscalated => Dto.EscalationLevel >= 1;

    // false = older than what is shown (ignored).
    public bool TryApply(AndonDto newer)
    {
        if (AndonVersions.Of(newer) < AndonVersions.Of(Dto)) return false;
        Dto = newer;
        return true;
    }

    // The API sends UTC without "Z" (Kind Unspecified): read it as UTC, show it on the plant PC's clock.
    public static DateTime ToLocal(DateTime utc, TimeProvider time)
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), time.LocalTimeZone);
}
