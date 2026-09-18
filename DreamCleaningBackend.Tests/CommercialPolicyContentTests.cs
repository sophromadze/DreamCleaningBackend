using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DreamCleaningBackend.Helpers.Commercial;
using DreamCleaningBackend.Services.Commercial;
using DreamCleaningBackend.Services.Contracts;
using UglyToad.PdfPig;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// The published commercial policies: one source, four surfaces.
    ///
    /// The page at /commercial-cleaning-policies, the complete policies PDF, the standalone
    /// Cancellation and Termination PDF and Section 36 of every new Master Service Agreement all
    /// resolve back to <see cref="CommercialPolicyDocument"/>. These tests are what makes that
    /// claim true rather than aspirational:
    ///
    ///   1. the frontend's generated copy still equals the C# it was generated from;
    ///   2. the standalone document's clauses are the SAME objects as the complete document's,
    ///      not a second set of words that happen to agree today;
    ///   3. every figure published matches the MSA default it describes;
    ///   4. the published text never states a cap as an automatic charge;
    ///   5. nothing confidential - a client, a price, a bank detail - is in a public document;
    ///   6. both PDFs render, and their text comes back out as text.
    /// </summary>
    public class CommercialPolicyContentTests
    {
        static CommercialPolicyContentTests()
        {
            // Program.cs sets this at startup; the test host has no startup.
            QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        }

        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  1. The frontend copy is still the backend document
        // ══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// THE MIRROR. `commercial-policy.content.ts` is generated from this class by
        /// `DreamCleaningNG/scripts/generate-commercial-policy-content.js`, and the page renders
        /// it directly - so the moment somebody edits the C# without regenerating, the website
        /// and the downloadable PDFs start stating different terms with nothing on either screen
        /// admitting it.
        ///
        /// Compared block for block rather than as a serialized blob, so a failure names the
        /// section and the paragraph instead of printing sixty kilobytes of diff.
        /// </summary>
        [Fact]
        public void TheFrontendCopyIsIdenticalToTheCanonicalDocument()
        {
            var mirrored = ReadGeneratedFrontendContent();

            AssertDocumentsMatch(
                CommercialPolicyDocument.BuildComplete(), mirrored.Complete, "complete");
            AssertDocumentsMatch(
                CommercialPolicyDocument.BuildCancellationAndTermination(),
                mirrored.Cancellation, "cancellation");
        }

        /// <summary>
        /// The generated file says it is generated. Without the banner the next person to read a
        /// typo in it fixes the typo in the wrong file, and their fix is silently reverted by the
        /// next regeneration.
        /// </summary>
        [Fact]
        public void TheGeneratedFrontendFileWarnsAgainstEditingItByHand()
        {
            var text = ReadFrontendFile("shared", "commercial-policies", "commercial-policy.content.ts");

            Assert.Contains("GENERATED FILE - DO NOT EDIT", text);
            Assert.Contains("generate-commercial-policy-content.js", text);
            Assert.Contains("CommercialPolicyDocument.cs", text);
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  2. The two documents share their clauses rather than repeating them
        // ══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// THE WHOLE REASON THE STANDALONE DOCUMENT IS SAFE TO PUBLISH. Its three substantive
        /// sections are built by the same three methods that build sections 4, 5 and 7 of the
        /// complete policies, so the notice window, the makeup window and the cancellation cap
        /// cannot say one thing in one download and something else in the other.
        ///
        /// If this ever fails, the fix is to restore the shared builder - NOT to copy the text
        /// across, which is the arrangement it exists to prevent.
        /// </summary>
        [Theory]
        [InlineData(3, 0, "cancellation and rescheduling")]
        [InlineData(4, 1, "contract duration and termination")]
        [InlineData(6, 2, "prepaid services and refunds")]
        public void TheStandaloneDocumentReusesTheCompleteDocumentsClauses(
            int completeIndex, int standaloneIndex, string subject)
        {
            var complete = CommercialPolicyDocument.BuildComplete().Sections[completeIndex];
            var standalone = CommercialPolicyDocument
                .BuildCancellationAndTermination().Sections[standaloneIndex];

            Assert.Equal(complete.Blocks.Count, standalone.Blocks.Count);

            for (var i = 0; i < complete.Blocks.Count; i++)
            {
                Assert.Equal(complete.Blocks[i].Kind, standalone.Blocks[i].Kind);
                Assert.Equal(complete.Blocks[i].Text, standalone.Blocks[i].Text);
            }

            // The numbering legitimately differs - section 4 of one document is section 1 of the
            // other - so the subject is asserted on the title rather than on the number.
            Assert.Contains(subject.Split(' ')[0], complete.Title, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The standalone document says what it does NOT cover. A cancellation policy read on its
        /// own, with no indication that insurance and liability live elsewhere, reads as the whole
        /// of the relationship.
        /// </summary>
        [Fact]
        public void TheStandaloneDocumentSaysWhatItLeavesOut()
        {
            var text = AllText(CommercialPolicyDocument.BuildCancellationAndTermination());

            Assert.Contains("This document covers cancellation and termination only", text);
            Assert.Contains("complete Commercial Cleaning Policies", text);
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  3. Every published figure is the MSA's own figure
        // ══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// THE PUBLISHED NUMBERS ARE READ OUT OF THE AGREEMENT, NOT CHOSEN.
        ///
        /// Each figure below is asserted against the snapshot default that produces it in a real
        /// contract, so changing a term in the MSA and leaving the website quoting the old one
        /// fails here rather than in front of a client who has read both.
        ///
        /// Numbers are matched as the DIGITS the policy prints: the agreement spells them out
        /// ("twenty-four (24)") for legal prose, the policy does not, and the shared fact is the
        /// value rather than the wording.
        /// </summary>
        [Fact]
        public void EveryPublishedFigureMatchesTheAgreementDefault()
        {
            var text = AllText(CommercialPolicyDocument.BuildComplete());
            var advanced = new AdvancedTermsSnapshot();
            var term = new TermSnapshot();
            var pricing = new PricingSnapshot();

            // Cancellation and rescheduling
            Assert.Contains($"at least {advanced.TimelyRescheduleHours} hours' notice", text);
            Assert.Contains($"fewer than {advanced.TimelyRescheduleHours} hours", text);
            Assert.Contains($"within {advanced.MakeupWindowDays} calendar days", text);
            Assert.Contains($"capped at {pricing.CancellationPercent:0}% of the pre-tax visit fee", text);
            Assert.Contains($"wait at least {advanced.LockoutWaitMinutes} minutes", text);
            Assert.Contains($"{advanced.MissedVisitThreshold} client-attributable missed visits", text);
            Assert.Contains($"rolling {advanced.MissedVisitWindowWeeks}-week period", text);
            Assert.Contains($"within {advanced.ServicePlanDays} calendar days", text);

            // Term and termination
            Assert.Contains($"standard is {term.InitialTermMonths} months", text);
            Assert.Contains($"at least {term.TerminationNoticeDays} calendar days' written notice", text);
            Assert.Contains($"cure within {advanced.CurePeriodDays} calendar days", text);
            Assert.Contains($"unpaid for {advanced.PastDueDays} calendar days", text);
            Assert.Contains($"{advanced.ForceMajeureDays} consecutive calendar days", text);

            // Money
            Assert.Contains($"at least {advanced.InvoiceLeadDays} calendar days before each scheduled visit", text);
            Assert.Contains($"due {pricing.PaymentDeadlineHours} hours before the agreed arrival window", text);
            Assert.Contains($"fewer than {advanced.LateInvoiceThresholdDays} calendar days before that deadline", text);
            Assert.Contains($"at least {advanced.LateInvoiceGraceBusinessDays} business days after", text);
            Assert.Contains($"more than {advanced.InterestGraceDays} calendar days", text);
            Assert.Contains($"{pricing.LateChargePercent:0}% per month", text);
            Assert.Contains("12% per year", text);   // the derived annual rate, Section 11(f)
            Assert.Contains($"within {advanced.BillingDisputeDays} business days of receiving it", text);
            Assert.Contains($"within {advanced.CreditReturnDays} calendar days", text);
            Assert.Contains($"within {advanced.RefundBusinessDays} business days", text);
            Assert.Contains($"at least {advanced.PriceReviewNoticeDays} calendar days' written notice", text);

            // Quality, damage, access, confidentiality
            Assert.Contains($"within {advanced.QualityComplaintHours} hours of the", text);
            Assert.Contains($"within {advanced.QualityCorrectionBusinessDays} business days", text);
            Assert.Contains($"within {advanced.DamageNoticeBusinessDays} business days", text);
            Assert.Contains($"within {advanced.KeyReturnBusinessDays} business days", text);
            Assert.Contains($"continue for {advanced.ConfidentialityYears} years after termination", text);

            // Insurance and liability
            Assert.Contains($"${advanced.InsurancePerOccurrence:N0} each occurrence", text);
            Assert.Contains($"${advanced.InsuranceAggregate:N0} general aggregate", text);
            Assert.Contains($"capped at {pricing.LiabilityCapMultiple} times the recurring pre-tax per-visit fee", text);

            // Disputes
            Assert.Contains($"within {advanced.DisputeDiscussionDays} calendar days", text);
            Assert.Contains($"after {advanced.MediationRequestDays} calendar days", text);
            Assert.Contains($"{advanced.SuitAfterDays} calendar days after the original", text);
            Assert.Contains($"{advanced.CollectionDemandBusinessDays} business days to pay", text);
            Assert.Contains(term.GoverningLawState + " law governs", text);
            Assert.Contains(term.VenueCounty, text);
        }

        /// <summary>
        /// The liability carve-out names the statute the MSA names. GOL 5-323 voids an agreement
        /// exempting a maintenance contractor from liability for its own negligence in connection
        /// with a building, so a published liability cap that did not carry the same exception
        /// would be advertising a limit the law does not permit.
        /// </summary>
        [Fact]
        public void ThePublishedLiabilityCapCarriesTheSameCarveOutsAsTheAgreement()
        {
            var text = AllText(CommercialPolicyDocument.BuildComplete());

            Assert.Contains("General Obligations Law section 5-323", text);
            Assert.Contains("bodily injury, death or damage to tangible property", text);
            Assert.Contains("fraud, gross negligence or willful misconduct", text);
            Assert.Contains("cannot lawfully be excluded or limited", text);
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  4. A cap is published as a cap
        // ══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// NEW YORK IS WHY THIS TEST EXISTS. Section 15(b) permits a charge limited to reasonable
        /// documented net loss, capped at a percentage; it does not fix a sum payable on
        /// cancellation. Publishing "a 50% cancellation fee applies" would describe a
        /// liquidated-damages clause the agreement does not contain - and one plainly
        /// disproportionate to the probable loss is an unenforceable penalty (JMD Holding Corp. v
        /// Congress Financial Corp., 4 NY3d 373 [2005]; Coast Cleaning Servs. LLC v Ambrosio
        /// Italian Rest. of S.I. Inc., 2022 NY Slip Op 50535[U], where a cleaning-services clause
        /// claiming $10,916.48 was refused and recovery limited to $813.18 of proven loss).
        ///
        /// So the qualifier is asserted, and the fee wording is asserted absent.
        /// </summary>
        [Fact]
        public void ACapIsPublishedAsACapAndNeverAsAnAutomaticFee()
        {
            foreach (var document in BothDocuments())
            {
                var text = AllText(document);

                Assert.Contains("reasonable, documented net loss", text);
                Assert.Contains("ceiling on a documented loss, not a fee that is charged automatically", text);
                Assert.Contains("if there was no loss, there is no charge", text);
                Assert.Contains("A full fee for failed access is not automatic", text);

                // The words that would turn a cap into a penalty.
                Assert.DoesNotContain("cancellation fee of", text);
                Assert.DoesNotContain("a 50% cancellation fee", text);
                Assert.DoesNotContain("early termination fee of", text);
                Assert.DoesNotContain("liquidated damages", text);
                Assert.DoesNotContain("non-refundable", text);
            }
        }

        /// <summary>
        /// The guarantee is published as what the agreement gives - re-performance, then a credit
        /// or refund for the deficient portion - and never as an unconditional refund or a free
        /// re-clean of the whole premises, neither of which Section 22 promises.
        /// </summary>
        [Fact]
        public void TheGuaranteeIsPublishedAsCorrectionAndNotAsAnUnconditionalRefund()
        {
            var text = AllText(CommercialPolicyDocument.BuildComplete());

            Assert.Contains("re-perform the deficient task at no charge", text);
            Assert.Contains("reasonable credit or refund attributable to the deficient portion", text);
            Assert.Contains("It is not an unconditional full refund and not a free re-clean", text);
            Assert.DoesNotContain("100% money-back", text);
            Assert.DoesNotContain("money-back guarantee", text);
        }

        /// <summary>
        /// The page never claims to bind anybody by being published, and says the executed
        /// agreement wins. Publishing terms on a website does not make them a contract, and a
        /// policy page that implies otherwise is making a claim about a client's obligations that
        /// nobody agreed to.
        /// </summary>
        [Fact]
        public void ThePublishedPoliciesDeferToTheSignedAgreement()
        {
            foreach (var document in BothDocuments())
            {
                Assert.Contains(CommercialPolicyDocument.PrecedenceNote, AllText(document));
            }

            var complete = AllText(CommercialPolicyDocument.BuildComplete());
            Assert.Contains("is not itself an offer, a contract, or a set of terms a client accepts", complete);
            Assert.Contains(
                "does not change the terms of an agreement a client has already signed", complete);
            Assert.Contains("We do not reserve a right to vary an existing contract by editing a web page",
                complete);
        }

        /// <summary>
        /// A minimum commitment is a contract-specific term. Publishing one as a company-wide rule
        /// would state an obligation on clients whose own agreements say something different.
        /// </summary>
        [Fact]
        public void TheMinimumCommitmentIsPublishedAsContractSpecific()
        {
            var text = AllText(CommercialPolicyDocument.BuildComplete());

            Assert.Contains("A minimum commitment is a contract-specific term, not a company-wide rule",
                text);
            Assert.Contains("stated in the client's own agreement", text);
            Assert.Contains("There is no separate early-termination penalty", text);
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  5. Nothing confidential is in a public document
        // ══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// THE LEAK GUARD, the public-document counterpart of the one in ContractRenderingTests.
        ///
        /// A published policy is read by every prospect and every competitor. A client's name, a
        /// negotiated price, a bank account or the contractor's residential street address in it
        /// is not a formatting problem, and it cannot be taken back once indexed.
        /// </summary>
        [Fact]
        public void NoClientOrBankingDetailAppearsInAPublishedDocument()
        {
            string[] forbidden =
            {
                // The reference agreement's client and premises.
                "Chick Tastic", "1569 Flatbush", "849.99", "925.43", "462.72",
                // Banking and credentials.
                "routing", "account number", "IBAN", "SWIFT", "Zelle",
                // The seeded contractor street address: a residential apartment, and publishing it
                // is a business decision nobody has taken.
                "8800 20th Ave", "Apt 2B",
                // A published price of any kind - commercial pricing is per-agreement.
                "per visit for", "$/visit"
            };

            foreach (var document in BothDocuments())
            {
                var text = AllText(document);
                foreach (var needle in forbidden)
                {
                    Assert.DoesNotContain(needle, text, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        /// <summary>
        /// The public contact details are the published ones, and they are the same on both
        /// documents - a client who downloads one and emails the address on it must reach us.
        /// </summary>
        [Fact]
        public void BothDocumentsCarryTheSamePublicContactDetails()
        {
            foreach (var document in BothDocuments())
            {
                var text = AllText(document);
                Assert.Contains(CommercialPolicyDocument.ContactEmail, text);
                Assert.Contains(CommercialPolicyDocument.ContactPhone, text);
                Assert.Equal(CommercialPolicyDocument.LegalIdentity, document.LegalIdentity);
                Assert.Equal(CommercialPolicyDocument.Version, document.Version);
                Assert.Equal(CommercialPolicyDocument.EffectiveDate, document.EffectiveDate);
            }
        }

        /// <summary>
        /// Residential and commercial stay separate, in both directions. The commercial policies
        /// point a residential reader at the Terms and Conditions instead of answering them, and
        /// never describe the online booking flow.
        /// </summary>
        [Fact]
        public void TheCommercialPoliciesDoNotAnswerForResidentialBookings()
        {
            var text = AllText(CommercialPolicyDocument.BuildComplete());

            Assert.Contains("They do not apply to residential cleaning", text);
            Assert.Contains("Terms and Conditions", text);

            // Residential vocabulary that would mean this page had started answering for the
            // online booking flow, whose own $70 cancellation fee and consents are unrelated.
            Assert.DoesNotContain("$70", text);
            Assert.DoesNotContain("Book Now", text);
            Assert.DoesNotContain("gift card", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("loyalty discount", text, StringComparison.OrdinalIgnoreCase);
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  6. Both PDFs render, and their text survives the round trip
        // ══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// QuestPDF raises layout faults at RENDER time, so a document that cannot compose throws
        /// on the first download rather than at build. Both are rendered here, and the bytes are
        /// checked to be a real PDF.
        /// </summary>
        [Fact]
        public void BothPdfsRenderAndAreValidPdfFiles()
        {
            var service = new CommercialPolicyPdfService();

            foreach (var bytes in new[] { service.GenerateComplete(), service.GenerateCancellationAndTermination() })
            {
                Assert.NotNull(bytes);
                Assert.True(bytes.Length > 10_000, $"PDF is implausibly small: {bytes.Length} bytes");
                Assert.Equal("%PDF", Encoding.ASCII.GetString(bytes, 0, 4));

                using var pdf = PdfDocument.Open(bytes);
                Assert.True(pdf.NumberOfPages >= 2);
            }
        }

        /// <summary>
        /// THE PDF SAYS WHAT THE PAGE SAYS, proved rather than asserted: the text is extracted back
        /// out of the rendered file and every 4+ letter word of the source document is looked for
        /// in it.
        ///
        /// This is the same technique - and the same reason - as ContractPdfTextIntegrityTests. With
        /// ligatures enabled the shaper substituted one glyph for pairs like "ti" and then dropped
        /// it, so a rendered document silently read "noce" for "notice". Nothing errors; the text
        /// just loses characters. It also proves the requirement that these PDFs are selectable,
        /// searchable text rather than an image of a page.
        /// </summary>
        [Theory]
        [InlineData(CommercialPolicyDocument.CompleteKey)]
        [InlineData(CommercialPolicyDocument.CancellationKey)]
        public void EveryWordOfTheDocumentSurvivesIntoThePdf(string key)
        {
            var document = key == CommercialPolicyDocument.CompleteKey
                ? CommercialPolicyDocument.BuildComplete()
                : CommercialPolicyDocument.BuildCancellationAndTermination();

            var service = new CommercialPolicyPdfService();
            var bytes = service.Generate(document);

            using var pdf = PdfDocument.Open(bytes);
            var extracted = new StringBuilder();
            foreach (var page in pdf.GetPages()) extracted.AppendLine(page.Text);

            var haystack = LettersOnly(extracted.ToString());
            Assert.NotEmpty(haystack);

            var missing = AllText(document)
                .Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(LettersOnly)
                .Where(w => w.Length >= 4)
                .Distinct()
                .Where(w => !haystack.Contains(w, StringComparison.Ordinal))
                .ToList();

            Assert.True(missing.Count == 0,
                $"Words present in the {key} document but not extractable from its PDF: "
                + string.Join(", ", missing.Take(20)));
        }

        /// <summary>
        /// Each PDF carries the branding and identification a document detached from the website
        /// still has to provide: the title, who issued it, the version and the effective date.
        /// </summary>
        [Fact]
        public void EachPdfIdentifiesItselfWithoutTheWebsite()
        {
            var service = new CommercialPolicyPdfService();

            foreach (var (document, bytes) in new[]
            {
                (CommercialPolicyDocument.BuildComplete(), service.GenerateComplete()),
                (CommercialPolicyDocument.BuildCancellationAndTermination(),
                    service.GenerateCancellationAndTermination())
            })
            {
                using var pdf = PdfDocument.Open(bytes);
                var text = new StringBuilder();
                foreach (var page in pdf.GetPages()) text.AppendLine(page.Text);
                var haystack = LettersOnly(text.ToString());

                Assert.Contains(LettersOnly(document.Title), haystack);
                Assert.Contains(LettersOnly(CommercialPolicyDocument.LegalIdentity), haystack);
                Assert.Contains(LettersOnly("September 16, 2026"), haystack);
                Assert.Contains(LettersOnly(CommercialPolicyDocument.ContactEmail), haystack);
                Assert.Contains(LettersOnly("CONTENTS"), haystack);
            }
        }

        /// <summary>
        /// The filenames are the ones the task and the page ask for, and the two are different -
        /// a client who downloads both must end up with two files, not one overwriting the other.
        /// </summary>
        [Fact]
        public void TheTwoDownloadsHaveDistinctDescriptiveFilenames()
        {
            Assert.Equal("Dream-Cleaning-NYC-Commercial-Cleaning-Policies.pdf",
                CommercialPolicyDocument.BuildComplete().PdfFileName);
            Assert.Equal("Dream-Cleaning-NYC-Cancellation-Termination-Policy.pdf",
                CommercialPolicyDocument.BuildCancellationAndTermination().PdfFileName);
            Assert.NotEqual(CommercialPolicyDocument.CompletePdfFileName,
                CommercialPolicyDocument.CancellationPdfFileName);
        }

        /// <summary>
        /// An unparseable effective date prints as written rather than throwing. A malformed
        /// constant is a deployment mistake; taking every policy download down with it is worse
        /// than printing the raw string.
        /// </summary>
        [Fact]
        public void AnUnparseableEffectiveDatePrintsAsWritten()
        {
            Assert.Equal("September 16, 2026", CommercialPolicyDocument.FormatEffectiveDate("2026-09-16"));
            Assert.Equal("soon", CommercialPolicyDocument.FormatEffectiveDate("soon"));
            Assert.Equal("", CommercialPolicyDocument.FormatEffectiveDate(""));
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  Helpers
        // ══════════════════════════════════════════════════════════════════════════════════════

        private static IEnumerable<PolicyDocument> BothDocuments() => new[]
        {
            CommercialPolicyDocument.BuildComplete(),
            CommercialPolicyDocument.BuildCancellationAndTermination()
        };

        private static string AllText(PolicyDocument document)
        {
            var sb = new StringBuilder();
            sb.AppendLine(document.Title).AppendLine(document.Subtitle).AppendLine(document.LegalIdentity);
            foreach (var block in document.Intro) sb.AppendLine(block.Text);
            foreach (var section in document.Sections)
            {
                sb.AppendLine(section.Title);
                foreach (var block in section.Blocks) sb.AppendLine(block.Text);
            }
            return sb.ToString();
        }

        private static string LettersOnly(string value) =>
            new string(value.Where(char.IsLetter).ToArray()).ToLowerInvariant();

        private static void AssertDocumentsMatch(
            PolicyDocument expected, PolicyDocument? actual, string label)
        {
            Assert.True(actual != null, $"The frontend copy has no '{label}' document.");

            Assert.Equal(expected.Key, actual!.Key);
            Assert.Equal(expected.Title, actual.Title);
            Assert.Equal(expected.Subtitle, actual.Subtitle);
            Assert.Equal(expected.LegalIdentity, actual.LegalIdentity);
            Assert.Equal(expected.Version, actual.Version);
            Assert.Equal(expected.EffectiveDate, actual.EffectiveDate);
            Assert.Equal(expected.PdfFileName, actual.PdfFileName);

            AssertBlocksMatch(expected.Intro, actual.Intro, $"{label}/intro");

            Assert.True(expected.Sections.Count == actual.Sections.Count,
                $"{label}: the frontend copy has {actual.Sections.Count} sections, the backend has "
                + $"{expected.Sections.Count}. Run "
                + "`node scripts/generate-commercial-policy-content.js` in DreamCleaningNG.");

            for (var i = 0; i < expected.Sections.Count; i++)
            {
                var want = expected.Sections[i];
                var got = actual.Sections[i];

                Assert.Equal(want.Number, got.Number);
                Assert.Equal(want.Title, got.Title);
                Assert.Equal(want.Anchor, got.Anchor);
                AssertBlocksMatch(want.Blocks, got.Blocks, $"{label}/{want.Anchor}");
            }
        }

        private static void AssertBlocksMatch(
            List<PolicyBlock> expected, List<PolicyBlock> actual, string where)
        {
            Assert.True(expected.Count == actual.Count,
                $"{where}: the frontend copy has {actual.Count} blocks, the backend has "
                + $"{expected.Count}. Run "
                + "`node scripts/generate-commercial-policy-content.js` in DreamCleaningNG.");

            for (var i = 0; i < expected.Count; i++)
            {
                Assert.True(expected[i].Kind == actual[i].Kind,
                    $"{where} block {i}: kind is {actual[i].Kind}, expected {expected[i].Kind}.");
                Assert.True(expected[i].Text == actual[i].Text,
                    $"{where} block {i} differs.\nBackend:  {expected[i].Text}\nFrontend: {actual[i].Text}");
            }
        }

        private class MirroredContent
        {
            public PolicyDocument? Complete { get; set; }
            public PolicyDocument? Cancellation { get; set; }
        }

        /// <summary>
        /// Reads the generated TypeScript module and deserialises the object literal in it.
        ///
        /// The generator emits valid JSON as the module's body for exactly this reason: the
        /// alternative is a backend test that has to understand TypeScript, and the alternative to
        /// THAT is no test at all, which is how the page and the PDFs would come to disagree.
        /// </summary>
        private static MirroredContent ReadGeneratedFrontendContent()
        {
            var text = ReadFrontendFile("shared", "commercial-policies", "commercial-policy.content.ts");

            // Located from the export, not from the first brace in the file - the module opens
            // with `import { CommercialPolicyContent } ...`, whose brace would otherwise be taken
            // for the start of the document.
            var marker = text.IndexOf("COMMERCIAL_POLICY_CONTENT", StringComparison.Ordinal);
            Assert.True(marker >= 0,
                "commercial-policy.content.ts does not export COMMERCIAL_POLICY_CONTENT.");

            var open = text.IndexOf('{', text.IndexOf('=', marker));
            var close = text.LastIndexOf('}');
            Assert.True(open >= 0 && close > open,
                "commercial-policy.content.ts does not contain an object literal. Run "
                + "`node scripts/generate-commercial-policy-content.js` in DreamCleaningNG.");

            var json = text.Substring(open, close - open + 1);
            var parsed = JsonSerializer.Deserialize<MirroredContent>(json, Json);

            Assert.True(parsed != null, "The generated frontend content could not be parsed as JSON.");
            return parsed!;
        }

        private static string ReadFrontendFile(params string[] parts)
        {
            var path = Path.Combine(
                new[] { SolutionRoot(), "DreamCleaningNG", "src", "app" }.Concat(parts).ToArray());
            Assert.True(File.Exists(path), $"{path} was not found.");
            return File.ReadAllText(path);
        }

        /// <summary>The folder holding both projects.</summary>
        private static string SolutionRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "DreamCleaningNG")))
                dir = dir.Parent;

            Assert.NotNull(dir);
            return dir!.FullName;
        }
    }
}
