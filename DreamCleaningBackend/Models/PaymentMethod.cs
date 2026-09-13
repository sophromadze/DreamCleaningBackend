namespace DreamCleaningBackend.Models
{
    // Tracks how an order was paid. Default `Normal` (0) preserves the pre-existing
    // Stripe / IsPaid flow exactly — no behavioral change for existing data. Anything
    // else means the order was paid outside Stripe and IsPaid stays false.
    public enum PaymentMethod
    {
        Normal = 0,   // Stripe — uses existing IsPaid flow (default)
        Cash = 1,
        Zelle = 2,
        Check = 3,
        Other = 4,

        /// <summary>
        /// Billed through a commercial invoice (2026-09). Unlike Cash/Zelle/Check/Other — which
        /// record money that HAS ALREADY ARRIVED at creation time — Invoice means the money is
        /// still owed: the order stays in its pending state until a CommercialInvoice covering it
        /// is fully Paid, and the invoice ledger is what says so.
        ///
        /// So this member is deliberately NOT "another manual method". Every place that reads
        /// `PaymentMethod != Normal` as "already paid outside Stripe" must go through
        /// <see cref="PaymentMethodRules"/> instead, or an unpaid invoice order would be counted
        /// as revenue the day it was created.
        /// </summary>
        Invoice = 5
    }

    /// <summary>
    /// The three questions the codebase asks about <see cref="PaymentMethod"/>, answered in one
    /// place so the Invoice member cannot be mistaken for a settled manual payment.
    ///
    /// Before Invoice existed the two questions "was this recorded outside Stripe?" and "has this
    /// money arrived?" had the same answer, and ~40 call sites spelled both as
    /// `PaymentMethod != Normal`. They are different questions now.
    /// </summary>
    public static class PaymentMethodRules
    {
        /// <summary>
        /// True when the money is handled outside the residential Stripe checkout — so
        /// <c>Order.IsPaid</c> stays false and the payment page must not ask for a card.
        /// Invoice included: a commercial client pays their invoice, not the order.
        /// </summary>
        public static bool IsOutsideStripe(PaymentMethod method) => method != PaymentMethod.Normal;

        /// <summary>
        /// True when recording this method on an order MEANS the customer has already handed the
        /// money over (cash in an envelope, a Zelle transfer, a cheque). Invoice is the one
        /// outside-Stripe method where it is false — the invoice has not been paid yet.
        /// </summary>
        public static bool IsSettledOnRecord(PaymentMethod method) =>
            method != PaymentMethod.Normal && method != PaymentMethod.Invoice;

        // NOTE: there is deliberately no "RetainsSalesTax" predicate here. Whether an order's
        // sales tax was remitted is answered in exactly one place —
        // OrderRevenueMath.ResolveRetainedTax — because it depends on how each TOP-UP was
        // collected as well as on the order's own method. A second, simpler answer living here
        // would be the one somebody reaches for, and it would be wrong.

        /// <summary>Parses the wire string the admin UI sends. Null when unrecognised — the
        /// caller rejects rather than coercing, because Status/PaymentMethod typos persist.</summary>
        public static PaymentMethod? Parse(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            return Enum.TryParse<PaymentMethod>(value.Trim(), ignoreCase: true, out var parsed)
                   && Enum.IsDefined(typeof(PaymentMethod), parsed)
                ? parsed
                : null;
        }
    }
}
