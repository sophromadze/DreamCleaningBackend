using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Helpers.Contracts
{
    /// <summary>Why an officer-title assignment was refused, so the caller can say something useful.</summary>
    public enum OrgTitleAssignmentResult
    {
        Allowed = 0,
        /// <summary>A CTO exists and the requester is not that person.</summary>
        LockedToExistingCto = 1,
        /// <summary>Bootstrap mode, but the requester is not a SuperAdmin.</summary>
        BootstrapRequiresSuperAdmin = 2,
        /// <summary>Bootstrap mode, but the target account is not a SuperAdmin.</summary>
        BootstrapTargetMustBeSuperAdmin = 3
    }

    /// <summary>
    /// WHO MAY ASSIGN OFFICER TITLES. Two modes, and which one applies is decided by the data at
    /// request time rather than by anything on the requester.
    ///
    /// <b>Bootstrap</b> — while NO account holds <see cref="OrgTitle.CTO"/>, any SuperAdmin may
    /// assign a title to themselves or to another SuperAdmin. This exists so the first CTO can
    /// come into being at all: the locked rule below requires a CTO to grant CTO, which would
    /// otherwise be unreachable on a fresh database.
    ///
    /// <b>Locked</b> — the moment a CTO exists, only that person may assign or clear any title,
    /// including handing CTO to someone else. Other SuperAdmins keep full SuperAdmin powers
    /// everywhere else in the app and simply lose this one.
    ///
    /// Clearing the last CTO drops the system back to bootstrap automatically — there is no flag
    /// to reset, because the mode is derived from whether a CTO row exists.
    /// </summary>
    public static class OrgTitlePolicy
    {
        /// <summary>
        /// <paramref name="currentCtoUserId"/> is the id of the account holding CTO right now, or
        /// null when none does. <paramref name="targetRole"/> is the role of the account being
        /// changed, which only matters in bootstrap mode.
        /// </summary>
        public static OrgTitleAssignmentResult Evaluate(
            int requesterUserId,
            UserRole requesterRole,
            int? currentCtoUserId,
            UserRole targetRole)
        {
            if (currentCtoUserId.HasValue)
            {
                // Locked: identity is the whole test. Being a SuperAdmin is not enough, and being
                // the CEO is not enough either.
                return currentCtoUserId.Value == requesterUserId
                    ? OrgTitleAssignmentResult.Allowed
                    : OrgTitleAssignmentResult.LockedToExistingCto;
            }

            if (requesterRole != UserRole.SuperAdmin)
                return OrgTitleAssignmentResult.BootstrapRequiresSuperAdmin;

            // Bootstrap is narrow on purpose: it can only ever hand a title to a SuperAdmin, so
            // the escape hatch cannot be used to mint an officer out of a regular admin account.
            if (targetRole != UserRole.SuperAdmin)
                return OrgTitleAssignmentResult.BootstrapTargetMustBeSuperAdmin;

            return OrgTitleAssignmentResult.Allowed;
        }

        /// <summary>True when no CTO exists and the escape hatch is therefore open.</summary>
        public static bool IsBootstrapMode(int? currentCtoUserId) => !currentCtoUserId.HasValue;

        public static string DescribeRefusal(OrgTitleAssignmentResult result) => result switch
        {
            OrgTitleAssignmentResult.LockedToExistingCto =>
                "Only the account currently holding the CTO title can assign or clear officer titles.",
            OrgTitleAssignmentResult.BootstrapRequiresSuperAdmin =>
                "Officer titles can only be assigned by a SuperAdmin until a CTO exists.",
            OrgTitleAssignmentResult.BootstrapTargetMustBeSuperAdmin =>
                "Until a CTO exists, an officer title can only be given to a SuperAdmin account.",
            _ => "This officer title change is not permitted."
        };
    }
}
