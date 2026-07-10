using HazelInvoice.Models;
using HazelInvoice.Services;
using HazelInvoice.Services.Dashboard;
using HazelInvoice.Services.Orders;
using HazelInvoice.Services.Printing;
using HazelInvoice.Services.Pricing;
using HazelInvoice.Services.Receipts;
using HazelInvoice.Services.Reports;
using HazelInvoice.Services.Settings;
using HazelInvoice.Services.Caching;
using HazelInvoice.Services.Expenses;
using HazelInvoice.Data;
using HazelInvoice.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using System.Threading.RateLimiting;

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

builder.Services.AddDefaultIdentity<IdentityUser>(options =>
    {
        options.SignIn.RequireConfirmedAccount = false;
        options.Password.RequiredLength = 8;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;
        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.SlidingExpiration = true;
    options.ExpireTimeSpan = TimeSpan.FromHours(12);
    options.LoginPath = "/Identity/Account/Login";
    options.AccessDeniedPath = "/Identity/Account/Login";
});

var dataProtectionKeyDirectory = builder.Configuration["DataProtection:KeyDirectory"];
if (!builder.Environment.IsDevelopment() && !string.IsNullOrWhiteSpace(dataProtectionKeyDirectory))
{
    Directory.CreateDirectory(dataProtectionKeyDirectory);
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeyDirectory));
}

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        if (!context.HttpContext.Response.HasStarted)
        {
            context.HttpContext.Response.ContentType = "text/plain; charset=utf-8";
            await context.HttpContext.Response.WriteAsync("Too many requests. Please wait a moment and try again.", cancellationToken);
        }
    };

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
    {
        var ipAddress = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: $"global:{ipAddress}",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 240,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            });
    });

    options.AddPolicy("auth", httpContext =>
    {
        var ipAddress = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: $"auth:{ipAddress}",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            });
    });
});

builder.Services.AddScoped<HazelInvoice.Services.IReceiptService, HazelInvoice.Services.ReceiptService>();
builder.Services.AddScoped<IReceiptQueryService, ReceiptQueryService>();
builder.Services.AddSingleton<IAppCacheInvalidator, AppCacheInvalidator>();
builder.Services.AddScoped<ILookupCacheService, LookupCacheService>();
builder.Services.AddScoped<IExpenseCategoryCatalogService, ExpenseCategoryCatalogService>();
builder.Services.AddScoped<IDailyPurchaseCostService, DailyPurchaseCostService>();
builder.Services.AddScoped<IProductPricingService, ProductPricingService>();

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
builder.Services.Configure<OperationsOptions>(builder.Configuration.GetSection("Operations"));
builder.Services.Configure<BootstrapSeedOptions>(builder.Configuration.GetSection("BootstrapSeed"));
builder.Services.Configure<ExpenseCatalogOptions>(builder.Configuration.GetSection("ExpenseCatalog"));

// Orders / Vegetable matrix (keep controller thin / scalable)
builder.Services.AddScoped<IVegetableMatrixService, VegetableMatrixService>();
builder.Services.AddScoped<IVegetableMatrixTemplateService, VegetableMatrixTemplateService>();

// Reports
builder.Services.AddScoped<IProfitReportService, ProfitReportService>();

builder.Services.AddControllersWithViews(options =>
{
    options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
});
builder.Services.Configure<FormOptions>(options =>
{
    options.ValueCountLimit = 10000;
    options.KeyLengthLimit = 4096;
    options.ValueLengthLimit = 1024 * 1024;
});

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

app.Use(async (context, next) =>
{
    context.Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
    context.Response.Headers[HeaderNames.XFrameOptions] = "SAMEORIGIN";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    context.Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";

    const string csp = "default-src 'self'; " +
                       "base-uri 'self'; " +
                       "object-src 'none'; " +
                       "frame-ancestors 'self'; " +
                       "form-action 'self'; " +
                       "img-src 'self' data: https:; " +
                       "script-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net; " +
                       "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com https://cdn.jsdelivr.net; " +
                       "font-src 'self' data: https://fonts.gstatic.com https://cdn.jsdelivr.net; " +
                       "connect-src 'self';";

    context.Response.Headers["Content-Security-Policy"] = csp;

    await next();
});

app.UseRouting();
app.UseRateLimiter();

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
    var bootstrapSeed = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<BootstrapSeedOptions>>().Value;
    var expenseCatalog = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ExpenseCatalogOptions>>().Value;
    await DbInitializer.Initialize(context, bootstrapSeed, expenseCatalog);
}

app.Run();
