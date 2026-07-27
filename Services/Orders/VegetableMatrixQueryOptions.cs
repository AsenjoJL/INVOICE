namespace HazelInvoice.Services.Orders;

public sealed record VegetableMatrixQueryOptions
{
    public DateTime Date { get; init; } = DateTime.Today;

    public int OutletPage { get; init; } = 1;
    public int ProductPage { get; init; } = 1;

    public string? GroupName { get; init; }

    public bool Print { get; init; }
    public bool Details { get; init; }
}
