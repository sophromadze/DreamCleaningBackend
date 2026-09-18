using System.Text;
using DreamCleaningBackend.Helpers.Commercial;
using DreamCleaningBackend.Helpers.Contracts;
using DreamCleaningBackend.Models.Contracts;
using DreamCleaningBackend.Services.Contracts;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// Section 36 - CANCELLATION, RESCHEDULING AND CONTRACT TERMINATION - is in every new
    /// agreement, states the terms an admin would otherwise have to paste in by hand, and cannot
    /// contradict the operative clauses it summarises.
    ///
    /// The non-contradiction is STRUCTURAL rather than asserted paragraph by paragraph: 36 quotes
    /// every figure through the same {{TOKEN}} the operative clause uses, so both render from one
    /// snapshot value. `TheConsolidatedSectionQuotesTheSameFiguresAsTheClausesItSummarises` proves
    /// it on a contract whose terms are all non-default, which is the case a hardcoded number
    /// would survive.
    /// </summary>
    public class ContractCancellationSectionTests
    {
        static ContractCancellationSectionTests()
        {
            QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  It is part of the agreement, automatically
        // ══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// The section is in the SEEDED TEMPLATE, which is what makes it automatic: every contract
        /// copies the template body into its own snapshot when the draft is built, so an admin
        /// creating a contract gets it without doing anything, and cannot forget it.
        /// </summary>
        [Fact]
        public void TheSeededAgreementCarriesTheConsolidatedSection()
        {
            var body = ContractTemplateSeed.BodyText;

            Assert.Contains("## 36. CANCELLATION, RESCHEDULING AND CONTRACT TERMINATION", body);

            // Every subject the section is required to cover, each as its own lettered paragraph.
            Assert.Contains("(b) Cancelling or rescheduling an individual scheduled visit.", body);
            Assert.Contains("(c) Late cancellation.", body);
            Assert.Contains("(d) Failed access to the Premises.", body);
            Assert.Contains("(e) Cancellation by Contractor.", body);
            Assert.Contains("(f) Emergency exceptions.", body);
            Assert.Contains("(g) Repeated missed visits.", body);
            Assert.Contains("(h) Minimum contractual commitment.", body);
            Assert.Contains("(i) Termination for convenience and required notice.", body);
            Assert.Contains("(j) Early termination for cause, nonpayment and safety.", body);
            Assert.Contains("(k) No early-termination charge.", body);
            Assert.Contains("(l) Prepaid Services and refunds.", body);
            Assert.Contains("(m) Outstanding payment obligations.", body);
            Assert.Contains("(n) Written cancellation and termination notices.", body);
            Assert.Contains("(o) Published policies.", body);
        }

        /// <summary>
        /// The signature block executes 36 sections. A block that names a narrower range than the
        /// document contains is an invitation to argue the extra section was not agreed.
        /// </summary>
        [Fact]
        public void TheSignatureBlockNamesTheSectionItIsSigningFor()
        {
            Assert.Contains("including Sections 1 through 36, Exhibit A and Exhibit B",
                ContractTemplateSeed.BodyText);
            Assert.DoesNotContain("Sections 1 through 35", ContractTemplateSeed.BodyText);
        }

        /// <summary>
        /// A body change is a VERSION BUMP, never an edit in place - the seeder matches on name AND
        /// version and inserts only what is missing, so leaving the number at 2.5 would have left
        /// every existing database issuing agreements without Section 36 while this file said
        /// otherwise.
        /// </summary>
        [Fact]
        public void TheSectionArrivedWithAVersionBump()
        {
            Assert.Equal("2.6", ContractTemplateSeed.TemplateVersion);
            Assert.Contains("2.5", ContractTemplateSeed.SupersededVersions);
            Assert.DoesNotContain(
                ContractTemplateSeed.TemplateVersion, ContractTemplateSeed.SupersededVersions);
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  It cannot contradict the clauses it summarises
        // ══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// THE TEST THAT MATTERS. A consolidated section that restates terms is worth having only
        /// if it cannot drift from them, so this renders a contract whose every relevant term is
        /// NON-DEFAULT and checks that Section 36 quotes those values rather than the standard
        /// ones. A figure hardcoded into the summary would pass against a default contract and
        /// fail here - which is the whole point of doing it this way round.
        /// </summary>
        [Fact]
        public void TheConsolidatedSectionQuotesTheSameFiguresAsTheClausesItSummarises()
        {
            var snapshot = Snapshot();
            snapshot.Advanced.TimelyRescheduleHours = 36;
            snapshot.Advanced.MakeupWindowDays = 21;
            snapshot.Advanced.LockoutWaitMinutes = 25;
            snapshot.Advanced.CurePeriodDays = 20;
            snapshot.Advanced.PastDueDays = 45;
            snapshot.Advanced.ForceMajeureDays = 60;
            snapshot.Advanced.CreditReturnDays = 21;
            snapshot.Advanced.RefundBusinessDays = 7;
            snapshot.Term.TerminationNoticeDays = 45;
            snapshot.Term.InitialTermMonths = 6;
            snapshot.Term.MinimumCommitmentMonths = 3;
            snapshot.Pricing.CancellationPercent = 35m;
            ContractPricingCalculator.Recalculate(snapshot.Pricing);

            var section = SectionThirtySix(snapshot);

            // Each of these is the SPELLED-OUT form the whole agreement uses, so finding it in
            // Section 36 proves the summary went through the same token as the operative clause.
            Assert.Contains("thirty-six (36) hours", section);
            Assert.Contains("twenty-one (21) calendar days", section);
            Assert.Contains("twenty-five (25) minutes", section);
            Assert.Contains("twenty (20) calendar days", section);
            Assert.Contains("forty-five (45) calendar days", section);
            Assert.Contains("sixty (60) consecutive calendar days", section);
            Assert.Contains("seven (7) business days", section);
            Assert.Contains("six (6) months", section);
            Assert.Contains("three (3) calendar months", section);
            Assert.Contains("thirty-five percent (35%) of the pre-tax visit fee", section);

            // And the standard figures are NOT in it, which is what a hardcoded number would leave
            // behind on a contract carrying different terms.
            Assert.DoesNotContain("twenty-four (24) hours", section);
            Assert.DoesNotContain("fourteen (14) calendar days", section);
            Assert.DoesNotContain("fifty percent (50%)", section);
            Assert.DoesNotContain("ten (10) months", section);
        }

        /// <summary>
        /// It is expressly a reference, not a second operative clause. Without this sentence, two
        /// statements of the same term sit in one contract with nothing saying which wins - and
        /// the first edit to either creates a contradiction nobody notices.
        /// </summary>
        [Fact]
        public void TheConsolidatedSectionDefersToTheOperativeClauses()
        {
            var section = SectionThirtySix(Snapshot());

            Assert.Contains("creates no additional right, charge, notice requirement, restriction "
                + "or remedy, and it removes none", section);
            Assert.Contains("control over any inconsistency with the summary in this Section", section);

            // The operative clauses it points at, by number.
            foreach (var reference in new[]
                { "Section 15(a)", "Section 15(b)", "Section 14", "Section 15(e)", "Section 15(d)",
                  "Section 15(f)", "Section 4(b)", "Section 4(a)", "Section 4(d)", "Section 11(f)",
                  "Section 30(b)", "Section 34(c)", "Section 32(a)", "Section 32(b)", "Section 29" })
            {
                Assert.Contains(reference, section);
            }
        }

        /// <summary>
        /// A cap is summarised as a cap. The agreement permits a charge limited to reasonable
        /// documented net loss; a summary that dropped the qualifier would be the first place
        /// anybody reads the term, and would describe a penalty the contract does not impose.
        /// </summary>
        [Fact]
        public void TheSummaryKeepsTheCapQualifiersRatherThanStatingAFee()
        {
            var section = SectionThirtySix(Snapshot());

            Assert.Contains("reasonable, documented net loss after avoided costs and net "
                + "replacement earnings", section);
            Assert.Contains("The cap is a ceiling on proven loss and not an automatic charge", section);
            Assert.Contains("No Client cancellation charge applies where Contractor cannot offer a "
                + "reasonable makeup opportunity", section);
            Assert.Contains("This Agreement provides no early-termination fee, exit charge or "
                + "liquidated sum payable on termination", section);
            Assert.Contains("no remaining fees are automatically accelerated", section);
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  The policy version is recorded, and frozen
        // ══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 36(o) RECORDS the published policy version so the terms accepted at signing stay
        /// identifiable after the website has moved on - and says, in the same paragraph, that the
        /// published policies are not incorporated and do not amend the agreement.
        ///
        /// That second half is the load-bearing one. Incorporating a web page into an executed
        /// contract would let a later edit to that page change what a client already signed, which
        /// is exactly what both the agreement and the published policy say cannot happen.
        /// </summary>
        [Fact]
        public void TheAgreementRecordsThePolicyVersionWithoutIncorporatingIt()
        {
            var section = SectionThirtySix(Snapshot());

            Assert.Contains(CommercialPolicyDocument.PublishedPolicyUrl, section);
            Assert.Contains($"version {CommercialPolicyDocument.Version}", section);
            Assert.Contains(
                CommercialPolicyDocument.FormatEffectiveDate(CommercialPolicyDocument.EffectiveDate),
                section);

            Assert.Contains("are not incorporated into this Agreement and do not amend it", section);
            Assert.Contains("a later revision of the published policies does not change any term of "
                + "this Agreement", section);
        }

        /// <summary>
        /// THE FREEZE. A version generated months ago keeps naming the policy version it was signed
        /// against, whatever the current constant says. Reading the constant at render time instead
        /// would silently rewrite an executed agreement's record of what was in force - the same
        /// failure the frozen snapshot prevents for the client's name and the template body.
        /// </summary>
        [Fact]
        public void AnExecutedContractKeepsThePolicyVersionItWasSignedAgainst()
        {
            var snapshot = Snapshot();
            snapshot.PolicyVersion = "0.9";
            snapshot.PolicyEffectiveDate = "2026-01-05";

            var section = SectionThirtySix(snapshot);

            Assert.Contains("version 0.9", section);
            Assert.Contains("January 5, 2026", section);
            Assert.DoesNotContain($"version {CommercialPolicyDocument.Version}", section);
        }

        /// <summary>
        /// A snapshot with no recorded version - a draft saved in the window between deploying
        /// this and being re-saved - falls back to the current constant rather than printing a
        /// ruled blank. The current version genuinely is the one in force for such a draft, and a
        /// blank would land the field in the preview's unresolved-token banner for no reason.
        /// </summary>
        [Fact]
        public void ASnapshotWithNoRecordedVersionFallsBackToTheCurrentOne()
        {
            var snapshot = Snapshot();
            snapshot.PolicyVersion = string.Empty;
            snapshot.PolicyEffectiveDate = string.Empty;

            var section = SectionThirtySix(snapshot);

            Assert.Contains($"version {CommercialPolicyDocument.Version}", section);
            Assert.DoesNotContain(ContractTextFormat.RuledBlank, section);
        }

        /// <summary>
        /// Every token the new section introduces resolves. An unfillable token renders literally
        /// on purpose elsewhere in this system, which is fine for a site detail an admin can go
        /// and fill in - and not fine for a sentence nobody can edit.
        /// </summary>
        [Fact]
        public void TheConsolidatedSectionLeavesNoUnresolvedToken()
        {
            var rendered = ContractRenderer.Render(Snapshot());
            var section = SectionThirtySix(rendered);

            Assert.DoesNotContain("{{", section);
            Assert.DoesNotContain("}}", section);
            Assert.DoesNotContain("POLICY_VERSION", rendered.UnresolvedTokens);
            Assert.DoesNotContain("POLICY_EFFECTIVE_DATE", rendered.UnresolvedTokens);
            Assert.DoesNotContain("CONTRACTOR_PUBLISHED_POLICY_URL", rendered.UnresolvedTokens);
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  It reaches the preview AND the PDF
        // ══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// The preview a client reads in the browser and the PDF they sign are the same blocks, so
        /// the section reaching one and not the other is not possible by construction - but the
        /// PDF is composed separately and lays out its own headings, so it is proved rather than
        /// assumed. Text is extracted back out of the rendered file, which also catches the
        /// ligature-drop failure that silently ate characters from an earlier executed contract.
        /// </summary>
        [Fact]
        public void TheSectionIsInThePdfAsWellAsThePreview()
        {
            var snapshot = Snapshot();
            var rendered = ContractRenderer.Render(snapshot);

            Assert.Contains(rendered.Blocks, b =>
                b.Kind == ContractBlockKind.Heading
                && b.Text.Contains("36. CANCELLATION, RESCHEDULING AND CONTRACT TERMINATION"));

            var bytes = new ContractPdfService().GenerateDocument(
                rendered, snapshot, SignatureBlock(), certificate: null, draftWatermark: true);

            using var pdf = UglyToad.PdfPig.PdfDocument.Open(bytes);
            var text = new StringBuilder();
            foreach (var page in pdf.GetPages()) text.AppendLine(page.Text);
            var haystack = LettersOnly(text.ToString());

            Assert.Contains(LettersOnly("CANCELLATION, RESCHEDULING AND CONTRACT TERMINATION"), haystack);
            Assert.Contains(LettersOnly("Late cancellation"), haystack);
            Assert.Contains(LettersOnly("No early-termination charge"), haystack);
            Assert.Contains(LettersOnly("Published policies"), haystack);
            Assert.Contains(LettersOnly("Sections 1 through 36"), haystack);
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  Helpers
        // ══════════════════════════════════════════════════════════════════════════════════════

        private static string SectionThirtySix(ContractSnapshot snapshot) =>
            SectionThirtySix(ContractRenderer.Render(snapshot));

        /// <summary>
        /// The rendered text of Section 36 alone. Scoped, because several of the phrases asserted
        /// here legitimately appear in the operative clauses too - finding "Section 15(b)" in the
        /// whole document would prove nothing about the summary.
        /// </summary>
        private static string SectionThirtySix(RenderedContract rendered)
        {
            var sb = new StringBuilder();
            var inside = false;

            foreach (var block in rendered.Blocks)
            {
                if (block.Kind == ContractBlockKind.Heading)
                {
                    inside = block.Text.Contains("36. CANCELLATION, RESCHEDULING AND CONTRACT TERMINATION");
                    if (inside) sb.AppendLine(block.Text);
                    continue;
                }

                if (inside) sb.AppendLine(block.Label is null ? block.Text : $"{block.Label}: {block.Text}");
            }

            Assert.True(sb.Length > 0, "Section 36 was not found in the rendered agreement.");
            return sb.ToString();
        }

        private static string LettersOnly(string value) =>
            new string(value.Where(char.IsLetter).ToArray()).ToLowerInvariant();

        private static DTOs.ContractSignatureBlockDto SignatureBlock() => new()
        {
            Contractor = new DTOs.ContractSignaturePartyDto
            {
                PartyLabel = "CONTRACTOR",
                EntityName = "Nodar Alania Inc. d/b/a Dream Cleaning NYC",
                SignerName = "Nodar Alania",
                SignerTitle = "CEO"
            },
            Client = new DTOs.ContractSignaturePartyDto
            {
                PartyLabel = "CLIENT",
                EntityName = "Northline Holdings Inc.",
                SignerName = "Dana Okafor",
                SignerTitle = "Facilities Director"
            }
        };

        /// <summary>
        /// An ordinary contract on the standard terms. Deliberately minimal - the site details and
        /// scope that <c>ContractRenderingTests</c> fills in do not affect Section 36, and an
        /// unfilled one renders a ruled blank rather than failing.
        /// </summary>
        private static ContractSnapshot Snapshot()
        {
            var snapshot = new ContractSnapshot
            {
                ContractNumber = "DCC-2026-11223344",
                VersionNumber = 1,
                EffectiveDate = new DateTime(2026, 9, 20),
                TemplateBodyText = ContractTemplateSeed.BodyText,
                PremisesType = "office",
                PolicyVersion = CommercialPolicyDocument.Version,
                PolicyEffectiveDate = CommercialPolicyDocument.EffectiveDate,
                Contractor = new ContractorSnapshot
                {
                    LegalEntityName = "Nodar Alania Inc.",
                    Dba = "Dream Cleaning NYC",
                    EntityType = "a New York corporation",
                    Address = "8800 20th Ave, Apt 2B",
                    City = "Brooklyn",
                    State = "NY",
                    Zip = "11214",
                    NoticeEmail = "hello@dreamcleaningnyc.com",
                    Phone = "9299301525"
                },
                Client = new ClientSnapshot
                {
                    LegalEntityName = "Northline Holdings Inc.",
                    EntityType = "a Delaware corporation",
                    PrincipalAddress = "12 Water Street",
                    City = "Jersey City",
                    State = "NJ",
                    Zip = "07302",
                    NoticeEmail = "ap@northline.example"
                },
                ServiceLocation = new ServiceLocationSnapshot
                {
                    LocationName = "Midtown office",
                    Address = "455 Madison Ave, Floor 3",
                    City = "New York",
                    State = "NY",
                    Zip = "10022"
                },
                ContractorSigner = new SignerSnapshot
                {
                    FirstName = "Nodar", LastName = "Alania", Title = "CEO",
                    Email = "hello@dreamcleaningnyc.com"
                },
                ClientSigner = new SignerSnapshot
                {
                    FirstName = "Dana", LastName = "Okafor", Title = "Facilities Director",
                    Email = "dana@northline.example"
                },
                Term = new TermSnapshot { ServiceCommencementDate = new DateTime(2026, 10, 1) },
                Pricing = new PricingSnapshot { PriceMode = ContractPriceMode.PreTax, PriceInput = 400m }
            };

            ContractPricingCalculator.Recalculate(snapshot.Pricing);
            return snapshot;
        }
    }
}
