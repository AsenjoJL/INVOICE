using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace HazelInvoice.Models;

public class Supply
{
    public int Id { get; set; }

    [Required]
    [StringLength(100)]
    public string Name { get; set; } = string.Empty;

    [StringLength(20)]
    public string Unit { get; set; } = "pc";

    public int ReorderLevel { get; set; } = 5;

    public bool IsActive { get; set; } = true;
}

public class ProductStockMovement
{
    public int Id { get; set; }

    public int ProductId { get; set; }
    public Product? Product { get; set; }

    public DateTime Date { get; set; } = DateTime.Now;

    public int Quantity { get; set; } // + for In, - for Out

    [StringLength(50)]
    public string Type { get; set; } = "Adjustment"; // Sale, StockIn, Return, Adjustment

    [StringLength(100)]
    public string? Reference { get; set; } // Receipt # or PO #
    
    public string? RecordedById { get; set; }
}

public class SupplyStockMovement
{
    public int Id { get; set; }

    public int SupplyId { get; set; }
    public Supply? Supply { get; set; }

    public DateTime Date { get; set; } = DateTime.Now;

    public int Quantity { get; set; }

    [StringLength(50)]
    public string Type { get; set; } = "StockIn"; // StockIn, Usage, Adjustment

    [StringLength(100)]
    public string? Reference { get; set; }
    
    public string? RecordedById { get; set; }
}
