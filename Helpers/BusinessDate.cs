namespace HazelInvoice.Helpers;

public static class BusinessDate
{
    private static readonly Lazy<TimeZoneInfo> TimeZone = new(ResolveTimeZone);

    public static DateTime Now()
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZone.Value);

    public static DateTime Today()
    {
        return Now().Date;
    }

    public static DateTime Tomorrow()
        => Today().AddDays(1);

    public static DateTime NormalizeNextDeliveryDate(DateTime? value)
    {
        var tomorrow = Tomorrow();
        if (!value.HasValue || value.Value == default)
            return tomorrow;

        var selectedDate = value.Value.Date;
        return selectedDate < tomorrow ? tomorrow : selectedDate;
    }

    private static TimeZoneInfo ResolveTimeZone()
    {
        var ids = new[]
        {
            "Asia/Manila",
            "Singapore Standard Time"
        };

        foreach (var id in ids)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.Local;
    }
}
