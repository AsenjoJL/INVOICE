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
