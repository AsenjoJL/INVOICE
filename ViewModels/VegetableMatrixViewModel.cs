using HazelInvoice.Models;

namespace HazelInvoice.ViewModels;

public class VegetableMatrixViewModel
{
    public DateTime Date { get; set; } = DateTime.Today;

    // Column Paging (Outlets)
    public int CurrentPage { get; set; } = 1;
    public int PageSize { get; set; } = 12;
    public int TotalOutletsInGroup { get; set; }
    public int TotalPages => (int)Math.Ceiling((double)TotalOutletsInGroup / PageSize);

    // Row Paging (Products)
    public int ProductPage { get; set; } = 1;
    public int ProductPageSize { get; set; } = 25;
    public int TotalProducts { get; set; }
    public int TotalProductPages => (int)Math.Ceiling((double)TotalProducts / ProductPageSize);

    // Grouping (optional)
    public string SelectedGroupName { get; set; } = "All";

    // Visible Data
    public List<Customer> VisibleOutlets { get; set; } = new();
    public List<Product> VisibleProducts { get; set; } = new();

    // ProductId -> Price (only IDs needed for visible + totals)
    public Dictionary<int, decimal> ProductPrices { get; set; } = new();

    // ProductId -> Original Cost
    public Dictionary<int, decimal> ProductCosts { get; set; } = new();

    // ProductId -> Markup
    public Dictionary<int, decimal> ProductMarkups { get; set; } = new();

    // "ProductId_CustomerId" -> Quantity (ONLY visible products×visible outlets)
    public Dictionary<string, decimal> MatrixQuantities { get; set; } = new();

    // ProductId -> Total Qty across ALL outlets in group (for visible rows)
    public Dictionary<int, decimal> ProductTotalQtyAllOutletsInGroup { get; set; } = new();

    public decimal GrandTotalQty { get; set; }
    public decimal GrandTotalAmount { get; set; }

    // ProductId -> Status (NO_ORDERS, UNPAID, PAID, THREE_PLUS, NORMAL)
    public Dictionary<int, string> ProductStatuses { get; set; } = new();

    // "ProductId_CustomerId" -> PaymentStatus string (UNPAID, PAID)
    public Dictionary<string, string> MatrixStatuses { get; set; } = new();
}
