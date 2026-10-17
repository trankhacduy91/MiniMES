namespace MiniMes.Application.Common;

// The plant's local time zone (config "Plant:TimeZone"). Data is stored in UTC; dates printed for people
// (WO number month, serial date) use the plant's calendar day, or a 00:30 serial would carry yesterday's date.
public sealed class PlantTimeZone(TimeZoneInfo zone)
{
    public TimeZoneInfo Zone { get; } = zone;

    public DateOnly DateOf(DateTime utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, Zone));
}
