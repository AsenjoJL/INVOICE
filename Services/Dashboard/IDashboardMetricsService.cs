using HazelInvoice.ViewModels;

namespace HazelInvoice.Services.Dashboard;

public interface IDashboardMetricsService
{
    Task<DashboardViewModel> BuildAsync(DateTime today, string? groupName = null, CancellationToken ct = default);
}
