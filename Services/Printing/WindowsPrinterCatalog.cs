using System.Runtime.InteropServices;

namespace HazelInvoice.Services.Printing;

public class WindowsPrinterCatalog : IPrinterCatalog
{
    public IReadOnlyList<string> GetInstalledPrinters()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Array.Empty<string>();

        try
        {
            return Win32PrinterApi.GetInstalledPrinterNames();
        }
        catch (DllNotFoundException)
        {
            return Array.Empty<string>();
        }
        catch (EntryPointNotFoundException)
        {
            return Array.Empty<string>();
        }
        catch (PlatformNotSupportedException)
        {
            return Array.Empty<string>();
        }
    }

    public bool PrinterExists(string printerName)
    {
        if (string.IsNullOrWhiteSpace(printerName)) return false;

        return GetInstalledPrinters().Any(p => string.Equals(p, printerName, StringComparison.OrdinalIgnoreCase));
    }
}
