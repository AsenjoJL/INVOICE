namespace HazelInvoice.Helpers;

public static class WeeklyPriceCalendar
{
    private const DayOfWeek WeeklyResetDay = DayOfWeek.Saturday;
    private const DayOfWeek WeeklyCutoffDay = DayOfWeek.Friday;
    private static readonly TimeSpan FridayResetTime = TimeSpan.FromHours(23);

    public static bool IsResetDay(DateTime date)
    {
        var day = date.DayOfWeek;
        if (day == WeeklyResetDay)
            return true;

        return day == WeeklyCutoffDay && date.TimeOfDay >= FridayResetTime;
    }

    public static DateTime? GetApplicablePriceDate(DateTime date)
        => IsResetDay(date) ? null : date.Date;

    public static (DateTime WeekStart, DateTime WeekEnd) GetWeekRange(DateTime targetDate)
    {
        var day = targetDate.Date;
        if (IsResetDay(targetDate))
        {
            // Friday 11:00 PM onward is treated as the upcoming pricing cycle,
            // while Saturday remains the full reset day before Sunday starts.
            var nextSunday = day.DayOfWeek == WeeklyResetDay
                ? day.AddDays(1)
                : day.AddDays(2);
            return (nextSunday, nextSunday.AddDays(5));
        }

        var diff = (7 + (day.DayOfWeek - DayOfWeek.Sunday)) % 7;
        var weekStart = day.AddDays(-diff).Date;
        return (weekStart, weekStart.AddDays(5).Date);
    }
}
