using HazelInvoice.ViewModels;

namespace HazelInvoice.Services.Dashboard;

public interface IDashboardMetricsService
{
    Task<DashboardViewModel> BuildAsync(DateTime today, CancellationToken ct = default);
}

