using System.ComponentModel.DataAnnotations;

namespace HazelInvoice.Models;

public class Customer
{
    public int Id { get; set; }

    [Required]
    [StringLength(100)]
    public string Name { get; set; } = string.Empty;

    [StringLength(200)]
    public string? Address { get; set; }

    [StringLength(50)]
    public string? ContactPerson { get; set; }
    
    [StringLength(50)]
    public string? ContactNumber { get; set; }

    public bool IsActive { get; set; } = true;

    // Reporting / Matrix Grouping
    [StringLength(100)]
    public string GroupName { get; set; } = "EIGHT2EIGHT OUTLETS"; // Default group
    
    [StringLength(50)]
    public string? SubLabel { get; set; } // e.g. "Kitchen" displayed under main header
}
