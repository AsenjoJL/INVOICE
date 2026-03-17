using HazelInvoice.Data;
using HazelInvoice.Services.Reports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// Quick verification tool: checks Profit & Sales math for "today" against basic invariants.
// Usage:
//   dotnet run --project scripts/ProfitCheck/ProfitCheck.csproj

AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
AppContext.SetSwitch("Npgsql.DisableDateTimeInfinityConversions", true);

var config = new ConfigurationBuilder()
    .SetBasePath(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")))
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.Development.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

var envConn = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");
var conn = envConn ?? config.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(conn))
{
    Console.Error.WriteLine("Missing connection string. Set ConnectionStrings__DefaultConnection or appsettings.json ConnectionStrings:DefaultConnection");
    return 2;
}

var services = new ServiceCollection();
services.AddLogging(b => b.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
}).SetMinimumLevel(LogLevel.Information));

services.AddDbContext<ApplicationDbContext>(o => o.UseNpgsql(conn));
services.AddScoped<IProfitReportService, ProfitReportService>();

await using var sp = services.BuildServiceProvider();
await using var scope = sp.CreateAsyncScope();

var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
var report = scope.ServiceProvider.GetRequiredService<IProfitReportService>();

var today = DateTime.Today;
var options = new ProfitReportQueryOptions(
    StartDate: today,
    EndDate: today,
    IncludeUnpaid: true,
    PercentFee: 1.0m,
    Partner1SharePercent: 40m
);

var vm = await report.BuildAsync(options);

// Invariants: receipt totals match lines, line Amount matches Qty*Price (within cent rounding).
var start = today.Date;
var endExclusive = today.Date.AddDays(1);

var receipts = await db.Receipts
    .AsNoTracking()
    .Include(r => r.Lines)
    .Where(r => r.Date >= start && r.Date < endExclusive && r.Status != HazelInvoice.Models.PaymentStatus.Void)
    .ToListAsync();

var receiptMismatch = 0;
foreach (var r in receipts)
{
    var sum = r.Lines.Sum(l => l.Amount);
    if (sum != r.TotalAmount)
    {
        receiptMismatch++;
    }
}

var lineMismatch = 0;
foreach (var l in receipts.SelectMany(r => r.Lines))
{
    var computed = Math.Round(l.Quantity * l.Price, 2, MidpointRounding.AwayFromZero);
    if (computed != l.Amount)
    {
        lineMismatch++;
    }
}

Console.WriteLine($"PROFIT CHECK (today={today:yyyy-MM-dd})");
Console.WriteLine($"Receipts: {vm.TotalReceiptCount}");
Console.WriteLine($"Sales: ₱{vm.TotalGrossSales:N2}");
Console.WriteLine($"Fees: ₱{vm.TotalFees:N2}");
Console.WriteLine($"Gross Profit: ₱{vm.TotalGrossProfit:N2}");
Console.WriteLine($"Deductions: ₱{vm.TotalDeductions:N2}");
Console.WriteLine($"Expenses: ₱{vm.TotalExpenses:N2}");
Console.WriteLine($"Capital Fund: ₱{vm.TotalCapitalFund:N2}");
Console.WriteLine($"Net Profit: ₱{vm.NetProfit:N2}");
Console.WriteLine();
Console.WriteLine($"Invariant checks:");
Console.WriteLine($"- Receipts with TotalAmount != sum(Lines.Amount): {receiptMismatch}");
Console.WriteLine($"- Lines with Amount != round(Qty*Price,2): {lineMismatch}");

return 0;
