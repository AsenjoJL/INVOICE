using HazelInvoice.Services.Printing;
using HazelInvoice.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HazelInvoice.Controllers;

[Authorize]
public class PrinterSettingsController : Controller
{
    private readonly PrinterSettingsService _printerSettings;
    private readonly IInvoicePrintManager _invoicePrintManager;

    public PrinterSettingsController(PrinterSettingsService printerSettings, IInvoicePrintManager invoicePrintManager)
    {
        _printerSettings = printerSettings;
        _invoicePrintManager = invoicePrintManager;
    }

    [HttpGet]
    public async Task<IActionResult> Index(string? returnUrl = null, CancellationToken ct = default)
    {
        var saved = await _printerSettings.GetSelectedPrinterAsync(ct);
        var printers = _printerSettings.GetInstalledPrinters();
        var borderless = await _printerSettings.GetReceiptBorderlessAsync(ct);
        var paperSize = await _printerSettings.GetPaperSizeAsync(ct);

        var safeReturnUrl = (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            ? returnUrl
            : null;

        var vm = new PrinterSettingsViewModel
        {
            SavedPrinter = saved,
            SelectedPrinter = saved,
            InstalledPrinters = printers.ToList(),
            ReceiptBorderless = borderless,
            PaperSize = paperSize,
            ReturnUrl = safeReturnUrl
        };

        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(PrinterSettingsViewModel vm, CancellationToken ct = default)
    {
        var (ok, error) = await _printerSettings.SaveSelectedPrinterAsync(vm.SelectedPrinter, ct);
        if (!ok)
        {
            TempData["ErrorMessage"] = error ?? "Unable to save printer setting.";
            return RedirectToAction(nameof(Index), new { returnUrl = (vm.ReturnUrl != null && Url.IsLocalUrl(vm.ReturnUrl)) ? vm.ReturnUrl : null });
        }

        await _printerSettings.SaveReceiptBorderlessAsync(vm.ReceiptBorderless, ct);
        await _printerSettings.SavePaperSizeAsync(vm.PaperSize, ct);

        // Optional but recommended for kiosk printing: make it the Windows default.
        var (applied, applyError) = _printerSettings.TrySetAsWindowsDefault(vm.SelectedPrinter);
        if (!applied)
        {
            TempData["ErrorMessage"] = $"Saved, but could not apply as Windows default: {applyError}";
        }
        else
        {
            TempData["SuccessMessage"] = "Printer saved successfully.";
        }

        if (!string.IsNullOrWhiteSpace(vm.ReturnUrl) && Url.IsLocalUrl(vm.ReturnUrl))
            return Redirect(vm.ReturnUrl);

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult TestPrint(PrinterSettingsViewModel vm)
    {
        // Test printing via browser is the most reliable because it matches production behavior
        // (kiosk printing + Windows default printer). RAW printing via spooler varies by driver.
        if (string.IsNullOrWhiteSpace(vm.SelectedPrinter))
        {
            TempData["ErrorMessage"] = "Please select a printer to test.";
            return RedirectToAction(nameof(Index), new { returnUrl = vm.ReturnUrl });
        }

        var safeReturnUrl = (!string.IsNullOrWhiteSpace(vm.ReturnUrl) && Url.IsLocalUrl(vm.ReturnUrl))
            ? vm.ReturnUrl
            : null;

        return RedirectToAction(nameof(TestPage), new { printerName = vm.SelectedPrinter, returnUrl = safeReturnUrl, paperSize = vm.PaperSize });
    }

    [HttpGet]
    public async Task<IActionResult> TestPage(string printerName, string? returnUrl = null, string? paperSize = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(printerName))
        {
            TempData["ErrorMessage"] = "Please select a printer to test.";
            return RedirectToAction(nameof(Index), new { returnUrl });
        }

        var safeReturnUrl = (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            ? returnUrl
            : null;

        // Best-effort: set selected printer as Windows default for this test run.
        var (ok, error) = _printerSettings.TrySetAsWindowsDefault(printerName);
        if (!ok)
        {
            TempData["ErrorMessage"] = $"Unable to use selected printer: {error}";
            return RedirectToAction(nameof(Index), new { returnUrl = safeReturnUrl });
        }

        ViewBag.PrinterName = printerName;
        ViewBag.ReturnUrl = safeReturnUrl;
        if (string.Equals(paperSize?.Trim(), "A4", StringComparison.OrdinalIgnoreCase))
            ViewBag.PaperSize = "A4";
        else if (string.Equals(paperSize?.Trim(), "Letter", StringComparison.OrdinalIgnoreCase))
            ViewBag.PaperSize = "Letter";
        else
            ViewBag.PaperSize = await _printerSettings.GetPaperSizeAsync(ct);
        return View();
    }

    // Called by UI print buttons to ensure printer is configured & applied before window.print().
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Prepare(CancellationToken ct = default)
    {
        var prep = await _invoicePrintManager.PrepareForInvoicePrintAsync(ct);
        if (prep.IsOk)
            return Ok(new { ok = true });

        // The browser Referer is an absolute URL. Convert to a local path so Url.IsLocalUrl works later.
        var referer = Request.Headers.Referer.ToString();
        string? safeReturnUrl = null;
        if (Uri.TryCreate(referer, UriKind.Absolute, out var uri))
        {
            var candidate = uri.PathAndQuery;
            if (Url.IsLocalUrl(candidate))
                safeReturnUrl = candidate;
        }

        var settingsUrl = Url.Action(nameof(Index), new { returnUrl = safeReturnUrl });
        return BadRequest(new { ok = false, message = prep.Message, settingsUrl });
    }
}
