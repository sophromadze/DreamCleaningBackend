using DreamCleaningBackend.Models.Commercial;

namespace DreamCleaningBackend.Helpers.Commercial
{
    /// <summary>
    /// The facts a permanent-delete decision is made on. A struct, so the rules below are pure and
    /// asserted directly without a database or Stripe.
    /// </summary>
    public readonly struct InvoiceDeletionFacts
    {
        public InvoiceStatus Status { get; init; }

        /// <summary>Money recorded against the invoice, including manual ACH and cheques.</summary>
        public decimal AmountPaid { get; init; }

        /// <summary>Rows in <c>CommercialInvoicePayments</c>, reversals included.</summary>
        public int PaymentCount { get; init; }

        /// <summary>
        /// Rows in <c>CommercialInvoicePaymentAttempts</c> that actually reached Stripe - i.e.
        /// carry a checkout session or payment intent id.
        /// </summary>
        public int ExternalPaymentAttemptCount { get; init; }

        /// <summary>
        /// Order allocations that were COMMITTED - the invoice was sent and adopted those
        /// cleanings onto the Invoice payment method.
        /// </summary>
        public int CommittedOrderAllocationCount { get; init; }
    }

    /// <summary>
    /// WHETHER AN INVOICE MAY BE DESTROYED, as opposed to voided or archived.
    ///
    /// Void and Archive both keep the row: Void is the permanent financial statement that a number
    /// was issued and cancelled, Archive just takes it off the default list. This policy governs
    /// the third option, which keeps nothing - so it exists to say no, and says yes only for an
    /// invoice that never touched money.
    ///
    /// THE TEST IS FINANCIAL ACTIVITY, NOT STATUS. A Sent invoice nobody ever paid is a test
    /// invoice an admin should be able to clear away; a Draft would be too, and already was
    /// (<c>DeleteDraftAsync</c>, whose behaviour is unchanged and is a strict subset of this).
    /// What is never deletable is an invoice with a payment row, a Stripe object, or money
    /// recorded against it - those are accounting history, and the reconciliation that reads them
    /// has no way to know a row was removed.
    ///
    /// A REVERSAL DOES NOT MAKE AN INVOICE CLEAN AGAIN. A refunded invoice nets to zero paid but
    /// still has two payment rows describing money that moved in the real world, so
    /// <see cref="InvoiceDeletionFacts.PaymentCount"/> is tested independently of
    /// <see cref="InvoiceDeletionFacts.AmountPaid"/> rather than as a shortcut for it.
    /// </summary>
    public static class InvoiceHardDeletePolicy
    {
        /// <summary>
        /// Null when the invoice may be permanently deleted; otherwise the sentence to show the
        /// admin and to return from the API.
        /// </summary>
        public static string? DescribeBlocker(InvoiceDeletionFacts facts)
        {
            // Payment rows are the strongest signal and cover refunds and reversals too, which is
            // why this is tested before the paid amount rather than after it.
            if (facts.PaymentCount > 0)
                return "This invoice has financial activity and cannot be permanently deleted. Void or archive it instead.";

            if (facts.AmountPaid != 0m)
                return "This invoice has financial activity and cannot be permanently deleted. Void or archive it instead.";

            // A Stripe checkout session or payment intent exists against this invoice. Even a
            // failed one is a record on Stripe's side that we cannot remove and should not
            // silently lose our half of.
            if (facts.ExternalPaymentAttemptCount > 0)
                return "This invoice has Stripe payment activity and cannot be permanently deleted. Void or archive it instead.";

            // Sending an invoice ADOPTS its cleanings onto the Invoice payment method. Deleting it
            // would cascade those allocations away and leave the orders stamped as invoice-billed
            // by an invoice that no longer exists. Voiding is what hands them back.
            if (facts.CommittedOrderAllocationCount > 0)
                return "This invoice has claimed cleanings. Void it first so those cleanings are released, then archive or delete it.";

            // Paid and PartiallyPaid should already have been caught by the payment tests above;
            // if the arithmetic and the status ever disagree, refuse rather than trust the status.
            if (facts.Status is InvoiceStatus.Paid or InvoiceStatus.PartiallyPaid)
                return "This invoice has financial activity and cannot be permanently deleted. Void or archive it instead.";

            return null;
        }

        /// <summary>Convenience for a caller that only needs the yes/no.</summary>
        public static bool CanHardDelete(InvoiceDeletionFacts facts) =>
            DescribeBlocker(facts) == null;
    }
}
