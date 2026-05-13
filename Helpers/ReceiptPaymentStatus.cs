using HazelInvoice.Models;

namespace HazelInvoice.Helpers;

public static class ReceiptPaymentStatus
{
    public static PaymentStatus Resolve(decimal totalAmount, decimal paidAmount)
    {
        if (paidAmount >= totalAmount && totalAmount > 0m)
            return PaymentStatus.Paid;

        if (paidAmount > 0m)
            return PaymentStatus.Partial;

        return PaymentStatus.Unpaid;
    }
}
