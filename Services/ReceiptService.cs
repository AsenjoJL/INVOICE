using HazelInvoice.Data;
using HazelInvoice.Models;
using Microsoft.EntityFrameworkCore;

namespace HazelInvoice.Services;

public interface IReceiptService
{
    Task<string> GenerateNextReceiptNumberAsync();
}

public class ReceiptService : IReceiptService
{
    private readonly ApplicationDbContext _context;
    private const int StartingReceiptNumber = 8000;

    public ReceiptService(ApplicationDbContext context)
    {
        _context = context;
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
