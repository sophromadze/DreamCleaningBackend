using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// SINGLE SOURCE OF TRUTH for "may this staff member mark the order Done themselves, or must
    /// they send a Done request for approval?" (2026-10).
    ///
    /// <b>Who decides directly</b> is exactly who may apply an order edit directly:
    /// <see cref="OrderEditApprovalPolicy.CanSaveDirectly"/> - SuperAdmins, and Admins a SuperAdmin
    /// has granted <c>CanEditOrdersWithoutApproval</c>. The same people approve Done requests
    /// (the owner's call: one trust grant, not a second one that could drift from it). The admin
    /// <c>AdminPosition</c> (Manager / Administrator) is deliberately NOT part of it - it only
    /// decides bonus credit.
    ///
    /// <b>Which moves need approval</b>: every move INTO Done except from Active. Active means the
    /// job was booked and confirmed in the normal way, so finishing it stays one click. Pending
    /// (unpaid / unconfirmed), and Cancelled or Refunded through the order editor, are the moves
    /// that need a second pair of eyes - covering all of them rather than only Pending is what stops
    /// the rule being stepped around by a detour through another status.
    ///
    /// Enforced server-side on every path that can set Done: the status endpoint (the panel's
    /// row button and Done modal), order creation straight into Done (recreate), and the order
    /// editor (which already routes every ungranted Admin's edit through approval). Mirrored on
    /// the frontend in <c>src/app/shared/order-done-approval.policy.ts</c>.
    /// </summary>
    public static class OrderDoneApprovalPolicy
    {
        /// <summary>True when this move is a move into Done that this caller must request.</summary>
        public static bool RequiresApproval(
            UserRole role, bool canEditOrdersWithoutApproval, string? fromStatus, string? toStatus)
        {
            if (!IsGatedMove(fromStatus, toStatus)) return false;
            return !OrderEditApprovalPolicy.CanSaveDirectly(role, canEditOrdersWithoutApproval);
        }

        /// <summary>
        /// A move into Done from anything but Active (or Done). Creating an order directly at Done
        /// is passed with a null <paramref name="fromStatus"/> and is gated too.
        /// </summary>
        public static bool IsGatedMove(string? fromStatus, string? toStatus)
        {
            if (!OrderStatuses.Is(toStatus, OrderStatuses.Done)) return false;
            if (OrderStatuses.Is(fromStatus, OrderStatuses.Done)) return false;
            return !OrderStatuses.Is(fromStatus, OrderStatuses.Active);
        }

        /// <summary>True when this caller may approve or reject a Done request.</summary>
        public static bool CanReview(UserRole role, bool canEditOrdersWithoutApproval)
            => OrderEditApprovalPolicy.CanSaveDirectly(role, canEditOrdersWithoutApproval);
    }
}
