using System.Threading;
using System.Threading.Tasks;
using HazelInvoice.ViewModels;

namespace HazelInvoice.Services.Reports;

public interface IProfitReportService
{
    Task<ProfitSummaryViewModel> BuildAsync(ProfitReportQueryOptions options, CancellationToken ct = default);
}

