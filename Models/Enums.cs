namespace HazelInvoice.Models;

public enum ReceiptType
{
    Sale, // Walk-in
    Delivery
}

public enum PaymentStatus
{
    Unpaid,
    Partial,
    Paid,
    Void
}

public enum PaymentMethod
{
    Cash,
    GCash,
    BankTransfer,
    Check
}

public enum AttendanceStatus
{
    Present,
    Absent
}

public enum AttendanceSource
{
    Auto,
    Manual
}

public enum PayrollRunStatus
{
    Draft,
    Approved,
    Closed
}

public enum AdjustmentType
{
    Addition,
    Deduction
}
