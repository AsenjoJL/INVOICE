using HazelInvoice.Models;
using HazelInvoice.Services;
using HazelInvoice.Services.Dashboard;
using HazelInvoice.Services.Orders;
using HazelInvoice.Services.Printing;
using HazelInvoice.Services.Receipts;
using HazelInvoice.Services.Reports;
using HazelInvoice.Services.Settings;
using HazelInvoice.Services.Caching;
using HazelInvoice.Data;
using HazelInvoice.Configuration;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
AppContext.SetSwitch("Npgsql.DisableDateTimeInfinityConversions", true);

var builder = WebApplication.CreateBuilder(args);

// Optional local overrides (keep secrets out of git; highest precedence after env vars).
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);


// Add services to the container.
var envConn = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");
if (!string.IsNullOrEmpty(envConn))
{
    Console.WriteLine("Using environment variable for DB connection.");
}
else
{
    Console.WriteLine("Environment variable 'ConnectionStrings__DefaultConnection' is NULL or EMPTY. Using config.");
}

var connectionString = envConn 
                       ?? builder.Configuration.GetConnectionString("DefaultConnection") 
                       ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString,
        npgsqlOptions => npgsqlOptions.EnableRetryOnFailure()));
builder.Services.AddDatabaseDeveloperPageExceptionFilter();
builder.Services.AddMemoryCache();

builder.Services.AddDefaultIdentity<IdentityUser>(options => options.SignIn.RequireConfirmedAccount = false) // Simplified for ease of use
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>();

builder.Services.AddScoped<HazelInvoice.Services.IReceiptService, HazelInvoice.Services.ReceiptService>();
builder.Services.AddScoped<IReceiptQueryService, ReceiptQueryService>();
builder.Services.AddSingleton<IAppCacheInvalidator, AppCacheInvalidator>();
builder.Services.AddScoped<ILookupCacheService, LookupCacheService>();

// Dashboard metrics (keep controllers thin / scalable)
builder.Services.AddScoped<IDashboardMetricsService, DashboardMetricsService>();

// Printer Settings + Print orchestration
builder.Services.AddScoped<IAppSettingStore, DbAppSettingStore>();
builder.Services.AddSingleton<IPrinterCatalog, WindowsPrinterCatalog>();
builder.Services.AddSingleton<IPrinterSpooler, WindowsPrinterSpooler>();
builder.Services.AddScoped<PrinterSettingsService>();
builder.Services.AddScoped<IInvoicePrintManager, InvoicePrintManager>();

// Feature flags (safe defaults; can be extended later)
builder.Services.Configure<FeaturesOptions>(builder.Configuration.GetSection("Features"));

// Orders / Vegetable matrix (keep controller thin / scalable)
builder.Services.AddScoped<IVegetableMatrixService, VegetableMatrixService>();
builder.Services.AddScoped<IVegetableMatrixTemplateService, VegetableMatrixTemplateService>();

// Reports
builder.Services.AddScoped<IProfitReportService, ProfitReportService>();

builder.Services.AddControllersWithViews();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
});

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.MapRazorPages();

using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var context = services.GetRequiredService<ApplicationDbContext>();
    context.Database.Migrate();
    await DbInitializer.Initialize(context);
}

app.Run();
