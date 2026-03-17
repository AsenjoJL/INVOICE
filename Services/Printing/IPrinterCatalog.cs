namespace HazelInvoice.Services.Printing;

public interface IPrinterCatalog
{
    IReadOnlyList<string> GetInstalledPrinters();
    bool PrinterExists(string printerName);
}

