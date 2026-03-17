using HazelInvoice.ViewModels;

namespace HazelInvoice.Services.Orders;

public interface IVegetableMatrixService
{
    Task<VegetableMatrixViewModel> GetAsync(VegetableMatrixQueryOptions options, CancellationToken cancellationToken = default);
}

