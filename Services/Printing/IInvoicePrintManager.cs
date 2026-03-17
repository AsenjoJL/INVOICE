namespace HazelInvoice.Services.Printing;

public interface IInvoicePrintManager
{
    /// <summary>
    /// Prepares the machine/browser printing pipeline by validating the saved printer and (best-effort)
    /// setting it as Windows default. Kiosk printing uses the default printer.
    /// </summary>
    Task<PrintPreparationResult> PrepareForInvoicePrintAsync(CancellationToken ct = default);
}

