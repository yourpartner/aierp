using System.Collections.Concurrent;

namespace Server.Infrastructure;

/// <summary>
/// Lightweight Japanese holiday calculator (1980-2099) covering Happy Monday, substitute, and bridge holidays.
/// </summary>
public sealed class JapaneseHolidayService
{
    public static JapaneseHolidayService Instance { get; } = new();

    private readonly ConcurrentDictionary<int, HashSet<DateOnly>> _cache = new();

    private JapaneseHolidayService() { }

    public bool IsNationalHoliday(DateOnly date)
    {
        var set = GetHolidays(date.Year);
        return set.Contains(date);
    }

    public IReadOnlyCollection<DateOnly> GetHolidays(int year)
    {
        return _cache.GetOrAdd(year, BuildHolidays);
    }

    private HashSet<DateOnly> BuildHolidays(int year)
    {
        var holidays = new HashSet<DateOnly>();

        void Add(int month, int day)
        {
            holidays.Add(new DateOnly(year, month, day));
        }

        // Fixed-date holidays
        Add(1, 1);   // New Year's Day
        Add(2, 11);  // National Foundation Day
        if (year >= 2020) Add(2, 23); // Emperor's Birthday (Reiwa era)
        Add(4, 29);  // Showa Day
        Add(5, 3);   // Constitution Memorial Day
        Add(5, 4);   // Greenery Day
        Add(5, 5);   // Children's Day
        Add(8, 11);  // Mountain Day
        Add(11, 3);  // Culture Day
        Add(11, 23); // Labor Thanksgiving Day

        // Happy Monday holidays
        holidays.Add(NthWeekday(year, 1, DayOfWeek.Monday, 2));  // Coming of Age Day
        holidays.Add(NthWeekday(year, 7, DayOfWeek.Monday, 3));  // Marine Day
        holidays.Add(NthWeekday(year, 9, DayOfWeek.Monday, 3));  // Respect for the Aged Day
        holidays.Add(NthWeekday(year, 10, DayOfWeek.Monday, 2)); // Sports Day

        // Equinox days
        holidays.Add(new DateOnly(year, 3, CalcVernalEquinoxDay(year)));
        holidays.Add(new DateOnly(year, 9, CalcAutumnEquinoxDay(year)));

        // Note: Marine/Sports Day adjustments in 2020/2021 are ignored; we use the standard Happy Monday schedule.

        // Bridge holiday between Respect for the Aged Day and Autumn Equinox handled later.

        // Substitute holidays (holiday falls on Sunday → next weekday)
        var baseHolidays = holidays.ToList();
        baseHolidays.Sort();
        foreach (var holiday in baseHolidays)
        {
            if (holiday.DayOfWeek != DayOfWeek.Sunday) continue;
            var substitute = holiday.AddDays(1);
            while (holidays.Contains(substitute))
            {
                substitute = substitute.AddDays(1);
            }
            holidays.Add(substitute);
        }

        // Bridge holidays (weekday sandwiched between two holidays)
        var ordered = holidays.ToList();
        ordered.Sort();
        for (int i = 0; i < ordered.Count - 1; i++)
        {
            var prev = ordered[i];
            var next = ordered[i + 1];
            if (next.DayNumber - prev.DayNumber == 2)
            {
                var between = prev.AddDays(1);
                if (between.DayOfWeek != DayOfWeek.Sunday)
                {
                    holidays.Add(between);
                }
            }
        }

        return holidays;
    }

    private static DateOnly NthWeekday(int year, int month, DayOfWeek dayOfWeek, int occurrence)
    {
        var first = new DateOnly(year, month, 1);
        int offset = ((int)dayOfWeek - (int)first.DayOfWeek + 7) % 7;
        var day = 1 + offset + (occurrence - 1) * 7;
        return new DateOnly(year, month, day);
    }

    private static int CalcVernalEquinoxDay(int year)
    {
        // Approximation formula for 1980-2099
        return (int)Math.Floor(20.8431 + 0.242194 * (year - 1980)) - (int)Math.Floor((year - 1980) / 4.0);
    }

    private static int CalcAutumnEquinoxDay(int year)
    {
        return (int)Math.Floor(23.2488 + 0.242194 * (year - 1980)) - (int)Math.Floor((year - 1980) / 4.0);
    }
}

