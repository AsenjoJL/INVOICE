namespace HazelInvoice.ViewModels;

public class PrinterSettingsViewModel
{
    public string? SelectedPrinter { get; set; }
    public string? SavedPrinter { get; set; }
    public List<string> InstalledPrinters { get; set; } = new();

    // When enabled, receipt print removes borders/gridlines for faster dot-matrix printing.
    public bool ReceiptBorderless { get; set; }

    // Browser print hint (@page). Choices: "Letter" | "A4".
    public string PaperSize { get; set; } = "Letter";

    public string? ReturnUrl { get; set; }
}
