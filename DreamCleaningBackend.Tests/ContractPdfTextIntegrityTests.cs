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
