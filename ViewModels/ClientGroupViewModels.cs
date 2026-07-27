using System.ComponentModel.DataAnnotations;

namespace HazelInvoice.ViewModels;

public sealed class ClientGroupListItemVm
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string OutletGroupNames { get; set; } = string.Empty;

    public bool IsActive { get; set; }

    public int DisplayOrder { get; set; }

    public int OutletCount { get; set; }
}

public sealed class ClientGroupFormVm
{
    public int Id { get; set; }

    [Required]
    [StringLength(120)]
    public string Name { get; set; } = string.Empty;

    [StringLength(1000)]
    [Display(Name = "Included Outlet Groups")]
    public string OutletGroupNames { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public int DisplayOrder { get; set; }
}
