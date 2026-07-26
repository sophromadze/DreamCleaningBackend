namespace DreamCleaningBackend.Models
{
    /// <summary>
    /// The status values Order.Status actually holds. Order.Status is a free string, not an enum
    /// (the OrderStatus enum in this folder is dead code — nothing references it), and the ~30
    /// comparison sites across the codebase are inconsistently cased ("cancelled" vs "Cancelled").
    /// That only works because MySQL's default collation is case-insensitive, so all comparisons
    /// here go through the case-insensitive helpers below rather than raw string equality.
    /// </summary>
    public static class OrderStatuses
    {
        public const string Pending = "Pending";
        public const string Active = "Active";
        public const string Done = "Done";
        public const string Cancelled = "Cancelled";

        /// <summary>Set automatically when a refund brings the refundable balance to zero.
        /// The pre-refund status is preserved on Order.StatusBeforeRefund — reporting needs it to
        /// tell a completed-then-refunded job (cleaner was paid) from one refunded before service.</summary>
        public const string Refunded = "Refunded";

        public static bool Is(string? status, string expected) =>
            string.Equals(status, expected, StringComparison.OrdinalIgnoreCase);

        public static bool IsRefunded(string? status) => Is(status, Refunded);

        public static bool IsCancelled(string? status) => Is(status, Cancelled);

        /// <summary>
        /// True when the cleaning actually happened, so the order's cleaner salary is a real cost
        /// even if the customer's money was later returned. A fully-refunded order carries
        /// Refunded in Status and Done in StatusBeforeRefund.
        /// </summary>
        public static bool WasPerformed(string? status, string? statusBeforeRefund) =>
            Is(status, Done) || (IsRefunded(status) && Is(statusBeforeRefund, Done));

        /// <summary>
        /// Only dead orders may be hidden from the admin list: cancelled, or fully refunded
        /// (Status is set to Refunded exactly when a refund clears the whole balance — a partial
        /// refund leaves the status alone, and that order is still live money). This stops a live
        /// or completed job being tidied out of sight where nobody will chase it.
        /// Unhiding is NOT gated on this — an order reactivated while hidden must still be
        /// restorable.
        /// </summary>
        public static bool CanBeHidden(string? status) =>
            IsCancelled(status) || IsRefunded(status);
    }
}
