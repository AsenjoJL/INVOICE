using HazelInvoice.Models;

namespace HazelInvoice.ViewModels;

public class VegetableOutletOrderViewModel
{
    public DateTime Date { get; set; } = DateTime.Today;
    
    public bool ShowAllOutlets { get; set; }

    // The currently selected outlet
    public int SelectedCustomerId { get; set; }
    
    // For the dropdown list
    public List<Customer> Customers { get; set; } = new();
    
    // All available products to list rows
    public List<Product> Products { get; set; } = new();
    
    // Prices for the current date (Key: ProductId)
    public Dictionary<int, decimal> ProductPrices { get; set; } = new();
    
    // Quantities for the SELECTED outlet (Key: ProductId)
    public Dictionary<int, decimal> Quantities { get; set; } = new();
}
