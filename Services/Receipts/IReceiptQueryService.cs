using HazelInvoice.ViewModels;

namespace HazelInvoice.Services.Receipts;

public interface IReceiptQueryService
{
    Task<ReceiptsIndexViewModel> QueryAsync(ReceiptQueryOptions options, CancellationToken ct = default);
}

