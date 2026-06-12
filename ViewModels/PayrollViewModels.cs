using System;
using System.Collections.Generic;
using HazelInvoice.Helpers;
using HazelInvoice.Models;

namespace HazelInvoice.ViewModels;

public class AttendanceWeekViewModel
{
    public DateTime WeekStart { get; set; }
    public DateTime WeekEnd { get; set; }
    public List<AttendanceWeekRow> Rows { get; set; } = new();
    public decimal TotalWage { get; set; }
}

public class AttendanceWeekRow
{
    public int LaborerId { get; set; }
    public string LaborerName { get; set; } = string.Empty;
    public decimal DailyRate { get; set; }
    public Dictionary<DateTime, AttendanceRecord?> DailyRecords { get; set; } = new();
    public int TotalPresent { get; set; }
    public int TotalAbsent { get; set; }
    public decimal WeekWage { get; set; }
}

public class AttendanceDailyViewModel
{
    public DateTime WorkDate { get; set; }
    public List<AttendanceEntryViewModel> Entries { get; set; } = new();
    public decimal TotalWage { get; set; }
}

public class AttendanceEntryViewModel
{
    public int? AttendanceRecordId { get; set; }
    public int LaborerId { get; set; }
    public string LaborerName { get; set; } = string.Empty;
    public decimal DailyRate { get; set; }
    public AttendanceStatus Status { get; set; }
    public string? Notes { get; set; }
    public decimal WageAmount { get; set; }
    public bool IsInPayroll { get; set; }
    public DateTime FirstWorkDate { get; set; }
    public int TotalDutyDays { get; set; }
    public int TotalAbsenceDays { get; set; }
    public int TotalTrackedDays { get; set; }
}

public class PayrollIndexViewModel
{
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public PaymentStatus? Status { get; set; }
    public PayrollEntryRecordType? RecordType { get; set; }
    public int CurrentPage { get; set; } = 1;
    public int PageSize { get; set; } = 25;
    public int TotalPages { get; set; }
    public int TotalEntries { get; set; }
    public int UnpaidCount { get; set; }
    public decimal TotalBalance { get; set; }
    public decimal PageTotalBalance { get; set; }
    public List<PayrollEntry> Entries { get; set; } = new();
}

public class CreatePayrollRunViewModel
{
    public DateTime WeekStart { get; set; }
    public DateTime WeekEnd { get; set; }
    public List<PayrollRunPreviewRow> Preview { get; set; } = new();
    public bool HasExistingRun { get; set; }
    public DateTime HistoricalStartDate { get; set; }
    public DateTime HistoricalEndDate { get; set; }
    public DateTime RecordOnlyThrough { get; set; }
    public DateTime PaidThrough { get; set; }
    public DateTime UnpaidFrom { get; set; }
}

public class PayrollRunPreviewRow
{
    public int LaborerId { get; set; }
    public string LaborerName { get; set; } = string.Empty;
    public int TotalDays { get; set; }
    public decimal GrossWage { get; set; }
    public decimal PendingAdvanceDeductions { get; set; }
    public decimal NetPay { get; set; }
    public bool IsSelected { get; set; } = true;
}

public class PayrollAdjustmentOption
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public bool IsDeduction { get; set; }
}

public class PayrollEntryDetailsViewModel
{
    public PayrollEntry Entry { get; set; } = new();
    public List<AttendanceRecord> AttendanceRecords { get; set; } = new();
    public List<PayrollPayment> Payments { get; set; } = new();
    public List<Adjustment> Adjustments { get; set; } = new();
    public List<AdvanceDeduction> AdvanceDeductions { get; set; } = new();
    public decimal RemainingBalance { get; set; }
    public PayrollPayment NewPayment { get; set; } = new();
    public List<PayrollAdjustmentOption> AdjustmentOptions { get; set; } = new();
}

public class PayslipViewModel
{
    public string LaborerName { get; set; } = string.Empty;
    public string? LaborerRole { get; set; }
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
    public int TotalDays { get; set; }
    public decimal DailyRate { get; set; }
    public decimal GrossWage { get; set; }
    public List<(string Label, decimal Amount)> Deductions { get; set; } = new();
    public List<(string Label, decimal Amount)> Additions { get; set; } = new();
    public decimal TotalDeductions { get; set; }
    public decimal TotalAdditions { get; set; }
    public decimal NetPay { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal Balance { get; set; }
    public PaymentStatus Status { get; set; }
    public string DisplayStatus { get; set; } = string.Empty;
}

public class CashAdvanceViewModel
{
    public int LaborerId { get; set; }
    public string LaborerName { get; set; } = string.Empty;
    public List<CashAdvance> Advances { get; set; } = new();
    public decimal TotalOutstanding { get; set; }
    public CashAdvance NewAdvance { get; set; } = new();
}

public class UnpaidPayrollViewModel
{
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public List<PayrollEntry> Entries { get; set; } = new();
}

public class LaborCostReportViewModel
{
    public DateTime StartDate { get; set; } = BusinessDate.Today().AddDays(-30);
    public DateTime EndDate { get; set; } = BusinessDate.Today();
    public decimal TotalCost { get; set; }
    public List<LaborCostRow> Rows { get; set; } = new();
}

public class LaborCostRow
{
    public int LaborerId { get; set; }
    public string LaborerName { get; set; } = string.Empty;
    public int TotalDays { get; set; }
    public decimal TotalWage { get; set; }
}
