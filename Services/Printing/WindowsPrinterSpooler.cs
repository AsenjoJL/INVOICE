using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace HazelInvoice.Services.Printing;

public class WindowsPrinterSpooler : IPrinterSpooler
{
    private readonly ILogger<WindowsPrinterSpooler> _logger;

    public WindowsPrinterSpooler(ILogger<WindowsPrinterSpooler> logger)
    {
        _logger = logger;
    }

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDefaultPrinter(string pszPrinter);

    public bool TrySetDefaultPrinter(string printerName, out string? error)
    {
        error = null;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            error = "Setting the default printer is only supported on Windows.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(printerName))
        {
            error = "Printer name is required.";
            return false;
        }

        try
        {
            var ok = SetDefaultPrinter(printerName);
            if (!ok)
            {
                var code = Marshal.GetLastWin32Error();
                error = $"Unable to set default printer (Win32Error={code}).";
                _logger.LogWarning("SetDefaultPrinter failed for {Printer}. Win32Error={Code}", printerName, code);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _logger.LogError(ex, "TrySetDefaultPrinter failed for {Printer}", printerName);
            return false;
        }
    }

    public bool TryTestPrint(string printerName, out string? error)
    {
        error = null;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            error = "Test printing is only supported on Windows.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(printerName))
        {
            error = "Printer name is required.";
            return false;
        }

        try
        {
            var text =
                "HAZELINVOICE - TEST PRINT\n" +
                "-------------------------\n" +
                $"Printer: {printerName}\n" +
                $"Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                "\n" +
                "If you can read this, printing works.\n" +
                "This test uses RAW text output (good for dot-matrix).\n";

            var ok = Win32PrinterApi.TryRawPrint(printerName, "HazelInvoice - Test Print", text, out error);
            if (!ok)
                _logger.LogWarning("Test print failed for {Printer}: {Error}", printerName, error);
            return ok;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _logger.LogError(ex, "TryTestPrint failed for {Printer}", printerName);
            return false;
        }
    }
}
