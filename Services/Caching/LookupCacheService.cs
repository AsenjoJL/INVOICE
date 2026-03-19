using HazelInvoice.Data;
using HazelInvoice.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace HazelInvoice.Services.Caching;

public interface ILookupCacheService
{
    Task<IReadOnlyList<Product>> GetActiveProductsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Customer>> GetActiveCustomersAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Supplier>> GetActiveSuppliersAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Service>> GetActiveServicesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Laborer>> GetActiveLaborersAsync(CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetPartnerNamesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<WeeklyPrice>> GetWeeklyPricesForDayAsync(DateTime day, CancellationToken ct = default);
}

public sealed class LookupCacheService : ILookupCacheService
{
    private static readonly MemoryCacheEntryOptions ShortLookupCache = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
    };

    private readonly ApplicationDbContext _context;
    private readonly IMemoryCache _cache;
    private readonly IAppCacheInvalidator _cacheInvalidator;

    public LookupCacheService(
        ApplicationDbContext context,
        IMemoryCache cache,
        IAppCacheInvalidator cacheInvalidator)
    {
        _context = context;
        _cache = cache;
        _cacheInvalidator = cacheInvalidator;
    }

    public Task<IReadOnlyList<Product>> GetActiveProductsAsync(CancellationToken ct = default)
        => _cache.GetOrCreateAsync(AppCacheKeys.ActiveProducts, async entry =>
        {
            entry.SetOptions(ShortLookupCache);
            return (IReadOnlyList<Product>)await _context.Products
                .AsNoTracking()
                .Where(p => p.IsActive)
                .OrderBy(p => p.Name)
                .ToListAsync(ct);
        })!;

    public Task<IReadOnlyList<Customer>> GetActiveCustomersAsync(CancellationToken ct = default)
        => _cache.GetOrCreateAsync(AppCacheKeys.ActiveCustomers, async entry =>
        {
            entry.SetOptions(ShortLookupCache);
            return (IReadOnlyList<Customer>)await _context.Customers
                .AsNoTracking()
                .Where(c => c.IsActive)
                .OrderBy(c => c.Name)
                .ToListAsync(ct);
        })!;

    public Task<IReadOnlyList<Supplier>> GetActiveSuppliersAsync(CancellationToken ct = default)
        => _cache.GetOrCreateAsync(AppCacheKeys.ActiveSuppliers, async entry =>
        {
            entry.SetOptions(ShortLookupCache);
            return (IReadOnlyList<Supplier>)await _context.Suppliers
                .AsNoTracking()
                .Where(s => s.IsActive)
                .OrderBy(s => s.Name)
                .ToListAsync(ct);
        })!;

    public Task<IReadOnlyList<Service>> GetActiveServicesAsync(CancellationToken ct = default)
        => _cache.GetOrCreateAsync(AppCacheKeys.ActiveServices, async entry =>
        {
            entry.SetOptions(ShortLookupCache);
            return (IReadOnlyList<Service>)await _context.Services
                .AsNoTracking()
                .Where(s => s.IsActive)
                .OrderBy(s => s.Name)
                .ToListAsync(ct);
        })!;

    public Task<IReadOnlyList<Laborer>> GetActiveLaborersAsync(CancellationToken ct = default)
        => _cache.GetOrCreateAsync(AppCacheKeys.ActiveLaborers, async entry =>
        {
            entry.SetOptions(ShortLookupCache);
            return (IReadOnlyList<Laborer>)await _context.Laborers
                .AsNoTracking()
                .Where(l => l.IsActive)
                .OrderBy(l => l.FullName)
                .ToListAsync(ct);
        })!;

    public Task<IReadOnlyList<string>> GetPartnerNamesAsync(CancellationToken ct = default)
        => _cache.GetOrCreateAsync(AppCacheKeys.PartnerNames, async entry =>
        {
            entry.SetOptions(ShortLookupCache);
            return (IReadOnlyList<string>)await _context.PartnerBalanceConfigs
                .AsNoTracking()
                .OrderBy(p => p.PartnerName)
                .Select(p => p.PartnerName)
                .ToListAsync(ct);
        })!;

    public Task<IReadOnlyList<WeeklyPrice>> GetWeeklyPricesForDayAsync(DateTime day, CancellationToken ct = default)
    {
        var cacheKey = AppCacheKeys.WeeklyPricesForDay(day.Date);
        if (_cacheInvalidator is AppCacheInvalidator invalidator)
            invalidator.TrackWeeklyPriceKey(cacheKey);

        return _cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.SetAbsoluteExpiration(TimeSpan.FromMinutes(5));
            return (IReadOnlyList<WeeklyPrice>)await _context.WeeklyPrices
                .AsNoTracking()
                .Include(w => w.Product)
                .Where(w => w.EffectiveFrom <= day.Date && w.EffectiveTo >= day.Date)
                .OrderBy(w => w.Product!.Name)
                .ToListAsync(ct);
        })!;
    }
}
