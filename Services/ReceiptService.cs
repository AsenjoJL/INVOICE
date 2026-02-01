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
            sequence = new ReceiptSequence { Year = year, LastNumber = 5000 };
            _context.ReceiptSequences.Add(sequence);
            await _context.SaveChangesAsync();
        }

        // Adjust sequence if it's below the starting point of 5000
        if (sequence.LastNumber < 5000)
        {
            sequence.LastNumber = 5000;
        }

        sequence.LastNumber++;
        await _context.SaveChangesAsync();

        return $"DR-{year}-{sequence.LastNumber:D6}";
    }
}
