using DreamCleaningBackend.DTOs;

namespace DreamCleaningBackend.Services.Interfaces
{
    public interface IOrderRefundService
    {
        /// <summary>
        /// Refunds <paramref name="amount"/> (null = everything still refundable) across the
        /// order's card charges, oldest charge first. Never throws for payment-provider failures —
        /// the outcome comes back on the result object with a customer-safe message.
        /// </summary>
        Task<RefundResultDto> IssueRefundAsync(int orderId, decimal? amount, string? reason,
            int adminUserId, bool sendEmail);

        /// <summary>Refund history plus the live remaining-refundable ceiling for the admin panel.</summary>
        /// <exception cref="KeyNotFoundException">No such order.</exception>
        Task<OrderRefundSummaryDto> GetRefundSummaryAsync(int orderId);

        /// <summary>
        /// Reconciles one order against Stripe, importing any refund that was issued outside the
        /// CRM (Stripe Dashboard, or before the CRM refund feature existed) as a Stripe-sourced
        /// OrderRefund row. Idempotent — re-running imports nothing. Sends no customer email.
        /// Chargebacks are DETECTED and reported but never imported: a dispute is not a refund and
        /// does not appear in Stripe's refunded amount.
        /// </summary>
        Task<RefundSyncResultDto> SyncRefundsFromStripeAsync(int orderId);

        /// <summary>
        /// Runs the sync across a page of orders holding a real Stripe charge. Paged because each
        /// charge costs a Stripe round trip. Manual, admin-triggered only — never on startup.
        /// </summary>
        Task<RefundBackfillResultDto> BackfillRefundsFromStripeAsync(int limit, int? afterOrderId);

        /// <summary>Sends (or re-sends) the customer refund confirmation for one recorded refund,
        /// on explicit admin request.</summary>
        Task<RefundResultDto> SendRefundEmailAsync(int orderId, int refundId);
    }
}
