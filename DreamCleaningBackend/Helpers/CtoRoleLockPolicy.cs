using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// THE CTO'S SUPERADMIN ROLE CANNOT BE TAKEN AWAY — by anybody, including another SuperAdmin,
    /// the CEO, and the CTO themselves.
    ///
    /// The CTO is the account that governs officer titles (see
    /// <see cref="Contracts.OrgTitlePolicy"/>: once a CTO exists, only that person may assign or
    /// clear any title). Leaving their own role editable by the ordinary hierarchy made that
    /// authority circular — any of the several SuperAdmins could demote the CTO to Customer,
    /// which drops the title with it and hands the whole officer system to whoever acted first.
    /// Locking the role is what makes the title lock mean anything.
    ///
    /// <b>The way out is the TITLE, never the role.</b> There is no override flag and no
    /// "SuperAdmin can force it" branch, because either one would be the hole this closes. To move
    /// a CTO off SuperAdmin, clear the CTO title first — which <see cref="Contracts.OrgTitlePolicy"/>
    /// lets the CTO do themselves, or lets another SuperAdmin do once the CTO account is
    /// deactivated (deactivating reopens bootstrap mode, because
    /// <c>GetCurrentCtoUserIdAsync</c> only counts an ACTIVE holder). So a departed CTO is
    /// recoverable in deliberate, separately audited steps, and no single action demotes them.
    ///
    /// Scope is exactly what it says: the SuperAdmin role on a CTO account. A CTO who sits on the
    /// Admin role is not covered — there is no SuperAdmin to protect there, and locking an
    /// ordinary Admin's role would be a surprise nobody asked for. Every OTHER field on a CTO
    /// account (status, name, permissions) is untouched by this.
    /// </summary>
    public static class CtoRoleLockPolicy
    {
        /// <summary>What the admin is told, on both role-change endpoints and in the panel.</summary>
        public const string RefusalMessage =
            "This account holds the CTO title, so its SuperAdmin role cannot be changed. " +
            "Clear the officer title first.";

        /// <summary>
        /// True when this role change must be refused. <paramref name="targetOrgTitle"/> and
        /// <paramref name="targetCurrentRole"/> describe the account being changed as it stands
        /// today; <paramref name="newRole"/> is what the caller is asking for.
        ///
        /// Deliberately takes no requester argument — "who is asking" is not part of the rule,
        /// and giving it one would invite a caller to pass a bypass.
        /// </summary>
        public static bool IsRoleChangeLocked(
            OrgTitle targetOrgTitle,
            UserRole targetCurrentRole,
            UserRole newRole)
        {
            if (targetOrgTitle != OrgTitle.CTO) return false;
            if (targetCurrentRole != UserRole.SuperAdmin) return false;

            // Re-saving SuperAdmin is a no-op, and the Users-tab edit form posts the role on every
            // save whether or not it moved — refusing an unchanged value would make an unrelated
            // name edit on the CTO's own account impossible.
            return newRole != UserRole.SuperAdmin;
        }
    }
}
