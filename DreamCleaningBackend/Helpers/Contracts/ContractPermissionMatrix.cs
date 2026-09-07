using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Helpers.Contracts
{
    /// <summary>The three authority levels the Contracts module recognises.</summary>
    public enum ContractAuthority
    {
        /// <summary>Any staff account with no officer title. Includes an UNTITLED SuperAdmin.</summary>
        Manager = 0,
        CEO = 1,
        CTO = 2
    }

    /// <summary>Every gated action in the Contracts module.</summary>
    public enum ContractAction
    {
        ViewContracts,
        ToggleBusinessFlag,
        AssignOrgTitle,
        CreateContract,
        GeneratePreview,
        SendForReview,
        SendForSignature,
        Duplicate,
        RegenerateExecutedPdf,
        ResendExecutedCopy,
        BackToEdit,
        CreateRevision,
        CreateAmendment,
        DeleteContract,
        RestoreContract,
        SignAsContractor
    }

    /// <summary>
    /// THE CONTRACTS PERMISSION MATRIX. This module does NOT follow the app's normal role
    /// hierarchy, and that inversion is the whole point of the file.
    ///
    /// Everywhere else, SuperAdmin is a superset of Admin. Here, authority comes from the officer
    /// title on the account: a CEO or CTO outranks an untitled SuperAdmin, and an untitled
    /// SuperAdmin is treated as an ordinary Manager. Nothing outside Contracts consults this — the
    /// meaning of SuperAdmin elsewhere is completely unchanged.
    ///
    /// Pure and table-driven so the matrix can be read at a glance and asserted directly, the same
    /// shape as the other policy helpers in this folder.
    /// </summary>
    public static class ContractPermissionMatrix
    {
        /// <summary>
        /// Which authority level an account carries, or null when it has no business in the module
        /// at all (customers, cleaners, and Moderators — who hold View elsewhere but were never
        /// given the Contracts controllers).
        ///
        /// The deliberate departure: <see cref="UserRole.SuperAdmin"/> with
        /// <see cref="OrgTitle.None"/> resolves to <see cref="ContractAuthority.Manager"/>, not to
        /// the top of the tree. An untitled SuperAdmin therefore cannot revise, amend or void a
        /// contract until somebody grants them a title.
        /// </summary>
        public static ContractAuthority? Resolve(UserRole role, OrgTitle title)
        {
            if (role is not (UserRole.Admin or UserRole.SuperAdmin)) return null;

            return title switch
            {
                OrgTitle.CEO => ContractAuthority.CEO,
                OrgTitle.CTO => ContractAuthority.CTO,
                _ => ContractAuthority.Manager
            };
        }

        /// <summary>
        /// The matrix itself. CEO and CTO differ in exactly two places — assigning officer titles
        /// and voiding a contract, both CTO-only. Everything else a CEO may do, a CTO may do, and
        /// vice versa.
        /// </summary>
        public static bool Can(ContractAuthority authority, ContractAction action) => action switch
        {
            // ── Read, and the ordinary day-to-day flow: everybody ──────────────
            ContractAction.ViewContracts => true,
            ContractAction.CreateContract => true,
            ContractAction.GeneratePreview => true,
            ContractAction.SendForReview => true,
            ContractAction.SendForSignature => true,
            ContractAction.Duplicate => true,

            // Re-rendering the executed PDF changes nothing and sends nothing, so it is safe for
            // anyone who can see the contract. Re-SENDING the executed copy is a separate action
            // (below) precisely so this one can stay harmless.
            ContractAction.RegenerateExecutedPdf => true,

            // Mailing the executed copy again is client-facing, but so are Send for Review and
            // Send for Signature, which a Manager already owns. Withholding only this one would be
            // arbitrary.
            ContractAction.ResendExecutedCopy => true,

            // ── Changing an existing document: officers only ───────────────────
            // A Manager who has generated a preview cannot fix even a typo afterwards. That is
            // intentional and specified, not an oversight.
            ContractAction.BackToEdit => authority is ContractAuthority.CEO or ContractAuthority.CTO,
            ContractAction.CreateRevision => authority is ContractAuthority.CEO or ContractAuthority.CTO,
            ContractAction.CreateAmendment => authority is ContractAuthority.CEO or ContractAuthority.CTO,

            // Signing as the contractor is only ever offered to the account actually named as that
            // contract's contractor signer; the authority check is the outer gate.
            ContractAction.SignAsContractor => authority is ContractAuthority.CEO or ContractAuthority.CTO,

            // ── CTO-only ───────────────────────────────────────────────────────
            // Delete replaced Void (2026-09) at the same restriction level. Restore is the same
            // decision in reverse and is held by the same person.
            ContractAction.DeleteContract => authority == ContractAuthority.CTO,
            ContractAction.RestoreContract => authority == ContractAuthority.CTO,

            // The STEADY-STATE rule for officer titles. It is not what the assignment endpoint
            // enforces, and that is deliberate: OrgTitlePolicy is, because it also has to cover
            // the bootstrap case where no CTO exists yet and this rule would make the first CTO
            // impossible to create. Read this row as "once a CTO exists", and OrgTitlePolicy as
            // the whole truth.
            ContractAction.AssignOrgTitle => authority == ContractAuthority.CTO,

            // ── Manager and CTO, but NOT CEO ───────────────────────────────────
            // Flagging an account as a business is operational record-keeping rather than an
            // officer decision, and the matrix deliberately leaves the CEO out of it.
            ContractAction.ToggleBusinessFlag => authority is ContractAuthority.Manager or ContractAuthority.CTO,

            _ => false
        };

        /// <summary>Convenience for a caller that has the raw role/title pair.</summary>
        public static bool Can(UserRole role, OrgTitle title, ContractAction action)
        {
            var authority = Resolve(role, title);
            return authority.HasValue && Can(authority.Value, action);
        }
    }
}
