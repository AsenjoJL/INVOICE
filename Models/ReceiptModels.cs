using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace HazelInvoice.Models;

public class Receipt
{
    public int Id { get; set; }

    [Required]
    [StringLength(20)]
    public string ReceiptNumber { get; set; } = string.Empty; // DR-2026-000001

    public DateTime Date { get; set; } = DateTime.Now;

    [StringLength(100)]
    public string CustomerName { get; set; } = string.Empty;

    [StringLength(200)]
    public string? CustomerAddress { get; set; }

    [StringLength(50)]
    public string? ContactNumber { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalAmount { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal PaidAmount { get; set; }

    public ReceiptType Type { get; set; }
    public PaymentStatus Status { get; set; } = PaymentStatus.Unpaid;

    public string? CreatedById { get; set; } // User ID
    
    [StringLength(100)]
    public string? ReceivedBy { get; set; } // Signature name

    public List<ReceiptLine> Lines { get; set; } = new();
    public List<Payment> Payments { get; set; } = new();
}

public class ReceiptLine
{
    public int Id { get; set; }

    public int ReceiptId { get; set; }
    public Receipt? Receipt { get; set; }

    public int? ProductId { get; set; }
    public Product? Product { get; set; }

    public int? ServiceId { get; set; }
    public Service? Service { get; set; }

    [Required]
    [StringLength(100)]
    public string ItemName { get; set; } = string.Empty; // Snapshot of name

    public int Quantity { get; set; }

    [StringLength(20)]
    public string Unit { get; set; } = "pc";

    [Column(TypeName = "decimal(18,2)")]
    public decimal Price { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal CostPriceSnapshot { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; } // Qty * Price
}

public class Payment
{
    public int Id { get; set; }

    public int ReceiptId { get; set; }
    public Receipt? Receipt { get; set; }

    public DateTime Date { get; set; } = DateTime.Now;

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    public PaymentMethod Method { get; set; }

    [StringLength(50)]
    public string? ReferenceNo { get; set; }
    
    public string? RecordedById { get; set; }
}

public class ReceiptSequence
{
    public int Id { get; set; }
    public int Year { get; set; }
    public int LastNumber { get; set; }
}
