using System.Globalization;

namespace MiniMes.Infrastructure.Persistence.Seed;

// Public holidays of the Labour Code 2019, article 112 (11 days), for seeding PlantCalendar.
// Sample data only: every year the government picks the exact Tet and National Day days, and the plant
// adds its own (company trip, swapped days) by hand on the calendar screen.
public static class VietnamHolidays
{
    // Lunar dates come from the Chinese lunisolar calendar. It is computed for UTC+8 and the Vietnamese one
    // for UTC+7; they differ on a new moon close to midnight (rare, e.g. Tet 1985), acceptable for demo data.
    private static readonly ChineseLunisolarCalendar Lunar = new();

    public static IReadOnlyList<(DateOnly Date, string Name)> For(int year)
    {
        var tet = LunarToSolar(year, 1, 1);
        var days = new List<(DateOnly, string)>
        {
            (new DateOnly(year, 1, 1), "New Year's Day"),
            // 5 days: the last day of the lunar year + the first 4 days of the new one.
            (tet.AddDays(-1), "Lunar New Year (Tet)"),
            (tet, "Lunar New Year (Tet)"),
            (tet.AddDays(1), "Lunar New Year (Tet)"),
            (tet.AddDays(2), "Lunar New Year (Tet)"),
            (tet.AddDays(3), "Lunar New Year (Tet)"),
            (LunarToSolar(year, 3, 10), "Hung Kings Commemoration"),
            (new DateOnly(year, 4, 30), "Reunification Day"),
            (new DateOnly(year, 5, 1), "International Labour Day"),
            (new DateOnly(year, 9, 1), "National Day"), // the adjacent day; the government may pick 3 Sep instead
            (new DateOnly(year, 9, 2), "National Day"),
        };
        return days;
    }

    // month is the lunar month number (1-12). A leap month repeats the month before it and takes the
    // next index in .NET (leap after month 2 -> GetLeapMonth = 3, real month 3 is index 4).
    private static DateOnly LunarToSolar(int year, int month, int day)
    {
        var leap = Lunar.GetLeapMonth(year);
        var index = leap > 0 && leap <= month ? month + 1 : month;
        return DateOnly.FromDateTime(Lunar.ToDateTime(year, index, day, 0, 0, 0, 0));
    }
}
