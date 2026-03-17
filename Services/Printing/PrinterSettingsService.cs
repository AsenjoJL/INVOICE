using HazelInvoice.Services.Settings;
using Microsoft.Extensions.Logging;

namespace HazelInvoice.Services.Printing;

public class PrinterSettingsService
{
    private readonly IAppSettingStore _settings;
    private readonly IPrinterCatalog _catalog;
    private readonly IPrinterSpooler _spooler;
    private readonly ILogger<PrinterSettingsService> _logger;

    public PrinterSettingsService(
        IAppSettingStore settings,
        IPrinterCatalog catalog,
        IPrinterSpooler spooler,
        ILogger<PrinterSettingsService> logger)
    {
        _settings = settings;
        _catalog = catalog;
        _spooler = spooler;
        _logger = logger;
    }

    public IReadOnlyList<string> GetInstalledPrinters() => _catalog.GetInstalledPrinters();

    public async Task<string?> GetSelectedPrinterAsync(CancellationToken ct = default)
    {
        return await _settings.GetAsync(PrinterSettingKeys.DefaultPrinterName, ct);
    }

    public async Task<bool> GetReceiptBorderlessAsync(CancellationToken ct = default)
    {
        var v = await _settings.GetAsync(PrinterSettingKeys.ReceiptBorderless, ct);
        return string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(v, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(v, "yes", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string> GetPaperSizeAsync(CancellationToken ct = default)
    {
        // Backward-compatible default: receipts were originally tuned for short bond paper (Letter).
        var v = (await _settings.GetAsync(PrinterSettingKeys.PaperSize, ct))?.Trim();
        return string.Equals(v, "A4", StringComparison.OrdinalIgnoreCase) ? "A4" : "Letter";
    }

    public async Task<(bool ok, string? error)> SaveSelectedPrinterAsync(string? printerName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(printerName))
            return (false, "Please select a printer.");

        if (!_catalog.PrinterExists(printerName))
            return (false, "Selected printer not found. Please choose another printer.");

        await _settings.SetAsync(PrinterSettingKeys.DefaultPrinterName, printerName, ct);
        return (true, null);
    }

    public async Task SaveReceiptBorderlessAsync(bool enabled, CancellationToken ct = default)
    {
        await _settings.SetAsync(PrinterSettingKeys.ReceiptBorderless, enabled ? "true" : "false", ct);
    }

    public async Task SavePaperSizeAsync(string? paperSize, CancellationToken ct = default)
    {
        var v = (paperSize ?? "").Trim();
        if (string.Equals(v, "A4", StringComparison.OrdinalIgnoreCase))
        {
            await _settings.SetAsync(PrinterSettingKeys.PaperSize, "A4", ct);
            return;
        }

        // Default to Letter for any unknown value.
        await _settings.SetAsync(PrinterSettingKeys.PaperSize, "Letter", ct);
    }

    public (bool ok, string? error) TestPrint(string? printerName)
    {
        if (string.IsNullOrWhiteSpace(printerName))
            return (false, "Please select a printer to test.");

        if (!_catalog.PrinterExists(printerName))
            return (false, "Selected printer not found. Please choose another printer.");

        var ok = _spooler.TryTestPrint(printerName, out var err);
        return (ok, err);
    }

    public (bool ok, string? error) TrySetAsWindowsDefault(string? printerName)
    {
        if (string.IsNullOrWhiteSpace(printerName))
            return (false, "Printer name is required.");

        if (!_catalog.PrinterExists(printerName))
            return (false, "Selected printer not found. Please choose another printer.");

        var ok = _spooler.TrySetDefaultPrinter(printerName, out var err);
        if (!ok)
        {
            _logger.LogWarning("Failed to set default printer to {Printer}: {Error}", printerName, err);
            return (false, err ?? "Unable to set default printer.");
        }

        return (true, null);
    }
}
