namespace MiniMes.Application.Common;

// The plant's local time zone (config "Plant:TimeZone"). Data is stored in UTC; dates printed for people
// (WO number month, serial date) use the plant's calendar day, or a 00:30 serial would carry yesterday's date.
public sealed class PlantTimeZone(TimeZoneInfo zone)
{
    public TimeZoneInfo Zone { get; } = zone;

    public DateOnly DateOf(DateTime utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, Zone));

    // M4 step 8: start (UTC) of the plant's clock hour containing utc. "Output this hour" follows the clock on the
    // shop floor wall, which matters for zones with a half-hour offset (India +5:30).
    public DateTime HourStartUtc(DateTime utc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, Zone);
        var hour = new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(hour, Zone);
    }
}
