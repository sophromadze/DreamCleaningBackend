using System.Text;
using System.Text.RegularExpressions;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models.Contracts;
using DreamCleaningBackend.Services.Contracts;
using UglyToad.PdfPig;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// THE PDF MUST NOT LOSE CHARACTERS.
    ///
    /// With standard ligatures enabled, the shaper substituted one glyph for letter pairs like
    /// "ti" and then dropped it, so the rendered contract silently read "Effecve" for "Effective",
    /// "noce" for "notice" and "cerficate" for "certificate". Nothing threw; the text simply lost
    /// characters, throughout a legal document.
    ///
    /// These tests do not take the fix on trust. They generate a real PDF, extract the text back
    /// out of it with PdfPig, and diff it against the source the renderer was given — which is the
    /// only way to prove the glyphs actually reached the page.
    /// </summary>
    public class ContractPdfTextIntegrityTests
    {
        static ContractPdfTextIntegrityTests()
        {
            QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        }

        private static ContractSnapshot Snapshot()
        {
            var snapshot = new ContractSnapshot
            {
                ContractNumber = "DC-2026-0001",
                VersionNumber = 1,
                EffectiveDate = new DateTime(2026, 3, 1),
                TemplateBodyText = ContractTemplateSeed.BodyText,
                PremisesType = "restaurant",
                Contractor = new ContractorSnapshot
                {
                    LegalEntityName = "Nodar Alania Inc.", Dba = "Dream Cleaning NYC",
                    EntityType = "a New York corporation",
                    Address = "8800 20th Ave, Apt 2B", City = "Brooklyn", State = "NY", Zip = "11214",
                    NoticeEmail = "hello@dreamcleaningnyc.com", Phone = "9299301525"
                },
                Client = new ClientSnapshot
                {
                    LegalEntityName = "Chick Tastic LLC", EntityType = "a limited liability company",
                    PrincipalAddress = "1569 Flatbush Ave.", City = "Brooklyn", State = "NY", Zip = "11210",
                    NoticeEmail = "ap@example.com", Phone = "7325471819"
                },
                ServiceLocation = new ServiceLocationSnapshot
                {
                    BusinessBrand = "Chick-fil-A", LocationName = "Flatbush",
                    Address = "1569 Flatbush Ave.", City = "Brooklyn", State = "NY", Zip = "11210"
                },
                ContractorSigner = new SignerSnapshot { FirstName = "Nodar", LastName = "Alania", Title = "CEO" },
                ClientSigner = new SignerSnapshot { FirstName = "Natalie", LastName = "Finkels" },
                Pricing = new PricingSnapshot
                {
                    PriceMode = ContractPriceMode.PreTax, PriceInput = 849.99m,
                    SalesTaxRatePercent = 8.875m, CancellationPercent = 50m
                },
                Scope = ContractScopeTemplateSeed.All().First(t => t.Name == "Restaurant").Structure.Clone()
            };
            ContractPricingCalculator.Recalculate(snapshot.Pricing);
            return snapshot;
        }

        private static ContractSignatureBlockDto Block() => new()
        {
            Contractor = new ContractSignaturePartyDto
            {
                PartyLabel = "CONTRACTOR", EntityName = "Nodar Alania Inc. d/b/a Dream Cleaning NYC",
                SignerName = "Nodar Alania", SignerTitle = "CEO", HasSigned = false
            },
            Client = new ContractSignaturePartyDto
            {
                PartyLabel = "CLIENT", EntityName = "Chick Tastic LLC",
                SignerName = "Natalie Finkels", HasSigned = false
            }
        };

        /// <summary>Generates the preview and reads every page's text back out.</summary>
        private static string ExtractedText()
        {
            var snapshot = Snapshot();
            var rendered = ContractRenderer.Render(snapshot);
            var bytes = new ContractPdfService()
                .GenerateDocument(rendered, snapshot, Block(), certificate: null, draftWatermark: true);

            using var pdf = PdfDocument.Open(bytes);
            var sb = new StringBuilder();
            foreach (var page in pdf.GetPages()) sb.AppendLine(page.Text);
            return sb.ToString();
        }

        /// <summary>Letters only, lowercased — so spacing, kerning and line wrapping don't matter.</summary>
        private static string LettersOnly(string value) =>
            new string(value.Where(char.IsLetter).ToArray()).ToLowerInvariant();

        /// <summary>
        /// Letters AND digits, lowercased. Same wrapping-proof comparison, for the assertions that
        /// turn on a number — a ZIP, a price — which <see cref="LettersOnly"/> would discard.
        /// </summary>
        private static string AlphanumericOnly(string value) =>
            new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        // ── the regression that started this ───────────────────────────────────

        [Theory]
        [InlineData("Effective")]
        [InlineData("notice")]
        [InlineData("certificate")]
        [InlineData("information")]
        [InlineData("continuation")]
        [InlineData("Tastic")]
        [InlineData("cancellation")]
        [InlineData("termination")]
        public void WordsContainingTiSurviveIntoThePdf(string word)
        {
            // The exact words the corruption was reported on. Each one lost its "ti" before.
            Assert.Contains(LettersOnly(word), LettersOnly(ExtractedText()));
        }

        [Theory]
        // Every ligature a Latin font commonly defines, not just the one that was noticed.
        [InlineData("fi", "notify")]
        [InlineData("fi", "confidential")]
        [InlineData("fl", "reflect")]
        [InlineData("ffi", "sufficient")]
        [InlineData("ff", "different")]
        [InlineData("ti", "negotiated")]
        public void NoCommonLigaturePairIsDropped(string pair, string probe)
        {
            var text = LettersOnly(ExtractedText());
            // The probe word only proves anything if the template actually contains it.
            var source = LettersOnly(ContractRenderer.Render(Snapshot()).PlainText);
            if (!source.Contains(LettersOnly(probe))) return;

            Assert.Contains(LettersOnly(probe), text);
            Assert.Contains(pair, text);
        }

        // ── the full diff the fix has to survive ───────────────────────────────

        [Fact]
        public void EveryWordOfTheRenderedContractReachesThePdfIntact()
        {
            // The real proof: take every word the renderer produced and confirm the PDF contains
            // it. A dropped glyph anywhere — not just in a pair we thought to check — fails here.
            var snapshot = Snapshot();
            var source = ContractRenderer.Render(snapshot);
            var pdfText = LettersOnly(ExtractedText());

            var words = Regex.Matches(source.PlainText, @"[A-Za-z]{4,}")
                .Select(m => m.Value.ToLowerInvariant())
                .Distinct()
                .ToList();

            Assert.NotEmpty(words);

            var missing = words.Where(w => !pdfText.Contains(w)).ToList();

            Assert.True(missing.Count == 0,
                $"{missing.Count} word(s) from the agreement did not survive into the PDF: " +
                string.Join(", ", missing.Take(25)));
        }

        [Fact]
        public void TheBrandedMastheadAndRunningHeaderAreRendered()
        {
            var text = LettersOnly(ExtractedText());
            Assert.Contains(LettersOnly("MASTER SERVICE AGREEMENT"), text);
            Assert.Contains(LettersOnly("COMMERCIAL CLEANING SERVICES"), text);
            // The running header names the contractor on every page after the cover.
            Assert.Contains(LettersOnly("DREAM CLEANING NYC"), text);
        }

        [Fact]
        public void TheSignatureBlockIsNeverSplitAcrossAPage()
        {
            // Both parties' boxes must land on one page. Previously the break fell between the
            // Contractor's name/title and the Client's box, which read as a duplicated, broken
            // block. Checked per page rather than over the whole document.
            var snapshot = Snapshot();
            var rendered = ContractRenderer.Render(snapshot);
            var bytes = new ContractPdfService()
                .GenerateDocument(rendered, snapshot, Block(), null, true);

            using var pdf = PdfDocument.Open(bytes);
            var pagesWithContractorBox = new List<int>();
            var pagesWithClientBox = new List<int>();

            foreach (var page in pdf.GetPages())
            {
                var letters = LettersOnly(page.Text);
                // "CONTRACTOR"/"CLIENT" appear in the prose too, so anchor on the entity names,
                // which only appear together inside the signature boxes.
                if (letters.Contains(LettersOnly("Nodar Alania Inc. d/b/a Dream Cleaning NYC")))
                    pagesWithContractorBox.Add(page.Number);
                if (letters.Contains(LettersOnly("Chick Tastic LLC")))
                    pagesWithClientBox.Add(page.Number);
            }

            Assert.NotEmpty(pagesWithContractorBox);
            Assert.NotEmpty(pagesWithClientBox);
            // The two boxes share at least one page — they are rendered side by side and the whole
            // block is wrapped in ShowEntire.
            Assert.True(pagesWithContractorBox.Intersect(pagesWithClientBox).Any(),
                "The contractor and client signature boxes did not appear together on any page.");
        }

        // ── the preview and the PDF are ONE render (2026-09-15) ────────────────

        /// <summary>
        /// THE PREVIEW HTML AND THE PDF COME FROM THE SAME <see cref="RenderedContract"/>.
        ///
        /// Both `GeneratePreviewAsync` and `RenderAndFileExecutedAsync` call
        /// <c>ContractRenderer.Render(snapshot)</c> and hand the result to their writer, so the
        /// two cannot say different things about the same version by construction. This proves it
        /// on the three changes most able to diverge if anyone ever re-derived one of them: the
        /// de-duplicated premises address, the dropped Client mailing-address row, and the two
        /// optional site-detail lines.
        /// </summary>
        [Fact]
        public void ThePdfSaysExactlyWhatThePreviewSays()
        {
            var snapshot = Snapshot();
            // Typed the way a person actually types it: the city and state are already in the
            // street box, and the structured columns repeat them.
            snapshot.ServiceLocation.Address = "1569 Flatbush Ave., Brooklyn, NY";
            snapshot.SiteDetails = new SiteDetailsSnapshot();   // nothing recorded at all

            var rendered = ContractRenderer.Render(snapshot);
            var bytes = new ContractPdfService()
                .GenerateDocument(rendered, snapshot, Block(), certificate: null, draftWatermark: true);

            using var pdf = PdfDocument.Open(bytes);
            // Letters AND digits here, because a ZIP is exactly what LettersOnly would throw away
            // and the ZIP is half of what makes the duplicate visible.
            var pdfText = AlphanumericOnly(string.Join(" ", pdf.GetPages().Select(p => p.Text)));
            var previewText = AlphanumericOnly(rendered.PlainText);

            // The address is written once, in both.
            Assert.Contains(AlphanumericOnly("1569 Flatbush Ave., Brooklyn, NY 11210"), previewText);
            Assert.Contains(AlphanumericOnly("1569 Flatbush Ave., Brooklyn, NY 11210"), pdfText);
            Assert.DoesNotContain(AlphanumericOnly("Brooklyn, NY, Brooklyn"), previewText);
            Assert.DoesNotContain(AlphanumericOnly("Brooklyn, NY, Brooklyn"), pdfText);

            // The retired row and the two optional lines are absent from both.
            foreach (var gone in new[]
                     {
                         "Client notice mailing address",
                         "business mailing address",
                         "FLOOR AND SURFACE MATERIALS",
                         "FOOD-SERVICE PERMIT HOLDER"
                     })
            {
                Assert.DoesNotContain(AlphanumericOnly(gone), previewText);
                Assert.DoesNotContain(AlphanumericOnly(gone), pdfText);
            }

            // And the contractor's own mailing-address row is still on both — it was never the
            // one being removed.
            Assert.Contains(AlphanumericOnly("Contractor notice mailing address"), previewText);
            Assert.Contains(AlphanumericOnly("Contractor notice mailing address"), pdfText);
        }

        /// <summary>
        /// THE PREAMBLE NAMES THE PREMISES ON BOTH SURFACES, AND NAMES IT THE SAME WAY.
        ///
        /// The introductory paragraph is the one place the PDF and the preview are most likely to
        /// diverge without anybody noticing: it is a long justified paragraph that wraps
        /// differently on every page width, so an admin comparing the two by eye is comparing two
        /// shapes. The wording matters as much as the address — the service location must never
        /// be presented as Client's principal office or mailing address, and a misread on the
        /// executed copy is the version a counterparty keeps.
        /// </summary>
        [Fact]
        public void ThePreamblePremisesAddressIsOnBothThePreviewAndThePdf()
        {
            var snapshot = Snapshot();
            var rendered = ContractRenderer.Render(snapshot);
            var bytes = new ContractPdfService()
                .GenerateDocument(rendered, snapshot, Block(), certificate: null, draftWatermark: true);

            using var pdf = PdfDocument.Open(bytes);
            var pdfText = AlphanumericOnly(string.Join(" ", pdf.GetPages().Select(p => p.Text)));
            var previewText = AlphanumericOnly(rendered.PlainText);

            var clause =
                "Chick Tastic LLC, a limited liability company, with Services to be performed at "
                + "1569 Flatbush Ave., Brooklyn, NY 11210 (\"Client\")";
            Assert.Contains(AlphanumericOnly(clause), previewText);
            Assert.Contains(AlphanumericOnly(clause), pdfText);

            // The Contractor's principal office is still the only principal office in either.
            var contractorOffice =
                "with its principal office at 8800 20th Ave, Apt 2B, Brooklyn, NY 11214";
            Assert.Contains(AlphanumericOnly(contractorOffice), previewText);
            Assert.Contains(AlphanumericOnly(contractorOffice), pdfText);

            foreach (var mislabel in new[]
                     {
                         "principal office at 1569 Flatbush",
                         "Client's principal office",
                         "registered office",
                         "business mailing address"
                     })
            {
                Assert.DoesNotContain(AlphanumericOnly(mislabel), previewText);
                Assert.DoesNotContain(AlphanumericOnly(mislabel), pdfText);
            }

            // Section 1(b) and Exhibit A still carry their own premises sentences, word for word,
            // on both surfaces - the preamble repeats the address rather than replacing them.
            foreach (var kept in new[]
                     {
                         "The Premises address is the service location only and is not necessarily "
                         + "Client's legal or principal business address.",
                         "Service location only; not necessarily the legal or principal business "
                         + "address of Client."
                     })
            {
                Assert.Contains(AlphanumericOnly(kept), previewText);
                Assert.Contains(AlphanumericOnly(kept), pdfText);
            }
        }

        /// <summary>
        /// THE OPTIONAL BACKUP CONTACTS RENDER THE SAME WAY IN BOTH, blank and filled.
        ///
        /// Same guarantee as the test above and the same reason to prove it: Exhibit B4 is a run
        /// of exhibit ROWS, and the PDF lays those out through its own table writer. A row the
        /// preview drops and the PDF keeps would put a ruled blank into an executed agreement that
        /// the admin who approved it never saw.
        /// </summary>
        [Fact]
        public void ABlankBackupContactIsAbsentFromBothThePreviewAndThePdf()
        {
            // Nothing recorded - the fixture never sets Contacts, so both backups are empty.
            var blankSnapshot = Snapshot();
            var blank = ContractRenderer.Render(blankSnapshot);
            var blankPdf = AlphanumericOnly(string.Join(" ", PdfDocument
                .Open(new ContractPdfService().GenerateDocument(
                    blank, blankSnapshot, Block(), certificate: null, draftWatermark: true))
                .GetPages().Select(p => p.Text)));
            var blankPreview = AlphanumericOnly(blank.PlainText);

            foreach (var gone in new[]
                     {
                         "Contractor backup on-call contact",
                         "Client backup on-call contact"
                     })
            {
                Assert.DoesNotContain(AlphanumericOnly(gone), blankPreview);
                Assert.DoesNotContain(AlphanumericOnly(gone), blankPdf);
            }

            // No ruled blank reached either surface, and the sentinel never leaked into one.
            Assert.DoesNotContain(AlphanumericOnly(ContractPlaceholders.OmitLineSentinel), blankPdf);

            // The primary rows are on both — dropping a backup must not take its primary along.
            Assert.Contains(AlphanumericOnly("Client primary on-call contact"), blankPreview);
            Assert.Contains(AlphanumericOnly("Client primary on-call contact"), blankPdf);

            // Filled in, the rows and their values come back on both surfaces.
            var filled = Snapshot();
            filled.Contacts.ContractorBackupContact = "Operations desk, (929) 930-1526";
            filled.Contacts.ClientBackupContact = "Security desk, (212) 555-9000";

            var filledRender = ContractRenderer.Render(filled);
            var filledPdf = AlphanumericOnly(string.Join(" ", PdfDocument
                .Open(new ContractPdfService().GenerateDocument(
                    filledRender, filled, Block(), certificate: null, draftWatermark: true))
                .GetPages().Select(p => p.Text)));
            var filledPreview = AlphanumericOnly(filledRender.PlainText);

            foreach (var present in new[]
                     {
                         "Contractor backup on-call contact",
                         "Operations desk, (929) 930-1526",
                         "Client backup on-call contact",
                         "Security desk, (212) 555-9000"
                     })
            {
                Assert.Contains(AlphanumericOnly(present), filledPreview);
                Assert.Contains(AlphanumericOnly(present), filledPdf);
            }
        }

        /// <summary>
        /// NO FIXED WORDING NAMES A ROOM, in the preview OR the PDF.
        ///
        /// Both offenders lived in Exhibit A, which the PDF lays out through its own exhibit-row
        /// and definition-list writers rather than as plain paragraphs — so "the preview is right"
        /// is not evidence that the executed document is. With the office unticked, neither
        /// surface may name it.
        /// </summary>
        [Fact]
        public void NeitherThePreviewNorThePdfNamesARoomThatWasNotIncluded()
        {
            var snapshot = Snapshot();

            var included = snapshot.Scope.Groups.First(g => g.Key == "included-areas");
            included.Items.First(i => i.Label == "the office").Selected = false;

            var rendered = ContractRenderer.Render(snapshot);
            var bytes = new ContractPdfService()
                .GenerateDocument(rendered, snapshot, Block(), certificate: null, draftWatermark: true);

            using var pdf = PdfDocument.Open(bytes);
            var pdfText = AlphanumericOnly(string.Join(" ", pdf.GetPages().Select(p => p.Text)));
            var previewText = AlphanumericOnly(rendered.PlainText);

            // The retired A1 label and the retired A2 examples are absent from both.
            foreach (var gone in new[]
                     {
                         "Hallways, office and doors",
                         "Hallways, offices and doors",
                         "such as the office",
                         "the employee restroom or hallways"
                     })
            {
                Assert.DoesNotContain(AlphanumericOnly(gone), previewText);
                Assert.DoesNotContain(AlphanumericOnly(gone), pdfText);
            }

            // The replacements are present on both, so the row and the rule did not simply vanish.
            foreach (var present in new[]
                     {
                         "Hallways, included rooms and doors",
                         "An area expressly identified as an Included Area in A1 remains included "
                         + "even if it is physically located in a back-of-house portion of the Premises."
                     })
            {
                Assert.Contains(AlphanumericOnly(present), previewText);
                Assert.Contains(AlphanumericOnly(present), pdfText);
            }
        }

        /// <summary>
        /// And with the office TICKED it prints on both, as an ordinary Included Area. The fix
        /// removed fixed references, not the selectable room.
        /// </summary>
        [Fact]
        public void AnIncludedOfficeStillPrintsOnBothSurfaces()
        {
            var snapshot = Snapshot();
            var rendered = ContractRenderer.Render(snapshot);
            var bytes = new ContractPdfService()
                .GenerateDocument(rendered, snapshot, Block(), certificate: null, draftWatermark: true);

            using var pdf = PdfDocument.Open(bytes);
            var pdfText = AlphanumericOnly(string.Join(" ", pdf.GetPages().Select(p => p.Text)));

            Assert.Contains(AlphanumericOnly("the office"), AlphanumericOnly(rendered.PlainText));
            Assert.Contains(AlphanumericOnly("the office"), pdfText);
        }

        [Fact]
        public void TheExecutedDocumentAndCertificateAlsoKeepTheirText()
        {
            var snapshot = Snapshot();
            var rendered = ContractRenderer.Render(snapshot);

            var certificate = new ContractCertificateData
            {
                ContractNumber = "DC-2026-0001",
                VersionNumber = 1,
                DocumentHash = new string('a', 64),
                EffectiveDate = new DateTime(2026, 3, 1),
                Signers = new List<ContractCertificateSigner>
                {
                    new()
                    {
                        PartyLabel = "CONTRACTOR", EntityName = "Nodar Alania Inc.",
                        Name = "Nodar Alania", Title = "CEO", Email = "hello@dreamcleaningnyc.com",
                        SignedAt = new DateTime(2026, 3, 2, 14, 5, 0, DateTimeKind.Utc),
                        IpAddress = "203.0.113.7", Method = ContractSignatureMethod.Draw,
                        DocumentHashAtSigning = new string('a', 64), ConsentAccepted = true
                    }
                }
            };

            var bytes = new ContractPdfService()
                .GenerateDocument(rendered, snapshot, Block(), certificate, draftWatermark: false);

            using var pdf = PdfDocument.Open(bytes);
            var text = LettersOnly(string.Join(" ", pdf.GetPages().Select(p => p.Text)));

            Assert.Contains(LettersOnly("ELECTRONIC SIGNATURE CERTIFICATE"), text);
            // "certificate" is itself a "ti" word — the certificate page has to survive too.
            Assert.Contains(LettersOnly("certificate"), text);
            Assert.Contains(LettersOnly("Consent"), text);
        }
    }
}
