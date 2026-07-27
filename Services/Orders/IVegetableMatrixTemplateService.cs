namespace HazelInvoice.Services.Orders;

public interface IVegetableMatrixTemplateService
{
    Task<byte[]> BuildTemplateAsync(DateTime date, string? groupName = null, CancellationToken cancellationToken = default);
}
