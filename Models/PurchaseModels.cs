using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace HazelInvoice.Models;

public class Supplier
{
    public int Id { get; set; }

    [Required]
    [StringLength(120)]
    public string Name { get; set; } = string.Empty;

    [StringLength(100)]
    public string? ContactPerson { get; set; }

    [StringLength(50)]
    public string? ContactNumber { get; set; }

    [StringLength(200)]
    public string? Address { get; set; }

    public bool IsActive { get; set; } = true;
}

public class Purchase
{
    public int Id { get; set; }

    [Required]
    [StringLength(20)]
    public string PurchaseNumber { get; set; } = string.Empty; // PO-2026-000001

    public DateTime Date { get; set; } = DateTime.Now;

    public int? SupplierId { get; set; }
    public Supplier? Supplier { get; set; }

    [StringLength(120)]
    public string SupplierName { get; set; } = string.Empty;

    [StringLength(200)]
    public string? SupplierAddress { get; set; }

    [StringLength(50)]
    public string? ContactNumber { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalAmount { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal PaidAmount { get; set; }

    public PaymentStatus Status { get; set; } = PaymentStatus.Unpaid;

    [StringLength(200)]
    public string? Notes { get; set; }

    public string? CreatedById { get; set; }

    public List<PurchaseLine> Lines { get; set; } = new();
    public List<PurchasePayment> Payments { get; set; } = new();
}

public class PurchaseLine
{
    public int Id { get; set; }

    public int PurchaseId { get; set; }
    public Purchase? Purchase { get; set; }

    public int? ProductId { get; set; }
    public Product? Product { get; set; }

    [Required]
    [StringLength(120)]
    public string ItemName { get; set; } = string.Empty;

    public int Quantity { get; set; }

    [StringLength(20)]
    public string Unit { get; set; } = "pc";

    [Column(TypeName = "decimal(18,2)")]
    public decimal Cost { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }
}

public class PurchasePayment
{
    public int Id { get; set; }

    public int PurchaseId { get; set; }
    public Purchase? Purchase { get; set; }

    public DateTime Date { get; set; } = DateTime.Now;

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    public PaymentMethod PaymentMethod { get; set; }

    [StringLength(50)]
    public string? ReferenceNo { get; set; }

    public string? RecordedById { get; set; }
}

public class PurchaseSequence
{
    public int Id { get; set; }
    public int Year { get; set; }
    public int LastNumber { get; set; }
}
