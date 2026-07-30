using HazelInvoice.Data;
using HazelInvoice.Helpers;
using HazelInvoice.Models;
using HazelInvoice.Services.Clients;
using HazelInvoice.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace HazelInvoice.Services.Receipts;

public sealed class ReceiptQueryService : IReceiptQueryService
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;
    private const string AllClientGroups = "All";

    private readonly ApplicationDbContext _db;
    private readonly IClientGroupService _clientGroups;

    public ReceiptQueryService(ApplicationDbContext db, IClientGroupService clientGroups)
    {
        _db = db;
        _clientGroups = clientGroups;
    }

    public async Task<ReceiptsIndexViewModel> QueryAsync(ReceiptQueryOptions options, CancellationToken ct = default)
    {
        var page = options.Page < 1 ? 1 : options.Page;
        var pageSize = options.PageSize <= 0 ? DefaultPageSize : options.PageSize;
        if (pageSize > MaxPageSize) pageSize = MaxPageSize;

        var q = (options.Query ?? string.Empty).Trim();
        var availableClientGroups = (await _clientGroups.GetClientGroupNamesAsync(ct)).ToList();
        var requestedClientGroup = (options.ClientGroup ?? string.Empty).Trim();
        var isAllClientGroups = string.IsNullOrWhiteSpace(requestedClientGroup) ||
                                string.Equals(requestedClientGroup, AllClientGroups, StringComparison.OrdinalIgnoreCase);
        var selectedClientGroup = isAllClientGroups
            ? AllClientGroups
            : await _clientGroups.ResolveClientGroupOrDefaultAsync(requestedClientGroup, ct);

        IQueryable<Receipt> baseQuery = _db.Receipts.AsNoTracking();

        if (!string.Equals(selectedClientGroup, AllClientGroups, StringComparison.OrdinalIgnoreCase))
        {
            var includedOutletGroups = await _clientGroups.ResolveOutletGroupsForClientAsync(selectedClientGroup, ct);
            baseQuery = baseQuery.Where(r =>
                (r.CustomerId.HasValue && _db.Customers.Any(c =>
                    c.Id == r.CustomerId.Value && includedOutletGroups.Contains(c.GroupName))) ||
                (!r.CustomerId.HasValue && _db.Customers.Any(c =>
                    c.Name == r.CustomerName && includedOutletGroups.Contains(c.GroupName))));
        }

        if (options.UnpaidOnly)
        {
            baseQuery = baseQuery.Where(r => r.PaidAmount <= 0m);
        }

        if (options.Date.HasValue)
        {
            var dayStart = options.Date.Value.Date;
            var dayEnd = dayStart.AddDays(1);
            baseQuery = baseQuery.Where(r => r.Date >= dayStart && r.Date < dayEnd);
        }
        else
        {
            var dateFrom = options.DateFrom?.Date;
            var dateTo = options.DateTo?.Date;

            if (dateFrom.HasValue && dateTo.HasValue && dateFrom > dateTo)
            {
                (dateFrom, dateTo) = (dateTo, dateFrom);
            }

            if (dateFrom.HasValue)
            {
                baseQuery = baseQuery.Where(r => r.Date >= dateFrom.Value);
            }

            if (dateTo.HasValue)
            {
                var endExclusive = dateTo.Value.AddDays(1);
                baseQuery = baseQuery.Where(r => r.Date < endExclusive);
            }
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

        foreach (var receipt in receipts)
            receipt.Status = ReceiptPaymentStatus.Resolve(receipt.TotalAmount, receipt.PaidAmount);

        return new ReceiptsIndexViewModel
        {
            TotalCount = totalCount,
            Receipts = receipts,
            Query = q,
            SelectedClientGroup = selectedClientGroup,
            AvailableClientGroups = availableClientGroups,
            Date = options.Date?.Date,
            DateFrom = options.Date.HasValue ? options.Date.Value.Date : options.DateFrom?.Date,
            DateTo = options.Date.HasValue ? options.Date.Value.Date : options.DateTo?.Date,
            Page = page,
            PageSize = pageSize,
            UnpaidOnly = options.UnpaidOnly
        };
    }
}
