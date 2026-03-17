namespace HazelInvoice.Services.Printing;

public static class PrinterSettingKeys
{
    // Future-proof: keep keys stable so we can evolve storage without breaking existing installs.
    public const string DefaultPrinterName = "Printing.DefaultPrinterName";

    // Print appearance (dot-matrix friendly options).
    public const string ReceiptBorderless = "Printing.ReceiptBorderless";

    // Paper size hint for browser printing (@page). Choices: "Letter" | "A4".
    public const string PaperSize = "Printing.PaperSize";
}
