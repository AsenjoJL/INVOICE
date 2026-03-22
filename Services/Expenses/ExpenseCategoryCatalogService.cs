using HazelInvoice.Data;
using HazelInvoice.Configuration;
using HazelInvoice.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace HazelInvoice.Services.Expenses;

public interface IExpenseCategoryCatalogService
{
    Task<List<ExpenseCategoryDefinition>> GetDefinitionsAsync(CancellationToken ct = default);
    Task<Dictionary<string, ExpenseCategoryGroup>> GetGroupMapAsync(CancellationToken ct = default);
    Task UpsertCategoryAsync(string name, ExpenseCategoryGroup group, CancellationToken ct = default);
}

public sealed class ExpenseCategoryCatalogService : IExpenseCategoryCatalogService
{
    private const string CacheKey = "expense-category-definitions";

    private readonly ApplicationDbContext _context;
    private readonly IMemoryCache _cache;
    private readonly HashSet<string> _systemDefaultNames;

    public ExpenseCategoryCatalogService(
        ApplicationDbContext context,
        IMemoryCache cache,
        IOptions<ExpenseCatalogOptions> expenseCatalog)
    {
        _context = context;
        _cache = cache;
        _systemDefaultNames = (expenseCatalog.Value.Defaults ?? [])
            .Select(x => ExpenseCategoryCatalog.NormalizeName(x.Name))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<List<ExpenseCategoryDefinition>> GetDefinitionsAsync(CancellationToken ct = default)
    {
        if (_cache.TryGetValue(CacheKey, out List<ExpenseCategoryDefinition>? cached) && cached is not null)
        {
            return cached;
        }

        var definitions = await _context.ExpenseCategoryDefinitions
            .AsNoTracking()
            .OrderBy(x => x.Group)
            .ThenBy(x => x.Name)
            .ToListAsync(ct);

        _cache.Set(CacheKey, definitions, TimeSpan.FromMinutes(10));
        return definitions;
    }

    public async Task<Dictionary<string, ExpenseCategoryGroup>> GetGroupMapAsync(CancellationToken ct = default)
    {
        var definitions = await GetDefinitionsAsync(ct);
        return definitions.ToDictionary(
            item => ExpenseCategoryCatalog.NormalizeName(item.Name),
            item => item.Group,
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task UpsertCategoryAsync(string name, ExpenseCategoryGroup group, CancellationToken ct = default)
    {
        var normalized = ExpenseCategoryCatalog.NormalizeName(name);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        var existing = await _context.ExpenseCategoryDefinitions
            .FirstOrDefaultAsync(x => x.Name == normalized, ct);

        if (existing is null)
        {
            _context.ExpenseCategoryDefinitions.Add(new ExpenseCategoryDefinition
            {
                Name = normalized,
                Group = group,
                IsSystem = _systemDefaultNames.Contains(normalized)
            });
        }
        else if (existing.Group != group)
        {
            existing.Group = group;
        }

        await _context.SaveChangesAsync(ct);
        _cache.Remove(CacheKey);
    }
}
