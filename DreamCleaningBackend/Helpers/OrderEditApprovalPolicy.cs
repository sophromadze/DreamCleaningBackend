using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// Single source of truth for "may this admin apply an order edit straight away, or does it have
    /// to go to a SuperAdmin for approval first?".
    ///
    /// SuperAdmins always save directly. A regular Admin only does so when a SuperAdmin has granted
    /// them <see cref="User.CanEditOrdersWithoutApproval"/> — the same shape as the page-view grants
    /// in <see cref="DreamCleaningBackend.Services.AdminViewablePages"/>, except this one is a single
    /// boolean rather than a list of keys.
    ///
    /// The grant is deliberately checked TOGETHER with the role: demoting a granted Admin makes the
    /// stored flag inert without anyone having to remember to clear the column.
    ///
    /// Mirrored on the frontend in
    /// <c>src/app/shared/order-edit-approval.policy.ts</c> — keep both in step.
    /// </summary>
    public static class OrderEditApprovalPolicy
    {
        /// <summary>True when this user's order edits are applied immediately.</summary>
        public static bool CanSaveDirectly(UserRole role, bool canEditOrdersWithoutApproval)
        {
            if (role == UserRole.SuperAdmin) return true;
            return role == UserRole.Admin && canEditOrdersWithoutApproval;
        }

        /// <summary>True when this user's order edits must be submitted for SuperAdmin approval.</summary>
        public static bool RequiresApproval(UserRole role, bool canEditOrdersWithoutApproval)
            => !CanSaveDirectly(role, canEditOrdersWithoutApproval);
    }
}
