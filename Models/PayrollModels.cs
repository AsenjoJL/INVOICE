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
    public string? Code { get; set; } // Optional code (e.g., L-0001)

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

    public DateTime? HiredDate { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime? ArchivedAt { get; set; }

    [StringLength(200)]
    public string? Notes { get; set; }

    public List<AttendanceRecord> AttendanceRecords { get; set; } = new();
    public List<PayrollPeriod> PayrollPeriods { get; set; } = new();
}

public class AttendanceRecord
{
    public int Id { get; set; }

    public int LaborerId { get; set; }
    public Laborer? Laborer { get; set; }

    public DateTime WorkDate { get; set; } = BusinessDate.Today();

    public AttendanceStatus Status { get; set; } = AttendanceStatus.Present;

    [Column(TypeName = "decimal(18,2)")]
    public decimal RateSnapshot { get; set; }

    [Column(TypeName = "decimal(5,2)")]
    public decimal Multiplier { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal WageAmount { get; set; }

    public int? PayrollPeriodId { get; set; }
    public PayrollPeriod? PayrollPeriod { get; set; }

    [StringLength(200)]
    public string? Notes { get; set; }

    public string? RecordedById { get; set; }
}

public class PayrollPeriod
{
    public int Id { get; set; }

    public int? PayrollRunId { get; set; }
    public PayrollRun? PayrollRun { get; set; }

    public int LaborerId { get; set; }
    public Laborer? Laborer { get; set; }

    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }

    public int TotalDays { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalWage { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal PaidAmount { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal AdjustmentTotal { get; set; }

    public PaymentStatus Status { get; set; } = PaymentStatus.Unpaid;

    public DateTime GeneratedAt { get; set; } = BusinessDate.Now();

    [StringLength(200)]
    public string? Notes { get; set; }

    public List<PayrollPayment> Payments { get; set; } = new();
    public List<PayrollAdjustment> Adjustments { get; set; } = new();
    public List<AttendanceRecord> AttendanceRecords { get; set; } = new();
}

public class PayrollRun
{
    public int Id { get; set; }

    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }

    public PayrollRunStatus Status { get; set; } = PayrollRunStatus.Generated;

    public DateTime GeneratedAt { get; set; } = BusinessDate.Now();
    public DateTime? ApprovedAt { get; set; }
    public DateTime? ClosedAt { get; set; }

    [StringLength(100)]
    public string? GeneratedBy { get; set; }

    [StringLength(200)]
    public string? Notes { get; set; }

    public List<PayrollPeriod> PayrollPeriods { get; set; } = new();
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

public class PayrollPayment
{
    public int Id { get; set; }

    public int PayrollPeriodId { get; set; }
    public PayrollPeriod? PayrollPeriod { get; set; }

    public DateTime Date { get; set; } = BusinessDate.Today();

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    public PaymentMethod PaymentMethod { get; set; }

    [StringLength(50)]
    public string? ReferenceNo { get; set; }

    public string? RecordedById { get; set; }
}

public class PayrollAdjustment
{
    public int Id { get; set; }

    public int PayrollPeriodId { get; set; }
    public PayrollPeriod? PayrollPeriod { get; set; }

    public DateTime Date { get; set; } = BusinessDate.Today();

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    [StringLength(200)]
    public string? Reason { get; set; }

    [StringLength(100)]
    public string? CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; } = BusinessDate.Now();
}
