namespace DreamCleaningBackend.Services.Interfaces
{
    /// <summary>Outcome of applying one succeeded Stripe additional payment to an order.</summary>
    /// <param name="OrderFound">False when the orderId does not exist.</param>
    /// <param name="MarkedCount">OrderUpdateHistory rows THIS call flipped to paid. Zero when
    /// another racer (webhook vs. browser confirm) already marked them — not an error.</param>
    /// <param name="AmountNewlyPaid">Sum of AdditionalAmount over those rows only, so a caller
    /// that sends a notification on payment cannot send it twice for the same money.</param>
    /// <param name="StatusReactivated">True when the order moved Pending -> Active on this call.</param>
    /// <param name="OrderStatus">The order's status after reconciliation.</param>
    public record AdditionalPaymentResult(
        bool OrderFound,
        int MarkedCount,
        decimal AmountNewlyPaid,
        bool StatusReactivated,
        string? OrderStatus);

    /// <summary>
    /// Single source of truth for settling additional (post-booking) payments and for the
    /// Pending -> Active status flip that follows. Every path that collects an additional amount
    /// goes through here: the Stripe webhook, the customer's confirm call, and the admin's manual
    /// (Zelle/cash/check) recording.
    ///
    /// Exists because the "is anything still unpaid?" check MUST run against saved data. EF Core
    /// does not flush pending changes before a query, and the check compiles to a scalar
    /// SELECT EXISTS that bypasses the change tracker entirely — so marking rows paid in memory
    /// and then asking the database still sees them as unpaid, and the flip silently never fires.
    /// Both methods here save before they query, so that ordering cannot be got wrong at a call site.
    /// </summary>
    public interface IOrderPaymentStatusReconciler
    {
        /// <summary>
        /// Marks the OrderUpdateHistory rows settled by <paramref name="paymentIntentId"/> as paid,
        /// then reconciles the order's status. Rows are matched on the payment intent id; when none
        /// carry it (the id had not been stamped yet) the latest unpaid row is used and stamped.
        /// Idempotent: calling it again after the rows are already paid marks nothing, reports
        /// AmountNewlyPaid = 0, and still reconciles the status.
        /// </summary>
        Task<AdditionalPaymentResult> ApplyStripeAdditionalPaymentAsync(
            int orderId, string paymentIntentId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Moves the order Pending -> Active once no unpaid additional amounts remain. Use directly
        /// when the caller settled the money itself (e.g. an admin recording a Zelle payment).
        /// Persists any changes the caller has staged on the shared DbContext first, so the
        /// remaining-unpaid check reads committed data. Never touches Done/Cancelled/Refunded, and
        /// never promotes an order whose base payment is still outstanding.
        /// </summary>
        /// <returns>True only when this call changed the status.</returns>
        Task<bool> ReconcileStatusAfterAdditionalPaymentAsync(
            int orderId, CancellationToken cancellationToken = default);
    }
}
