using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;

namespace HazelInvoice.Services.Printing;

public class InvoicePrintManager : IInvoicePrintManager
{
    private readonly PrinterSettingsService _printerSettings;
    private readonly IPrinterCatalog _catalog;
    private readonly ILogger<InvoicePrintManager> _logger;

    public InvoicePrintManager(
        PrinterSettingsService printerSettings,
        IPrinterCatalog catalog,
        ILogger<InvoicePrintManager> logger)
    {
        _printerSettings = printerSettings;
        _catalog = catalog;
        _logger = logger;
    }

    public async Task<PrintPreparationResult> PrepareForInvoicePrintAsync(CancellationToken ct = default)
    {
        // In non-Windows environments (e.g., cloud hosting), kiosk-style "set Windows default printer"
        // is not applicable. Allow browser printing to proceed without blocking.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new PrintPreparationResult(PrintPreparationStatus.Ok);

        var selected = await _printerSettings.GetSelectedPrinterAsync(ct);
        if (string.IsNullOrWhiteSpace(selected))
        {
            return new PrintPreparationResult(
                PrintPreparationStatus.NotConfigured,
                "No printer is selected. Please update Printer Settings.");
        }

        if (!_catalog.PrinterExists(selected))
        {
            return new PrintPreparationResult(
                PrintPreparationStatus.PrinterMissing,
                "Selected printer not found. Please update printer settings.");
        }

        // Best-effort: set as Windows default so kiosk printing and the print dialog both target it.
        var (ok, err) = _printerSettings.TrySetAsWindowsDefault(selected);
        if (!ok)
        {
            _logger.LogWarning("PrepareForInvoicePrintAsync: failed to set Windows default to {Printer}: {Error}", selected, err);
            return new PrintPreparationResult(
                PrintPreparationStatus.FailedToApply,
                "Printer is available but could not be applied as default. Please run HazelInvoice as the current Windows user and verify printer permissions.");
        }

        return new PrintPreparationResult(PrintPreparationStatus.Ok);
    }
}
