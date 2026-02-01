using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace HazelInvoice.Models;

public class Expense
{
    public int Id { get; set; }

    public DateTime Date { get; set; } = DateTime.Now;

    [Required]
    [StringLength(50)]
    public string Category { get; set; } = string.Empty;

    [StringLength(100)]
    public string? Vendor { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    public PaymentMethod PaymentMethod { get; set; }

    [StringLength(50)]
    public string? ReferenceNo { get; set; }

    [StringLength(200)]
    public string? Description { get; set; }
    
    public string? RecordedById { get; set; }
}

public class Goal
{
    public int Id { get; set; }

    public int Month { get; set; }
    public int Year { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal SalesTarget { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal NetProfitTarget { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal ExpenseBudget { get; set; }
}
