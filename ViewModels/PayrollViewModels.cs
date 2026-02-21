using System;
using System.Collections.Generic;
using HazelInvoice.Models;

namespace HazelInvoice.ViewModels;

public class AttendanceDailyViewModel
{
    public DateTime WorkDate { get; set; } = DateTime.Today;
    public List<AttendanceEntryViewModel> Entries { get; set; } = new();
    public decimal TotalWage { get; set; }
}

public class AttendanceEntryViewModel
{
    public int LaborerId { get; set; }
    public string LaborerName { get; set; } = string.Empty;
    public decimal DailyRate { get; set; }
    public AttendanceStatus Status { get; set; } = AttendanceStatus.Present;
    public string? Notes { get; set; }
    public decimal WageAmount { get; set; }
}

public class PayrollGenerateViewModel
{
    public DateTime StartDate { get; set; } = DateTime.Today.AddDays(-14);
    public DateTime EndDate { get; set; } = DateTime.Today;
    public List<PayrollGenerateRow> Rows { get; set; } = new();
    public List<int> SelectedLaborerIds { get; set; } = new();
}

public class PayrollGenerateRow
{
    public int LaborerId { get; set; }
    public string LaborerName { get; set; } = string.Empty;
    public int TotalDays { get; set; }
    public decimal TotalWage { get; set; }
}

public class PayrollDetailsViewModel
{
    public PayrollPeriod Period { get; set; } = new();
    public List<AttendanceRecord> AttendanceRecords { get; set; } = new();
    public List<PayrollPayment> Payments { get; set; } = new();
    public decimal RemainingBalance { get; set; }
    public PayrollPayment NewPayment { get; set; } = new();
}

public class UnpaidPayrollViewModel
{
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public List<PayrollPeriod> Periods { get; set; } = new();
}

public class LaborCostReportViewModel
{
    public DateTime StartDate { get; set; } = DateTime.Today.AddDays(-30);
    public DateTime EndDate { get; set; } = DateTime.Today;
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
