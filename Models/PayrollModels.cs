using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using HazelInvoice.Helpers;

namespace HazelInvoice.Models;

public class Laborer
{
    public int Id { get; set; }

    [StringLength(20)]
    public string Code { get; set; } = string.Empty;

    [Required]
    [StringLength(120)]
    public string FullName { get; set; } = string.Empty;

    [Column(TypeName = "decimal(18,2)")]
    public decimal DailyRate { get; set; }

    [StringLength(80)]
    public string? Role { get; set; }

    [StringLength(50)]
    public string? ContactNumber { get; set; }

    [StringLength(200)]
    public string? Address { get; set; }

    public DateTime HiredDate { get; set; } = BusinessDate.Today();

    public bool IsActive { get; set; } = true;

    public DateTime? ArchivedAt { get; set; }

    [StringLength(200)]
    public string? Notes { get; set; }

    public List<AttendanceRecord> AttendanceRecords { get; set; } = new();
    public List<PayrollEntry> PayrollEntries { get; set; } = new();
    public List<LaborerSchedule> Schedules { get; set; } = new();
    public List<CashAdvance> CashAdvances { get; set; } = new();
}

public class LaborerSchedule
{
    public int Id { get; set; }
    public int LaborerId { get; set; }
    public Laborer Laborer { get; set; } = null!;

    [StringLength(100)]
    public string WorkDays { get; set; } = "Mon,Tue,Wed,Thu,Fri,Sat";

    public DateTime EffectiveDate { get; set; } = BusinessDate.Today();
    public bool IsActive { get; set; } = true;
}

public class CashAdvance
{
    public int Id { get; set; }
    public int LaborerId { get; set; }
    public Laborer Laborer { get; set; } = null!;

    public DateTime Date { get; set; } = BusinessDate.Today();

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal RemainingBalance { get; set; }

    [StringLength(200)]
    public string? Notes { get; set; }

    public string? RecordedById { get; set; }

    public List<AdvanceDeduction> Deductions { get; set; } = new();
}

public class AttendanceRecord
{
    public int Id { get; set; }

    public int LaborerId { get; set; }
    public Laborer Laborer { get; set; } = null!;

    public DateTime WorkDate { get; set; } = BusinessDate.Today();
    public AttendanceStatus Status { get; set; } = AttendanceStatus.Present;
    public AttendanceSource Source { get; set; } = AttendanceSource.Auto;

    [Column(TypeName = "decimal(18,2)")]
    public decimal RateSnapshot { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal WageAmount { get; set; }

    public int? PayrollEntryId { get; set; }
    public PayrollEntry? PayrollEntry { get; set; }

    [StringLength(200)]
    public string? Notes { get; set; }

    public string? RecordedById { get; set; }
}

public class PayrollRun
{
    public int Id { get; set; }
    public DateTime WeekStart { get; set; }
    public DateTime WeekEnd { get; set; }
    public PayrollRunStatus Status { get; set; } = PayrollRunStatus.Draft;
    public DateTime CreatedAt { get; set; } = BusinessDate.Now();
    public DateTime? ApprovedAt { get; set; }
    public DateTime? ClosedAt { get; set; }

    [StringLength(100)]
    public string? CreatedBy { get; set; }

    [StringLength(200)]
    public string? Notes { get; set; }

    public List<PayrollEntry> Entries { get; set; } = new();
}

public class PayrollEntry
{
    public int Id { get; set; }
    public int PayrollRunId { get; set; }
    public PayrollRun PayrollRun { get; set; } = null!;
    public int LaborerId { get; set; }
    public Laborer Laborer { get; set; } = null!;
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
    public int TotalDays { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal GrossWage { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalDeductions { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalAdditions { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal NetPay { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal PaidAmount { get; set; }

    public PaymentStatus Status { get; set; } = PaymentStatus.Unpaid;
    public DateTime GeneratedAt { get; set; } = BusinessDate.Now();

    [StringLength(200)]
    public string? Notes { get; set; }

    public List<AttendanceRecord> AttendanceRecords { get; set; } = new();
    public List<PayrollPayment> Payments { get; set; } = new();
    public List<Adjustment> Adjustments { get; set; } = new();
    public List<AdvanceDeduction> AdvanceDeductions { get; set; } = new();
}

public class Adjustment
{
    public int Id { get; set; }
    public int PayrollEntryId { get; set; }
    public PayrollEntry PayrollEntry { get; set; } = null!;
    public AdjustmentType Type { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    [StringLength(200)]
    public string? Reason { get; set; }

    public DateTime Date { get; set; } = BusinessDate.Today();
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = BusinessDate.Now();
}

public class AdvanceDeduction
{
    public int Id { get; set; }
    public int PayrollEntryId { get; set; }
    public PayrollEntry PayrollEntry { get; set; } = null!;
    public int CashAdvanceId { get; set; }
    public CashAdvance CashAdvance { get; set; } = null!;

    [Column(TypeName = "decimal(18,2)")]
    public decimal DeductAmount { get; set; }
}

public class PayrollPayment
{
    public int Id { get; set; }
    public int PayrollEntryId { get; set; }
    public PayrollEntry PayrollEntry { get; set; } = null!;
    public DateTime Date { get; set; } = BusinessDate.Today();

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    public PaymentMethod PaymentMethod { get; set; }

    [StringLength(50)]
    public string? ReferenceNo { get; set; }
    public string? RecordedById { get; set; }
}

public class PayrollCutoff
{
    public int Id { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public bool IsLocked { get; set; }
    public DateTime? LockedAt { get; set; }

    [StringLength(100)]
    public string? LockedBy { get; set; }

    [StringLength(200)]
    public string? Notes { get; set; }
}
