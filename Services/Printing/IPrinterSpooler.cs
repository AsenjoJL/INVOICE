namespace HazelInvoice.Services.Printing;

public interface IPrinterSpooler
{
    bool TrySetDefaultPrinter(string printerName, out string? error);
    bool TryTestPrint(string printerName, out string? error);
}

