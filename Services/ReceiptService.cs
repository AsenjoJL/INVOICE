using System.Threading;
using HazelInvoice.Data;
using HazelInvoice.Models;
using Microsoft.EntityFrameworkCore;

namespace HazelInvoice.Services;

public interface IReceiptService
{
    Task<string> GenerateNextReceiptNumberAsync();
    Task<bool> DeleteReceiptAsync(int id, string? deletedBy = null, CancellationToken ct = default);
}

public class ReceiptService : IReceiptService
{
    private readonly ApplicationDbContext _context;
    private const int StartingReceiptNumber = 8000;

    public ReceiptService(ApplicationDbContext context)
    {
        _context = context;
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
}
