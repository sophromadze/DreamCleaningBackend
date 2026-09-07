using DreamCleaningBackend.Helpers.Contracts;
using DreamCleaningBackend.Models;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// THE CONTRACTS MATRIX INVERTS THE APP'S ROLE HIERARCHY.
    ///
    /// Everywhere else, SuperAdmin is a superset of Admin. In this one module a titled CEO/CTO
    /// outranks an UNTITLED SuperAdmin, and an untitled SuperAdmin is an ordinary Manager. That is
    /// deliberate and surprising, so it is asserted directly rather than left to be inferred from
    /// controller attributes.
    /// </summary>
    public class ContractPermissionMatrixTests
    {
        // ── resolving authority ────────────────────────────────────────────────

        [Theory]
        [InlineData(UserRole.Admin, OrgTitle.None, ContractAuthority.Manager)]
        [InlineData(UserRole.Admin, OrgTitle.CEO, ContractAuthority.CEO)]
        [InlineData(UserRole.Admin, OrgTitle.CTO, ContractAuthority.CTO)]
        [InlineData(UserRole.SuperAdmin, OrgTitle.CEO, ContractAuthority.CEO)]
        [InlineData(UserRole.SuperAdmin, OrgTitle.CTO, ContractAuthority.CTO)]
        public void AuthorityComesFromTheTitleWhenThereIsOne(
            UserRole role, OrgTitle title, ContractAuthority expected)
        {
            Assert.Equal(expected, ContractPermissionMatrix.Resolve(role, title));
        }

        [Fact]
        public void AnUntitledSuperAdminFallsBackToManager()
        {
            // The deliberate departure. A SuperAdmin with no officer title cannot revise, amend or
            // void a contract, even though they hold every other power in the application.
            Assert.Equal(ContractAuthority.Manager,
                ContractPermissionMatrix.Resolve(UserRole.SuperAdmin, OrgTitle.None));

            Assert.False(ContractPermissionMatrix.Can(
                UserRole.SuperAdmin, OrgTitle.None, ContractAction.DeleteContract));
            Assert.False(ContractPermissionMatrix.Can(
                UserRole.SuperAdmin, OrgTitle.None, ContractAction.CreateRevision));
            Assert.False(ContractPermissionMatrix.Can(
                UserRole.SuperAdmin, OrgTitle.None, ContractAction.BackToEdit));
        }

        [Theory]
        [InlineData(UserRole.Customer)]
        [InlineData(UserRole.Moderator)]
        [InlineData(UserRole.Cleaner)]
        public void NonStaffRolesHaveNoAuthorityInTheModuleAtAll(UserRole role)
        {
            Assert.Null(ContractPermissionMatrix.Resolve(role, OrgTitle.None));
            // Even a title cannot let a customer in — the role gate comes first.
            Assert.Null(ContractPermissionMatrix.Resolve(role, OrgTitle.CTO));
            Assert.False(ContractPermissionMatrix.Can(role, OrgTitle.CTO, ContractAction.ViewContracts));
        }

        // ── the shared day-to-day flow ─────────────────────────────────────────

        [Theory]
        [InlineData(ContractAction.ViewContracts)]
        [InlineData(ContractAction.CreateContract)]
        [InlineData(ContractAction.GeneratePreview)]
        [InlineData(ContractAction.SendForReview)]
        [InlineData(ContractAction.SendForSignature)]
        [InlineData(ContractAction.Duplicate)]
        [InlineData(ContractAction.RegenerateExecutedPdf)]
        [InlineData(ContractAction.ResendExecutedCopy)]
        public void EveryAuthorityCanRunTheOrdinaryFlow(ContractAction action)
        {
            Assert.True(ContractPermissionMatrix.Can(ContractAuthority.Manager, action));
            Assert.True(ContractPermissionMatrix.Can(ContractAuthority.CEO, action));
            Assert.True(ContractPermissionMatrix.Can(ContractAuthority.CTO, action));
        }

        [Fact]
        public void ManagerKeepsBothHalvesOfTheSplitExecutedAction()
        {
            // Regenerate is a silent re-render; re-sending is client-facing but no more so than
            // Send for Review, which a Manager already owns. Withholding only that one would be
            // arbitrary, so the matrix gives them both.
            Assert.True(ContractPermissionMatrix.Can(
                ContractAuthority.Manager, ContractAction.RegenerateExecutedPdf));
            Assert.True(ContractPermissionMatrix.Can(
                ContractAuthority.Manager, ContractAction.ResendExecutedCopy));
        }

        // ── what a Manager may not do ──────────────────────────────────────────

        [Theory]
        [InlineData(ContractAction.BackToEdit)]
        [InlineData(ContractAction.CreateRevision)]
        [InlineData(ContractAction.CreateAmendment)]
        [InlineData(ContractAction.DeleteContract)]
        [InlineData(ContractAction.AssignOrgTitle)]
        [InlineData(ContractAction.SignAsContractor)]
        public void ManagerCannotChangeAnExistingDocumentOrSignForTheContractor(ContractAction action)
        {
            Assert.False(ContractPermissionMatrix.Can(ContractAuthority.Manager, action));
        }

        [Fact]
        public void AManagerWhoHasGeneratedAPreviewCannotFixTheirOwnTypo()
        {
            // Called out explicitly because it is the matrix's sharpest practical consequence and
            // it is intended, not a gap: editing after a preview exists is BackToEdit.
            Assert.True(ContractPermissionMatrix.Can(ContractAuthority.Manager, ContractAction.CreateContract));
            Assert.True(ContractPermissionMatrix.Can(ContractAuthority.Manager, ContractAction.GeneratePreview));
            Assert.False(ContractPermissionMatrix.Can(ContractAuthority.Manager, ContractAction.BackToEdit));
        }

        [Fact]
        public void ManagerCanDuplicateButNotAmend()
        {
            // Both arrive at one endpoint, separated only by a query flag — which is exactly why
            // the controller gates on the flag rather than the route.
            Assert.True(ContractPermissionMatrix.Can(ContractAuthority.Manager, ContractAction.Duplicate));
            Assert.False(ContractPermissionMatrix.Can(ContractAuthority.Manager, ContractAction.CreateAmendment));
        }

        // ── CEO vs CTO: exactly two differences ────────────────────────────────

        [Fact]
        public void CeoAndCtoDifferInExactlyFourPlaces()
        {
            // NOTE ON THE SPEC: its prose said CEO and CTO differ in "exactly two" places — what
            // was then Void, plus AssignOrgTitle — while its permission TABLE also withheld the
            // business flag from the CEO. The table was the authoritative instruction. Void has
            // since become the Delete/Restore pair, which is one housekeeping decision expressed
            // as two actions, so the count is four.
            //
            // If any further difference appears, the spec changed and this test should be the
            // thing that says so.
            var differences = Enum.GetValues<ContractAction>()
                .Where(a => ContractPermissionMatrix.Can(ContractAuthority.CEO, a)
                         != ContractPermissionMatrix.Can(ContractAuthority.CTO, a))
                .ToList();

            Assert.Equal(4, differences.Count);
            Assert.Contains(ContractAction.DeleteContract, differences);
            Assert.Contains(ContractAction.RestoreContract, differences);
            Assert.Contains(ContractAction.AssignOrgTitle, differences);
            Assert.Contains(ContractAction.ToggleBusinessFlag, differences);
        }

        [Fact]
        public void CeoAndCtoAreIdenticalOnEveryCONTRACTAction()
        {
            // The prose's claim, made precise: across everything that acts on the contract DOCUMENT,
            // the two officers are interchangeable. Only deleting and restoring separate them, and
            // that is housekeeping rather than a contract capability.
            var contractActions = Enum.GetValues<ContractAction>()
                .Where(a => a is not (ContractAction.AssignOrgTitle or ContractAction.ToggleBusinessFlag
                    or ContractAction.DeleteContract or ContractAction.RestoreContract));

            var differences = contractActions
                .Where(a => ContractPermissionMatrix.Can(ContractAuthority.CEO, a)
                         != ContractPermissionMatrix.Can(ContractAuthority.CTO, a))
                .ToList();

            Assert.Empty(differences);
        }

        [Fact]
        public void OnlyTheCtoMayDeleteRestoreOrAssignTitles()
        {
            Assert.True(ContractPermissionMatrix.Can(ContractAuthority.CTO, ContractAction.RestoreContract));
            Assert.False(ContractPermissionMatrix.Can(ContractAuthority.CEO, ContractAction.RestoreContract));
            Assert.True(ContractPermissionMatrix.Can(ContractAuthority.CTO, ContractAction.DeleteContract));
            Assert.True(ContractPermissionMatrix.Can(ContractAuthority.CTO, ContractAction.AssignOrgTitle));

            Assert.False(ContractPermissionMatrix.Can(ContractAuthority.CEO, ContractAction.DeleteContract));
            Assert.False(ContractPermissionMatrix.Can(ContractAuthority.CEO, ContractAction.AssignOrgTitle));
        }

        [Fact]
        public void TheCeoHasEveryOtherCapabilityTheCtoHas()
        {
            foreach (var action in Enum.GetValues<ContractAction>())
            {
                if (action is ContractAction.DeleteContract or ContractAction.RestoreContract
                    or ContractAction.AssignOrgTitle or ContractAction.ToggleBusinessFlag) continue;

                Assert.Equal(
                    ContractPermissionMatrix.Can(ContractAuthority.CTO, action),
                    ContractPermissionMatrix.Can(ContractAuthority.CEO, action));
            }
        }

        // ── the business flag ──────────────────────────────────────────────────

        [Fact]
        public void TheBusinessFlagIsManagerAndCtoButNotCeo()
        {
            // The one action a Manager holds and a CEO does not. Flagging an account is
            // operational record-keeping rather than an officer decision.
            Assert.True(ContractPermissionMatrix.Can(
                ContractAuthority.Manager, ContractAction.ToggleBusinessFlag));
            Assert.True(ContractPermissionMatrix.Can(
                ContractAuthority.CTO, ContractAction.ToggleBusinessFlag));
            Assert.False(ContractPermissionMatrix.Can(
                ContractAuthority.CEO, ContractAction.ToggleBusinessFlag));
        }

        // ── signing ────────────────────────────────────────────────────────────

        [Fact]
        public void OnlyOfficersMaySignAsTheContractorFromInsideTheApp()
        {
            Assert.True(ContractPermissionMatrix.Can(ContractAuthority.CEO, ContractAction.SignAsContractor));
            Assert.True(ContractPermissionMatrix.Can(ContractAuthority.CTO, ContractAction.SignAsContractor));
            // A Manager is never a contractor signer in the first place.
            Assert.False(ContractPermissionMatrix.Can(ContractAuthority.Manager, ContractAction.SignAsContractor));
        }
    }

    /// <summary>
    /// OFFICER TITLES: BOOTSTRAP, THEN LOCKED.
    ///
    /// The rule has to solve a chicken-and-egg problem — only a CTO may grant CTO, but on a fresh
    /// database no CTO exists — without leaving a permanent hole. It does that by deriving the mode
    /// from the data: no CTO means the hatch is open, and the moment one exists it shuts.
    /// </summary>
    public class OrgTitlePolicyTests
    {
        private const int Nika = 10;
        private const int Nodar = 11;
        private const int OtherSuperAdmin = 12;
        private const int Nugzar = 20;

        [Fact]
        public void BootstrapIsOpenWhileNoCtoExists()
        {
            Assert.True(OrgTitlePolicy.IsBootstrapMode(null));
            Assert.False(OrgTitlePolicy.IsBootstrapMode(Nika));
        }

        [Fact]
        public void InBootstrapASuperAdminMayGrantThemselvesCto()
        {
            // The whole point: this is how the first CTO comes into being.
            Assert.Equal(OrgTitleAssignmentResult.Allowed,
                OrgTitlePolicy.Evaluate(Nika, UserRole.SuperAdmin, null, UserRole.SuperAdmin));
        }

        [Fact]
        public void InBootstrapASuperAdminMayGrantAnotherSuperAdmin()
        {
            Assert.Equal(OrgTitleAssignmentResult.Allowed,
                OrgTitlePolicy.Evaluate(Nika, UserRole.SuperAdmin, null, UserRole.SuperAdmin));
        }

        [Fact]
        public void BootstrapCannotMintAnOfficerOutOfAPlainAdmin()
        {
            // The escape hatch is narrow on purpose — it can only ever hand a title to a
            // SuperAdmin, so it is not a route to promoting an ordinary admin.
            Assert.Equal(OrgTitleAssignmentResult.BootstrapTargetMustBeSuperAdmin,
                OrgTitlePolicy.Evaluate(Nika, UserRole.SuperAdmin, null, UserRole.Admin));
        }

        [Fact]
        public void BootstrapIsNotOpenToANonSuperAdmin()
        {
            Assert.Equal(OrgTitleAssignmentResult.BootstrapRequiresSuperAdmin,
                OrgTitlePolicy.Evaluate(Nugzar, UserRole.Admin, null, UserRole.SuperAdmin));
        }

        [Fact]
        public void OnceACtoExistsOnlyThatPersonMayAssignTitles()
        {
            Assert.Equal(OrgTitleAssignmentResult.Allowed,
                OrgTitlePolicy.Evaluate(Nika, UserRole.SuperAdmin, currentCtoUserId: Nika, UserRole.SuperAdmin));

            // Another SuperAdmin keeps every other power in the app and loses this one.
            Assert.Equal(OrgTitleAssignmentResult.LockedToExistingCto,
                OrgTitlePolicy.Evaluate(OtherSuperAdmin, UserRole.SuperAdmin, currentCtoUserId: Nika, UserRole.SuperAdmin));
        }

        [Fact]
        public void EvenTheCeoCannotAssignTitlesOnceACtoExists()
        {
            // Being an officer is not the test; being THE CTO is.
            Assert.Equal(OrgTitleAssignmentResult.LockedToExistingCto,
                OrgTitlePolicy.Evaluate(Nodar, UserRole.SuperAdmin, currentCtoUserId: Nika, UserRole.SuperAdmin));
        }

        [Fact]
        public void TheCtoMayHandTheTitleToAPlainAdminOnceLockedIn()
        {
            // The SuperAdmin-target restriction belongs to bootstrap only. A sitting CTO is
            // trusted to decide who holds a title, including reassigning their own.
            Assert.Equal(OrgTitleAssignmentResult.Allowed,
                OrgTitlePolicy.Evaluate(Nika, UserRole.SuperAdmin, currentCtoUserId: Nika, UserRole.Admin));
        }

        [Fact]
        public void ClearingTheLastCtoReopensBootstrapAutomatically()
        {
            // There is no flag to reset — the mode is derived from whether a CTO row exists, so
            // deleting or clearing the CTO account cannot leave the system permanently locked.
            Assert.Equal(OrgTitleAssignmentResult.LockedToExistingCto,
                OrgTitlePolicy.Evaluate(OtherSuperAdmin, UserRole.SuperAdmin, Nika, UserRole.SuperAdmin));

            Assert.Equal(OrgTitleAssignmentResult.Allowed,
                OrgTitlePolicy.Evaluate(OtherSuperAdmin, UserRole.SuperAdmin, null, UserRole.SuperAdmin));
        }

        [Fact]
        public void EveryRefusalCarriesAnActionableSentence()
        {
            foreach (var result in Enum.GetValues<OrgTitleAssignmentResult>())
            {
                if (result == OrgTitleAssignmentResult.Allowed) continue;
                var message = OrgTitlePolicy.DescribeRefusal(result);
                Assert.False(string.IsNullOrWhiteSpace(message));
                Assert.EndsWith(".", message);
            }
        }
    }
}
