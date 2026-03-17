namespace HazelInvoice.Services.Printing;

public enum PrintPreparationStatus
{
    Ok = 0,
    NotConfigured = 1,
    PrinterMissing = 2,
    FailedToApply = 3
}

public record PrintPreparationResult(PrintPreparationStatus Status, string? Message = null)
{
    public bool IsOk => Status == PrintPreparationStatus.Ok;
}

