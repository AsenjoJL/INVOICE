using HazelInvoice.Data;
using HazelInvoice.Models;
using HazelInvoice.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace HazelInvoice.Services.Receipts;

public sealed class ReceiptQueryService : IReceiptQueryService
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;

    private readonly ApplicationDbContext _db;

    public ReceiptQueryService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<ReceiptsIndexViewModel> QueryAsync(ReceiptQueryOptions options, CancellationToken ct = default)
    {
        var page = options.Page < 1 ? 1 : options.Page;
        var pageSize = options.PageSize <= 0 ? DefaultPageSize : options.PageSize;
        if (pageSize > MaxPageSize) pageSize = MaxPageSize;

        var q = (options.Query ?? string.Empty).Trim();

        IQueryable<Receipt> baseQuery = _db.Receipts.AsNoTracking();

        if (options.UnpaidOnly)
        {
            baseQuery = baseQuery.Where(r => r.Status == PaymentStatus.Unpaid);
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            // Keep it simple + index-friendly: exact-ish receipt number match + contains on customer.
            // Postgres ILIKE for case-insensitive contains.
            baseQuery = baseQuery.Where(r =>
                EF.Functions.ILike(r.ReceiptNumber, $"%{q}%") ||
                EF.Functions.ILike(r.CustomerName, $"%{q}%"));
        }

        var totalCount = await baseQuery.CountAsync(ct);
        var skip = (page - 1) * pageSize;
        if (skip >= totalCount && totalCount > 0)
        {
            page = (int)Math.Ceiling(totalCount / (double)pageSize);
            skip = (page - 1) * pageSize;
        }

        var receipts = await baseQuery
            .OrderByDescending(r => r.Date)
            .ThenByDescending(r => r.Id)
            .Skip(skip)
            .Take(pageSize)
            .Select(r => new ReceiptListItemViewModel
            {
                Id = r.Id,
                ReceiptNumber = r.ReceiptNumber,
                Date = r.Date,
                CustomerName = r.CustomerName,
                Type = r.Type,
                TotalAmount = r.TotalAmount,
                PaidAmount = r.PaidAmount,
                Status = r.Status
            })
            .ToListAsync(ct);

        return new ReceiptsIndexViewModel
        {
            TotalCount = totalCount,
            Receipts = receipts,
            Query = q,
            Page = page,
            PageSize = pageSize,
            UnpaidOnly = options.UnpaidOnly
        };
    }
}

