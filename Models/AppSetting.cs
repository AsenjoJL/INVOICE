using System.ComponentModel.DataAnnotations;

namespace HazelInvoice.Models;

public class AppSetting
{
    [Key]
    [MaxLength(120)]
    public string Key { get; set; } = string.Empty;

    [MaxLength(2048)]
    public string? Value { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

