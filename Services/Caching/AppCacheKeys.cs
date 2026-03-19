namespace HazelInvoice.Services.Caching;

public static class AppCacheKeys
{
    public const string ActiveProducts = "lookup:products:active";
    public const string ActiveCustomers = "lookup:customers:active";
    public const string ActiveSuppliers = "lookup:suppliers:active";
    public const string ActiveServices = "lookup:services:active";
    public const string ActiveLaborers = "lookup:laborers:active";
    public const string PartnerNames = "lookup:partners:names";

    public static string WeeklyPricesForDay(DateTime day) => $"lookup:weeklyprices:{day:yyyyMMdd}";
    public static string Dashboard(DateTime day) => $"dashboard:{day:yyyyMMdd}";
    public static string ProfitReport(DateTime startDate, DateTime endDate, bool includeUnpaid, decimal percentFee, decimal partner1SharePercent)
        => $"profit:{startDate:yyyyMMdd}:{endDate:yyyyMMdd}:{includeUnpaid}:{percentFee}:{partner1SharePercent}";
}
