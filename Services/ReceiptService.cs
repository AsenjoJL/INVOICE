using System.Threading;
using HazelInvoice.Data;
using HazelInvoice.Helpers;
using HazelInvoice.Models;
using HazelInvoice.Services.Caching;
using Microsoft.EntityFrameworkCore;

namespace HazelInvoice.Services;

public interface IReceiptService
{
    Task<string> GenerateNextReceiptNumberAsync();
    Task<bool> DeleteReceiptAsync(int id, string? deletedBy = null, CancellationToken ct = default);
    Task<bool> RecordReceiptPaymentAsync(int id, decimal amount, string? recordedBy = null, CancellationToken ct = default);
    Task<bool> MarkReceiptPaidAsync(int id, string? recordedBy = null, CancellationToken ct = default);
    Task<int> MarkReceiptsPaidAsync(IEnumerable<int> ids, string? recordedBy = null, CancellationToken ct = default);
}

public class ReceiptService : IReceiptService
{
    private readonly ApplicationDbContext _context;
    private readonly IAppCacheInvalidator _cacheInvalidator;
    private const int StartingReceiptNumber = 8000;

    public ReceiptService(ApplicationDbContext context, IAppCacheInvalidator cacheInvalidator)
    {
        _context = context;
        _cacheInvalidator = cacheInvalidator;
    }

    public async Task<bool> DeleteReceiptAsync(int id, string? deletedBy = null, CancellationToken ct = default)
    {
        var strategy = _context.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _context.Database.BeginTransactionAsync(ct);

            var receipt = await _context.Receipts
                .Include(r => r.Lines)
                .Include(r => r.Payments)
                .FirstOrDefaultAsync(r => r.Id == id, ct);

            if (receipt == null)
            {
                await transaction.RollbackAsync(ct);
                return false;
            }

            // Remove any stock movements tied to this receipt number
            var stockMoves = await _context.ProductStockMovements
                .Where(m => m.Reference == receipt.ReceiptNumber)
                .ToListAsync(ct);
            _context.ProductStockMovements.RemoveRange(stockMoves);

            _context.Payments.RemoveRange(receipt.Payments);
            _context.ReceiptLines.RemoveRange(receipt.Lines);
            _context.Receipts.Remove(receipt);

            await _context.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return true;
        });
    }

    public async Task<string> GenerateNextReceiptNumberAsync()
    {
        int year = DateTime.Now.Year;

        // If a transaction is already active, do not start a new one.
        if (_context.Database.CurrentTransaction != null)
        {
            return await GenerateNextNumberInternal(year);
        }

        var strategy = _context.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var receiptNumber = await GenerateNextNumberInternal(year);
                await transaction.CommitAsync();
                return receiptNumber;
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        });
    }

    private async Task<string> GenerateNextNumberInternal(int year)
    {
        var sequence = await _context.ReceiptSequences
            .FirstOrDefaultAsync(s => s.Year == year);

        if (sequence == null)
        {
            // Initialize to one before the starting number, so the first generated number is StartingReceiptNumber.
            sequence = new ReceiptSequence { Year = year, LastNumber = StartingReceiptNumber - 1 };
            _context.ReceiptSequences.Add(sequence);
            await _context.SaveChangesAsync();
        }

        // If the existing sequence is below our configured starting number, jump it forward.
        // This allows changing the receipt start number without requiring DB edits/migrations.
        if (sequence.LastNumber < StartingReceiptNumber - 1)
        {
            sequence.LastNumber = StartingReceiptNumber - 1;
        }

        sequence.LastNumber++;
        await _context.SaveChangesAsync();

        return sequence.LastNumber.ToString();
    }

    public async Task<bool> RecordReceiptPaymentAsync(int id, decimal amount, string? recordedBy = null, CancellationToken ct = default)
    {
        var strategy = _context.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _context.Database.BeginTransactionAsync(ct);

            var receipt = await _context.Receipts
                .Include(r => r.Payments)
                .FirstOrDefaultAsync(r => r.Id == id, ct);

            if (receipt == null || receipt.Status == PaymentStatus.Void)
            {
                await transaction.RollbackAsync(ct);
                return false;
            }

            if (!TryApplyPayment(receipt, amount, recordedBy, out var payment))
            {
                await transaction.RollbackAsync(ct);
                return false;
            }

            _context.Payments.Add(payment);
            await _context.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            InvalidateMetrics();
            return true;
        });
    }

    public async Task<bool> MarkReceiptPaidAsync(int id, string? recordedBy = null, CancellationToken ct = default)
    {
        var updated = await MarkReceiptsPaidAsync([id], recordedBy, ct);
        if (updated > 0)
            return true;

        return await _context.Receipts
            .AsNoTracking()
            .AnyAsync(r => r.Id == id, ct);
    }

    public async Task<int> MarkReceiptsPaidAsync(IEnumerable<int> ids, string? recordedBy = null, CancellationToken ct = default)
    {
        var receiptIds = ids
            .Distinct()
            .ToList();

        if (receiptIds.Count == 0)
            return 0;

        var strategy = _context.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _context.Database.BeginTransactionAsync(ct);

            var receipts = await _context.Receipts
                .Include(r => r.Payments)
                .Where(r => receiptIds.Contains(r.Id) && r.Status != PaymentStatus.Void)
                .ToListAsync(ct);

            if (receipts.Count == 0)
            {
                await transaction.RollbackAsync(ct);
                return 0;
            }

            var paymentTimestamp = BusinessDate.Now();
            var recorder = recordedBy ?? "System";
            var payments = new List<Payment>();

            foreach (var receipt in receipts)
            {
                var remainingBalance = GetRemainingBalance(receipt);
                if (remainingBalance <= 0m)
                    continue;

                receipt.PaidAmount += remainingBalance;
                receipt.Status = PaymentStatus.Paid;

                payments.Add(new Payment
                {
                    ReceiptId = receipt.Id,
                    Date = paymentTimestamp,
                    Amount = remainingBalance,
                    Method = PaymentMethod.Cash,
                    RecordedById = recorder
                });
            }

            if (payments.Count == 0)
            {
                await transaction.RollbackAsync(ct);
                return 0;
            }

            _context.Payments.AddRange(payments);
            await _context.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            InvalidateMetrics();
            return payments.Count;
        });
    }

    private static bool TryApplyPayment(Receipt receipt, decimal amount, string? recordedBy, out Payment payment)
    {
        payment = default!;

        var normalizedAmount = NormalizePaymentAmount(amount, receipt);
        if (normalizedAmount <= 0m)
            return false;

        receipt.PaidAmount += normalizedAmount;
        receipt.Status = receipt.PaidAmount >= receipt.TotalAmount
            ? PaymentStatus.Paid
            : PaymentStatus.Partial;

        payment = new Payment
        {
            ReceiptId = receipt.Id,
            Date = BusinessDate.Now(),
            Amount = normalizedAmount,
            Method = PaymentMethod.Cash,
            RecordedById = recordedBy ?? "System"
        };

        return true;
    }

    private static decimal NormalizePaymentAmount(decimal amount, Receipt receipt)
    {
        if (amount <= 0m)
            return 0m;

        var remainingBalance = GetRemainingBalance(receipt);
        if (remainingBalance <= 0m)
            return 0m;

        return Math.Min(amount, remainingBalance);
    }

    private static decimal GetRemainingBalance(Receipt receipt)
        => Math.Max(0m, receipt.TotalAmount - receipt.PaidAmount);

    private void InvalidateMetrics()
    {
        _cacheInvalidator.InvalidateDashboard();
        _cacheInvalidator.InvalidateProfitReports();
    }
}
