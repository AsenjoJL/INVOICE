using Microsoft.Extensions.Caching.Memory;

namespace HazelInvoice.Services.Caching;

public interface IAppCacheInvalidator
{
    void InvalidateProducts();
    void InvalidateCustomers();
    void InvalidateSuppliers();
    void InvalidateServices();
    void InvalidateWeeklyPrices();
    void InvalidatePartners();
    void InvalidateLaborers();
    void InvalidateDashboard();
    void InvalidateProfitReports();
}

public sealed class AppCacheInvalidator : IAppCacheInvalidator
{
    private readonly IMemoryCache _cache;
    private readonly object _sync = new();
    private readonly HashSet<string> _weeklyPriceKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _profitReportKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dashboardKeys = new(StringComparer.Ordinal);

    public AppCacheInvalidator(IMemoryCache cache)
    {
        _cache = cache;
    }

    public void TrackWeeklyPriceKey(string key)
    {
        lock (_sync)
        {
            _weeklyPriceKeys.Add(key);
        }
    }

    public void TrackProfitReportKey(string key)
    {
        lock (_sync)
        {
            _profitReportKeys.Add(key);
        }
    }

    public void TrackDashboardKey(string key)
    {
        lock (_sync)
        {
            _dashboardKeys.Add(key);
        }
    }

    public void InvalidateProducts() => _cache.Remove(AppCacheKeys.ActiveProducts);
    public void InvalidateCustomers() => _cache.Remove(AppCacheKeys.ActiveCustomers);
    public void InvalidateSuppliers() => _cache.Remove(AppCacheKeys.ActiveSuppliers);
    public void InvalidateServices() => _cache.Remove(AppCacheKeys.ActiveServices);
    public void InvalidateLaborers() => _cache.Remove(AppCacheKeys.ActiveLaborers);
    public void InvalidatePartners() => _cache.Remove(AppCacheKeys.PartnerNames);

    public void InvalidateWeeklyPrices()
    {
        lock (_sync)
        {
            foreach (var key in _weeklyPriceKeys)
                _cache.Remove(key);

            _weeklyPriceKeys.Clear();
        }
    }

    public void InvalidateProfitReports()
    {
        lock (_sync)
        {
            foreach (var key in _profitReportKeys)
                _cache.Remove(key);

            _profitReportKeys.Clear();
        }
    }

    public void InvalidateDashboard()
    {
        lock (_sync)
        {
            foreach (var key in _dashboardKeys)
                _cache.Remove(key);

            _dashboardKeys.Clear();
        }

        // Proactively bust today's and yesterday's dashboard cache by their known key
        // pattern, even if no request has registered the key via TrackDashboardKey yet.
        var today = DateTime.Today;
        _cache.Remove(AppCacheKeys.Dashboard(today));
        _cache.Remove(AppCacheKeys.Dashboard(today.AddDays(-1)));
    }
}
