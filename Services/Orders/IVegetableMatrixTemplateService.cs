namespace HazelInvoice.Services.Orders;

public interface IVegetableMatrixTemplateService
{
    Task<byte[]> BuildTemplateAsync(DateTime date, CancellationToken cancellationToken = default);
}

