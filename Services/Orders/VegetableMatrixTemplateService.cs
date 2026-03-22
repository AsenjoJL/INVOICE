using HazelInvoice.Data;
using HazelInvoice.Configuration;
using HazelInvoice.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HazelInvoice.Services.Orders;

public sealed class VegetableMatrixTemplateService : IVegetableMatrixTemplateService
{
    private readonly ApplicationDbContext _context;
    private readonly HashSet<string> _outletGroups;

    public VegetableMatrixTemplateService(ApplicationDbContext context, IOptions<OperationsOptions> operations)
    {
        _context = context;
        _outletGroups = (operations.Value.OutletGroups ?? []).Where(g => !string.IsNullOrWhiteSpace(g))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<byte[]> BuildTemplateAsync(DateTime date, CancellationToken cancellationToken = default)
    {
        // Keep template compatible with ImportVegetableMatrixExcel:
        // - Header row must have "Vegetables" in column A.
        // - Outlet headers should match Customer.Name values.
        var outlets = await _context.Customers
            .AsNoTracking()
            .Where(c => c.IsActive && c.GroupName != null && _outletGroups.Contains(c.GroupName))
            .OrderBy(c => c.GroupName)
            .ThenBy(c => c.Name)
            .Select(c => c.Name)
            .ToListAsync(cancellationToken);

        var products = await _context.Products
            .AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.Name)
            .Select(p => p.Name)
            .ToListAsync(cancellationToken);

        // Rows: title row, header row, product rows.
        var rows = new List<IReadOnlyList<string>>(capacity: 2 + products.Count);

        rows.Add(new[]
        {
            "VEGETABLE ORDER MATRIX TEMPLATE",
            $"Delivery Date: {date:yyyy-MM-dd}"
        });

        var header = new List<string>(capacity: 2 + outlets.Count)
        {
            "Vegetables",
            "Price"
        };
        header.AddRange(outlets);
        rows.Add(header);

        foreach (var name in products)
        {
            // Keep columns A/B only; outlet qty cells intentionally blank.
            rows.Add(new[] { name ?? string.Empty, string.Empty });
        }

        return SimpleXlsxWriter.WriteSingleSheet("Template", rows);
    }
}
