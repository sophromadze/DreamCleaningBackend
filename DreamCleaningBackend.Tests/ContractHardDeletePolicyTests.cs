using DreamCleaningBackend.Helpers.Contracts;
using DreamCleaningBackend.Models.Contracts;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// WHICH CONTRACTS MAY BE DESTROYED, and which may only be archived.
    ///
    /// Pure, so every rule is asserted without a database. Both callers go through this file — the
    /// admin's Full delete button and the retention sweep — which is the point: before it existed
    /// the sweep hard-deleted anything archived for six months, executed agreements included.
    /// </summary>
    public class ContractHardDeletePolicyTests
    {
        private static ContractDeletionFacts Clean(ContractStatus status = ContractStatus.Draft) =>
            new()
            {
                Status = status,
                SignatureCount = 0,
                LinkedInvoiceCount = 0,
                LinkedRecurringTemplateCount = 0,
                DerivedContractCount = 0
            };

        // ── what MAY be destroyed ──────────────────────────────────────────────

        /// <summary>
        /// The whole point of the feature: a test contract somebody drafted, previewed and maybe
        /// even sent, that nobody ever signed, can be cleared away completely.
        ///
        /// Note AwaitingSignatures and ReadyForSignature in this list. THE TEST IS EVIDENCE, NOT
        /// VOCABULARY - a contract sent to a client who never opened it carries nothing, whatever
        /// its status looks like, and those are exactly the test contracts that accumulate.
        /// </summary>
        [Theory]
        [InlineData(ContractStatus.Draft)]
        [InlineData(ContractStatus.PreviewGenerated)]
        [InlineData(ContractStatus.AwaitingClientReview)]
        [InlineData(ContractStatus.NeedsRevision)]
        [InlineData(ContractStatus.ReadyForSignature)]
        [InlineData(ContractStatus.AwaitingSignatures)]
        [InlineData(ContractStatus.Expired)]
        [InlineData(ContractStatus.Voided)]
        public void AnUnsignedContractWithNothingHangingOffItMayBeDestroyed(ContractStatus status)
        {
            Assert.Null(ContractHardDeletePolicy.DescribeBlocker(Clean(status)));
            Assert.True(ContractHardDeletePolicy.CanHardDelete(Clean(status)));
        }

        // ── signatures ─────────────────────────────────────────────────────────

        /// <summary>
        /// ONE SIGNATURE IS ENOUGH, and it outranks the status entirely.
        ///
        /// A PartiallySigned contract carries a real person's mark on a real document. That is the
        /// artefact the whole module exists to produce, and it is never destroyed by a button.
        /// </summary>
        [Fact]
        public void ASingleSignatureBlocksDestruction()
        {
            var facts = Clean(ContractStatus.PartiallySigned) with { SignatureCount = 1 };

            var blocker = ContractHardDeletePolicy.DescribeBlocker(facts);

            Assert.NotNull(blocker);
            Assert.Contains("signature", blocker, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Archive it instead", blocker);
        }

        /// <summary>The sentence counts, so an admin knows how much is being protected.</summary>
        [Fact]
        public void TwoSignaturesAreReportedAsTwo()
        {
            var facts = Clean(ContractStatus.FullySigned) with { SignatureCount = 2 };

            Assert.Contains("2 signatures", ContractHardDeletePolicy.DescribeBlocker(facts));
        }

        /// <summary>
        /// The status is not the half to trust silently if the two ever disagree. An executed
        /// contract with no signature rows should not exist; if one does, refuse rather than
        /// destroy it and find out afterwards.
        /// </summary>
        [Theory]
        [InlineData(ContractStatus.FullySigned)]
        [InlineData(ContractStatus.Completed)]
        public void AnExecutedStatusBlocksEvenWithNoSignatureRows(ContractStatus status)
        {
            var blocker = ContractHardDeletePolicy.DescribeBlocker(Clean(status));

            Assert.NotNull(blocker);
            Assert.Contains("executed", blocker, StringComparison.OrdinalIgnoreCase);
        }

        // ── orphan guards ──────────────────────────────────────────────────────

        /// <summary>
        /// AN INVOICE RAISED AGAINST THIS CONTRACT BLOCKS IT.
        ///
        /// CommercialInvoice.ContractId is mapped SetNull, so deleting the contract would NOT
        /// fail - it would quietly detach a real financial document from the agreement it was
        /// raised under. A refusal an admin can act on beats a silent orphan.
        /// </summary>
        [Fact]
        public void ALinkedInvoiceBlocksDestruction()
        {
            var facts = Clean() with { LinkedInvoiceCount = 1 };

            var blocker = ContractHardDeletePolicy.DescribeBlocker(facts);

            Assert.NotNull(blocker);
            Assert.Contains("invoice", blocker, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void SeveralLinkedInvoicesAreCounted()
        {
            var facts = Clean() with { LinkedInvoiceCount = 3 };

            Assert.Contains("3 invoices", ContractHardDeletePolicy.DescribeBlocker(facts));
        }

        /// <summary>Same SetNull orphan hazard, one table over.</summary>
        [Fact]
        public void ARecurringBillingTemplateBlocksDestruction()
        {
            var facts = Clean() with { LinkedRecurringTemplateCount = 1 };

            Assert.Contains("recurring billing template",
                ContractHardDeletePolicy.DescribeBlocker(facts)!);
        }

        /// <summary>
        /// DuplicatedFromContractId is a plain column with NO foreign key, so this one cannot fail
        /// at the database either. It would leave an amendment pointing at a contract nobody can
        /// open - and "amended from" is the sentence that explains why the amendment reads the way
        /// it does.
        /// </summary>
        [Fact]
        public void AnAmendmentBuiltFromThisContractBlocksDestruction()
        {
            var facts = Clean() with { DerivedContractCount = 1 };

            var blocker = ContractHardDeletePolicy.DescribeBlocker(facts);

            Assert.NotNull(blocker);
            Assert.Contains("duplicate or amendment", blocker);
        }

        // ── ordering ───────────────────────────────────────────────────────────

        /// <summary>
        /// When several things protect a contract, the SIGNATURE is what the admin is told about.
        /// It is the one they cannot resolve by deleting something else first, so leading with an
        /// invoice would send them to delete a bill for no reason.
        /// </summary>
        [Fact]
        public void TheSignatureIsReportedAheadOfEverythingElse()
        {
            var facts = Clean(ContractStatus.FullySigned) with
            {
                SignatureCount = 2,
                LinkedInvoiceCount = 4,
                DerivedContractCount = 1
            };

            Assert.Contains("signatures", ContractHardDeletePolicy.DescribeBlocker(facts)!);
        }

        /// <summary>Every blocker points at the alternative, so no refusal is a dead end.</summary>
        [Theory]
        [InlineData(1, 0, 0, 0)]
        [InlineData(0, 1, 0, 0)]
        [InlineData(0, 0, 1, 0)]
        [InlineData(0, 0, 0, 1)]
        public void EveryRefusalNamesArchiveAsTheWayForward(
            int signatures, int invoices, int templates, int derived)
        {
            var facts = Clean() with
            {
                SignatureCount = signatures,
                LinkedInvoiceCount = invoices,
                LinkedRecurringTemplateCount = templates,
                DerivedContractCount = derived
            };

            var blocker = ContractHardDeletePolicy.DescribeBlocker(facts);

            Assert.NotNull(blocker);
            Assert.Contains("archive", blocker, StringComparison.OrdinalIgnoreCase);
        }
    }
}
