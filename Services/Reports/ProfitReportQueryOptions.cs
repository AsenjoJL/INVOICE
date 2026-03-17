using System;

namespace HazelInvoice.Services.Reports;

public sealed record ProfitReportQueryOptions(
    DateTime StartDate,
    DateTime EndDate,
    bool IncludeUnpaid,
    decimal PercentFee,
    decimal Partner1SharePercent
);

