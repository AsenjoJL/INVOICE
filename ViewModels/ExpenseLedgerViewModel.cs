using HazelInvoice.Models;

namespace HazelInvoice.ViewModels;

public class ExpenseLedgerViewModel
{
    public List<ExpenseLedgerGroupViewModel> Groups { get; set; } = new();
    public decimal GrandTotal { get; set; }
}

public class ExpenseLedgerGroupViewModel
{
    public string Label { get; set; } = string.Empty;
    public List<Expense> Items { get; set; } = new();
    public decimal Total { get; set; }
}
