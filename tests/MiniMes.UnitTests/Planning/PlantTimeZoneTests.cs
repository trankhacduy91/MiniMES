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
}
