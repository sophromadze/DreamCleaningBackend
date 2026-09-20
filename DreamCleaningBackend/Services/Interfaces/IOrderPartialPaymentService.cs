using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Services.Interfaces
{
    /// <summary>
    /// The ONLY code that may write <see cref="Order.AmountPaid"/> or an
    /// <see cref="OrderPartialPayment"/> row. Every "how much has arrived / how much is owed"
    /// question is answered here or in <c>Helpers/OrderBalance.cs</c>, so the admin panel, the
    /// payment page and the Stripe webhook cannot arrive at three different numbers.
    /// </summary>
    public interface IOrderPartialPaymentService
    {
        /// <summary>The order's balance, its live request (at most one) and its payment history.</summary>
        Task<OrderPaymentBalanceDto> GetBalanceAsync(int orderId, CancellationToken ct = default);

        /// <summary>The live request, or null. Throws nothing for a missing order.</summary>
        Task<OrderPartialPayment?> GetPendingRequestAsync(int orderId, CancellationToken ct = default);

        /// <summary>
        /// Ask the customer for part of the outstanding balance. Refuses a second live request, an
        /// amount above what is still owed, and anything below Stripe's minimum.
        /// </summary>
        /// <exception cref="PartialPaymentException">The request cannot be made; the message is
        /// admin-facing and says why.</exception>
        Task<OrderPartialPayment> CreateRequestAsync(
            int orderId, decimal amount, string? note, int adminUserId, CancellationToken ct = default);

        /// <summary>Withdraw a live request. The row is kept as Cancelled, never deleted.</summary>
        /// <exception cref="PartialPaymentException">Not found, or already settled.</exception>
        Task<OrderPartialPayment> CancelRequestAsync(
            int orderId, int requestId, int adminUserId, CancellationToken ct = default);

        /// <summary>Records that the request's payment link went out, for the panel's "sent" line.</summary>
        Task MarkNotificationSentAsync(int requestId, CancellationToken ct = default);

        /// <summary>
        /// Records money that Stripe confirms has arrived, idempotently — the browser's confirm and
        /// the webhook both call this for the same intent and only the first one moves anything.
        ///
        /// Does NOT mark the order paid even when the balance reaches zero. Settling an order also
        /// consumes the loyalty discount, activates the subscription and sends the booking
        /// confirmation, and that finishing work lives in one place (BookingController's
        /// confirm-payment); this reports <see cref="PartialPaymentSettlement.OrderNowFullyPaid"/>
        /// so the caller can run it.
        /// </summary>
        /// <param name="amountReceived">What Stripe says arrived — never a client-supplied figure.</param>
        Task<PartialPaymentSettlement> SettleAsync(
            int orderId, string paymentIntentId, decimal amountReceived, CancellationToken ct = default);

        /// <summary>
        /// Records a LIVE (Pending) request as paid outside Stripe — cash handed over, a Zelle
        /// confirmation, a check, or an amount going on a commercial invoice. Unlike
        /// <see cref="SettleAsync"/> there is no external source of truth to reconcile against, so
        /// this settles for exactly <see cref="OrderPartialPayment.RequestedAmount"/> and re-checks
        /// it against the order's CURRENT balance first — an order edited down after the request
        /// was created must not let a stale request collect more than is actually owed.
        /// </summary>
        /// <param name="method">Must not be <see cref="PaymentMethod.Normal"/> — that value means
        /// "through the card link" and this call is specifically the alternative to it.</param>
        /// <exception cref="PartialPaymentException">Not found, already settled/cancelled, method
        /// is Normal, or the request now exceeds what is owed.</exception>
        Task<PartialPaymentSettlement> RecordManualPaymentAsync(
            int orderId, int requestId, PaymentMethod method, string? paymentReference, string? paymentNotes,
            int adminUserId, CancellationToken ct = default);

        /// <summary>
        /// Credits ONE order's share of a "Pay all upcoming" charge that does not settle it (the
        /// order's price rose between the customer authorising and Stripe capturing). The share is
        /// recorded as a paid slice whose <c>PaymentReference</c> is the batch intent — never its
        /// <c>PaymentIntentId</c>, which is UNIQUE and would collide the moment two orders of one
        /// batch were each part-covered. Returns false, changing nothing, when the order no longer
        /// holds the <paramref name="expectedTotal"/> / <paramref name="expectedAmountPaid"/> the
        /// caller computed the share against; the caller must then re-read and retry.
        /// </summary>
        Task<bool> RecordCombinedPaymentSliceAsync(
            int orderId, decimal amount, decimal expectedTotal, decimal expectedAmountPaid,
            string paymentIntentId, int batchId, CancellationToken ct = default);

        OrderPartialPaymentDto ToDto(OrderPartialPayment row);
    }

    /// <summary>
    /// Outcome of settling one slice.
    /// </summary>
    /// <param name="Applied">False when this intent had already been recorded — a webhook retry or
    /// the webhook racing the browser. The caller must treat it as success, not as an error.</param>
    /// <param name="OrderNowFullyPaid">True when nothing collectable is left, so the caller should
    /// run the order's normal payment-completion work.</param>
    public record PartialPaymentSettlement(
        bool Applied,
        int? PartialPaymentId,
        decimal AmountApplied,
        decimal AmountPaidTotal,
        decimal AmountDue,
        bool OrderNowFullyPaid);

    /// <summary>A part-payment operation the caller asked for and cannot have. The message is
    /// written for the admin (or the payer) who will read it.</summary>
    public class PartialPaymentException : Exception
    {
        public PartialPaymentException(string message) : base(message) { }
    }
}
