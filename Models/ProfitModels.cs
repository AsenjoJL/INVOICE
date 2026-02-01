using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace HazelInvoice.Models;

public class Deduction
{
    public int Id { get; set; }
    
    public DateTime Date { get; set; } = DateTime.Now;
    
    [Required]
    [StringLength(200)]
    public string Description { get; set; } = string.Empty;
    
    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }
    
    [StringLength(50)]
    public string? Category { get; set; } // e.g., "Operational", "Salary"
    
    [StringLength(50)]
    public string? AppliedTo { get; set; } // "General", "Hazel", "Troy"
}

public class PartnerPurchase
{
    public int Id { get; set; }
    
    [Required]
    [StringLength(50)]
    public string PartnerName { get; set; } = string.Empty; // "Hazel", "Troy"
    
    public DateTime Date { get; set; } = DateTime.Now;
    
    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }
    
    [StringLength(200)]
    public string? Notes { get; set; }
}

public class PartnerBalanceConfig
{
    public int Id { get; set; }
    
    [Required]
    [StringLength(50)]
    public string PartnerName { get; set; } = string.Empty;
    
    [Column(TypeName = "decimal(18,2)")]
    public decimal OpeningBalance { get; set; }
    
    public DateTime AsOfDate { get; set; } = DateTime.Now;
}

public class PartnerCapital
{
    public int Id { get; set; }
    
    public DateTime Date { get; set; } = DateTime.Now;
    
    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }
    
    [StringLength(100)]
    public string Description { get; set; } = "Capital Fund";
}
