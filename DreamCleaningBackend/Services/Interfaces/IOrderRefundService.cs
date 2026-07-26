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
    }
}
