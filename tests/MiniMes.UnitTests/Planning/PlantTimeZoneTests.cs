using MiniMes.Application.Common;

namespace MiniMes.UnitTests.Planning;

// Spec M2 1.5 #6: a serial made at 00:30 in the plant must carry the plant's date, not yesterday's UTC date.
public class PlantTimeZoneTests
{
    private static readonly PlantTimeZone Vietnam = new(TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"));

    [Theory]
    [InlineData("2026-09-30T17:30:00Z", "2026-10-01")]   // 00:30 on 1 Oct in Vietnam
    [InlineData("2026-09-30T16:59:59Z", "2026-09-30")]   // 23:59:59 on 30 Sep
    [InlineData("2026-10-01T10:00:00Z", "2026-10-01")]
    public void Plant_date_follows_the_plant_clock(string utc, string expected)
    {
        var instant = DateTime.Parse(utc, null, System.Globalization.DateTimeStyles.AdjustToUniversal);

        Assert.Equal(DateOnly.Parse(expected), Vietnam.DateOf(instant));
    }

    // M4 step 8: "output this hour" follows the wall clock of the plant. India (+5:30) shows why hours are not UTC hours.
    [Theory]
    [InlineData("Asia/Ho_Chi_Minh", "2026-10-17T01:10:00Z", "2026-10-17T01:00:00Z")]   // 08:10 -> 08:00
    [InlineData("Asia/Ho_Chi_Minh", "2026-10-17T16:59:59Z", "2026-10-17T16:00:00Z")]   // 23:59:59 -> 23:00
    [InlineData("Asia/Kolkata", "2026-10-17T01:10:00Z", "2026-10-17T00:30:00Z")]       // 06:40 -> 06:00
    [InlineData("Asia/Kolkata", "2026-10-17T01:29:59Z", "2026-10-17T00:30:00Z")]       // 06:59:59 -> 06:00
    [InlineData("Asia/Kolkata", "2026-10-17T01:30:00Z", "2026-10-17T01:30:00Z")]       // 07:00 -> 07:00
    public void Hour_start_follows_the_plant_clock(string zone, string utc, string expected)
    {
        var plant = new PlantTimeZone(TimeZoneInfo.FindSystemTimeZoneById(zone));
        static DateTime Utc(string s) => DateTime.Parse(s, null, System.Globalization.DateTimeStyles.AdjustToUniversal);

        Assert.Equal(Utc(expected), plant.HourStartUtc(Utc(utc)));
    }
}
