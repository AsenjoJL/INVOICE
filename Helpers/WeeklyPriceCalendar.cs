namespace HazelInvoice.Helpers;

public static class WeeklyPriceCalendar
{
    public static bool IsResetDay(DateTime date)
        => date.Date.DayOfWeek == DayOfWeek.Saturday;

    public static DateTime? GetApplicablePriceDate(DateTime date)
        => IsResetDay(date) ? null : date.Date;

    public static (DateTime WeekStart, DateTime WeekEnd) GetWeekRange(DateTime targetDate)
    {
        var day = targetDate.Date;
        if (day.DayOfWeek == DayOfWeek.Saturday)
        {
            var nextSunday = day.AddDays(1);
            return (nextSunday, nextSunday.AddDays(5));
        }

        var diff = (7 + (day.DayOfWeek - DayOfWeek.Sunday)) % 7;
        var weekStart = day.AddDays(-diff).Date;
        return (weekStart, weekStart.AddDays(5).Date);
    }
}
