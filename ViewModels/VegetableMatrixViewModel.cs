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

    public bool IsPrint { get; set; }
    public bool ShowDetails { get; set; }

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

    // OutletId -> Total Qty across all products for the day (visible outlets)
    public Dictionary<int, decimal> OutletTotalQty { get; set; } = new();

    // OutletId -> Has any order for the day (visible outlets)
    public Dictionary<int, bool> OutletHasOrders { get; set; } = new();

    public decimal GrandTotalQty { get; set; }
    public decimal GrandTotalAmount { get; set; }

    public List<VegetableOrderDetailRow> DetailRows { get; set; } = new();
    public decimal DetailTotalSales { get; set; }
    public decimal DetailTotalCost { get; set; }
    public decimal DetailTotalGrossProfit { get; set; }
    public decimal DetailTotalExpenses { get; set; }
    public decimal DetailTotalDeductions { get; set; }
    public decimal DetailTotalDeliveryPayments { get; set; }

    // Partner purchases (ProfitReport.PartnerPurchases) for the selected day.
    // These are treated like additional "sales" for the 1% fee base (matches existing Profit & Sales behavior).
    public List<string> DetailPartnerNames { get; set; } = new();
    public List<PartnerPurchaseGroupVm> DetailPartnerPurchaseGroups { get; set; } = new();
    public decimal DetailTotalPartnerPurchases { get; set; }
    public decimal DetailSalesSubTotal { get; set; } // TotalSales + PartnerPurchases

    // Per-product partner attribution coming from receipt line tagging (ReceiptLine.PartnerName).
    // These totals are shown as columns beside Status in the details table.
    public Dictionary<string, decimal> DetailPartnerOrderTotals { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Partner-attributed sales breakdown (ReceiptLine.PartnerName) aggregated by item for the selected day.
    public List<PartnerSalesAttributionGroupVm> DetailPartnerSalesAttributions { get; set; } = new();
    public decimal DetailTotalPartnerSalesAttributed { get; set; }

    public decimal DetailSubTotal { get; set; }
    public decimal DetailPercentFee { get; set; }
    public decimal DetailPercentFeeAmount { get; set; }
    public decimal DetailNetProfit { get; set; }

    public List<MoneyLineItem> DetailDeductionItems { get; set; } = new();
    public List<MoneyLineItem> DetailExpenseItems { get; set; } = new();

    // ProductId -> Status (NO_ORDERS, ONE_ORDER, MANY_ORDERS, LOWEST_QTY, HIGHEST_QTY)
    public Dictionary<int, string> ProductStatuses { get; set; } = new();

    // "ProductId_CustomerId" -> PaymentStatus string (UNPAID, PAID)
    public Dictionary<string, string> MatrixStatuses { get; set; } = new();
}

public class VegetableOrderDetailRow
{
    public int ProductId { get; set; }
    public string OutletName { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public string OwnerName { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal Amount { get; set; }
    public decimal CostAmount { get; set; }
    public decimal GrossProfit { get; set; }
    public string Status { get; set; } = string.Empty;

    // Partner purchases are not tied to an outlet; they are shown once per product in the details table.
    // Key is PartnerName; value is amount for this product for the selected day.
    public Dictionary<string, decimal> PartnerPurchaseByPartner { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class MoneyLineItem
{
    public string Label { get; set; } = string.Empty;
    public decimal Amount { get; set; }
}

public class PartnerPurchaseGroupVm
{
    public string PartnerName { get; set; } = string.Empty;
    public List<MoneyLineItem> Items { get; set; } = new();
    public decimal TotalAmount => Items.Sum(x => x.Amount);
}

// For Vegetable Order Details: partner-tagged sales lines (ReceiptLine.PartnerName)
// aggregated by partner and item name for the selected day.
public class PartnerSalesAttributionGroupVm
{
    public string PartnerName { get; set; } = string.Empty;
    public List<MoneyLineItem> Items { get; set; } = new();
    public decimal TotalAmount => Items.Sum(x => x.Amount);
}
