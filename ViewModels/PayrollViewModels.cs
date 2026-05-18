using System;
using System.Collections.Generic;
using HazelInvoice.Models;
using HazelInvoice.Helpers;

namespace HazelInvoice.ViewModels;

public class AttendanceDailyViewModel
{
    public DateTime WorkDate { get; set; } = BusinessDate.Today();
    public List<AttendanceEntryViewModel> Entries { get; set; } = new();
    public decimal TotalWage { get; set; }
    public bool IsDateLocked { get; set; }
    public string? DateLockReason { get; set; }
}

public class AttendanceEntryViewModel
{
    public int? AttendanceRecordId { get; set; }
    public int LaborerId { get; set; }
    public string LaborerName { get; set; } = string.Empty;
    public decimal DailyRate { get; set; }
    public AttendanceStatus Status { get; set; } = AttendanceStatus.Present;
    public string? Notes { get; set; }
    public decimal WageAmount { get; set; }
    public bool IsInPayroll { get; set; }
}

public class PayrollGenerateViewModel
{
    public DateTime StartDate { get; set; } = BusinessDate.Today().AddDays(-6);
    public DateTime EndDate { get; set; } = BusinessDate.Today();
    public List<PayrollGenerateRow> Rows { get; set; } = new();
    public List<int> SelectedLaborerIds { get; set; } = new();
    public int? ExistingRunId { get; set; }
    public PayrollRunStatus? ExistingRunStatus { get; set; }
}

public class PayrollGenerateRow
{
    public int LaborerId { get; set; }
    public string LaborerName { get; set; } = string.Empty;
    public int TotalDays { get; set; }
    public decimal TotalWage { get; set; }
}

public class PayrollIndexViewModel
{
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public PaymentStatus? Status { get; set; }
    public int CurrentPage { get; set; } = 1;
    public int PageSize { get; set; } = 25;
    public int TotalPages { get; set; }
    public int TotalPeriods { get; set; }
    public int FilteredUnpaidCount { get; set; }
    public decimal FilteredBalanceTotal { get; set; }
    public decimal PageTotalBalance { get; set; }
    public List<PayrollPeriod> Periods { get; set; } = new();
}

public class PayrollDetailsViewModel
{
    public PayrollPeriod Period { get; set; } = new();
    public List<AttendanceRecord> AttendanceRecords { get; set; } = new();
    public List<PayrollPayment> Payments { get; set; } = new();
    public List<PayrollAdjustment> Adjustments { get; set; } = new();
    public List<PayrollAdjustmentOption> AdjustmentOptions { get; set; } = new();
    public decimal RemainingBalance { get; set; }
    public decimal PayableTotal { get; set; }
    public PayrollPayment NewPayment { get; set; } = new();
}

public class PayrollAdjustmentOption
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public bool IsDeduction { get; set; }
}

public class UnpaidPayrollViewModel
{
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public List<PayrollPeriod> Periods { get; set; } = new();
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
