using System.Text;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models.Contracts;
using DreamCleaningBackend.Services.Contracts;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// The PDF actually composes.
    ///
    /// QuestPDF raises layout exceptions at RENDER time, not compile time - an element that cannot
    /// fit its container throws rather than clipping. The executed contract is generated inside
    /// the signing flow, so a layout fault there would surface as a failed execution after both
    /// parties had already signed. These tests render the real seeded agreement, with and without
    /// signatures, so that never reaches production.
    ///
    /// No font family is named anywhere in the writer on purpose: QuestPDF embeds Lato, and a
    /// system font would render differently (or fail) on the Linux VPS than on a Windows dev box.
    /// </summary>
    public class ContractPdfTests
    {
        static ContractPdfTests()
        {
            // Program.cs sets this at startup; the test host has no startup.
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

        private static ContractSignatureBlockDto UnsignedBlock() => new()
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

        /// <summary>A 1x1 PNG - enough for the writer to decode and place a drawn mark.</summary>
        private const string OnePixelPng =
            "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

        private static ContractSignatureBlockDto SignedBlock()
        {
            var block = UnsignedBlock();
            // One drawn, one typed — both marks have to place without a layout fault.
            block.Contractor.HasSigned = true;
            block.Contractor.Method = ContractSignatureMethod.Draw;
            block.Contractor.SignatureMark = OnePixelPng;
            block.Contractor.SignedAt = new DateTime(2026, 3, 2, 14, 5, 0, DateTimeKind.Utc);

            block.Client.HasSigned = true;
            block.Client.Method = ContractSignatureMethod.Type;
            block.Client.SignatureMark = "Natalie Finkels";
            block.Client.SignedAt = new DateTime(2026, 3, 3, 9, 30, 0, DateTimeKind.Utc);
            return block;
        }

        private static ContractCertificateData Certificate() => new()
        {
            ContractNumber = "DC-2026-0001",
            VersionNumber = 1,
            DocumentHash = new string('a', 64),
            EffectiveDate = new DateTime(2026, 3, 1),
            GeneratedAt = new DateTime(2026, 3, 3, 9, 31, 0, DateTimeKind.Utc),
            Signers = new List<ContractCertificateSigner>
            {
                new()
                {
                    PartyLabel = "CONTRACTOR", EntityName = "Nodar Alania Inc.",
                    Name = "Nodar Alania", Title = "CEO", Email = "hello@dreamcleaningnyc.com",
                    SignedAt = new DateTime(2026, 3, 2, 14, 5, 0, DateTimeKind.Utc),
                    IpAddress = "203.0.113.7", Method = ContractSignatureMethod.Draw,
                    DocumentHashAtSigning = new string('a', 64), ConsentAccepted = true,
                    UserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 " +
                                "(KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36"
                },
                new()
                {
                    PartyLabel = "CLIENT", EntityName = "Chick Tastic LLC",
                    Name = "Natalie Finkels", Title = "Owner", Email = "natalie@example.com",
                    SignedAt = new DateTime(2026, 3, 3, 9, 30, 0, DateTimeKind.Utc),
                    IpAddress = "198.51.100.22", Method = ContractSignatureMethod.Type,
                    DocumentHashAtSigning = new string('a', 64), ConsentAccepted = true
                }
            }
        };

        /// <summary>%PDF- is the file signature; anything else is not a PDF.</summary>
        private static void AssertIsPdf(byte[] bytes)
        {
            Assert.NotNull(bytes);
            Assert.True(bytes.Length > 5000, $"PDF is suspiciously small ({bytes.Length} bytes).");
            Assert.Equal("%PDF-", Encoding.ASCII.GetString(bytes, 0, 5));
        }

        [Fact]
        public void ThePreviewPdfRendersTheWholeAgreement()
        {
            var snapshot = Snapshot();
            var rendered = ContractRenderer.Render(snapshot);

            var bytes = new ContractPdfService().GenerateDocument(
                rendered, snapshot, UnsignedBlock(), certificate: null, draftWatermark: true);

            AssertIsPdf(bytes);
        }

        [Fact]
        public void TheExecutedPdfRendersBothMarksAndTheCertificate()
        {
            var snapshot = Snapshot();
            var rendered = ContractRenderer.Render(snapshot);

            var bytes = new ContractPdfService().GenerateDocument(
                rendered, snapshot, SignedBlock(), Certificate(), draftWatermark: false);

            AssertIsPdf(bytes);
            // The certificate is a whole extra page, so the executed file is meaningfully larger.
            var preview = new ContractPdfService().GenerateDocument(
                rendered, snapshot, UnsignedBlock(), null, true);
            Assert.True(bytes.Length > preview.Length);
        }

        [Fact]
        public void TheCertificateRendersOnItsOwn()
        {
            AssertIsPdf(new ContractPdfService().GenerateCertificate(Snapshot(), Certificate()));
        }

        [Fact]
        public void AMalformedSignatureMarkFallsBackInsteadOfThrowing()
        {
            // A truncated or non-image data URI must not take down the execution of a contract
            // both parties have already signed.
            var snapshot = Snapshot();
            var block = UnsignedBlock();
            block.Client.HasSigned = true;
            block.Client.SignatureMark = "data:image/png;base64,not-actually-base64!!";

            var bytes = new ContractPdfService().GenerateDocument(
                ContractRenderer.Render(snapshot), snapshot, block, null, false);

            AssertIsPdf(bytes);
        }

        [Fact]
        public void AnEmptyScopeAndAnUnsetEffectiveDateStillRender()
        {
            // The earliest a preview can be generated is a barely-filled draft. It must produce a
            // document rather than throw, so the admin can see what is still missing.
            var snapshot = Snapshot();
            snapshot.EffectiveDate = null;
            foreach (var group in snapshot.Scope.Groups)
                foreach (var item in group.Items) item.Selected = false;

            var bytes = new ContractPdfService().GenerateDocument(
                ContractRenderer.Render(snapshot), snapshot, UnsignedBlock(), null, true);

            AssertIsPdf(bytes);
        }

        /// <summary>
        /// The branding is not decoration and it has regressed before: the first executed contract
        /// went out with no logo, no colour at all and a signature block split across two pages.
        /// These assertions read the generated PDF back and check the marks that were missing.
        /// </summary>
        public class Branding
        {
            static Branding() => QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

            private static UglyToad.PdfPig.PdfDocument Executed()
            {
                var snapshot = Snapshot();
                var bytes = new ContractPdfService().GenerateDocument(
                    ContractRenderer.Render(snapshot), snapshot, SignedBlock(), Certificate(), false);
                return UglyToad.PdfPig.PdfDocument.Open(bytes);
            }

            [Fact]
            public void TheLogoIsOnTheCover()
            {
                using var doc = Executed();
                Assert.NotEmpty(doc.GetPage(1).GetImages());
            }

            [Fact]
            public void TheDocumentIsPrintedInTheBrandColours()
            {
                // The whole first executed contract came out in flat black. One blue heading on
                // the cover is the cheapest proof that the palette is reaching the page.
                using var doc = Executed();
                var blue = doc.GetPage(1).Letters
                    .Select(l => l.Color.ToRGBValues())
                    .Any(c => c.b > 0.7 && c.r < 0.3);

                Assert.True(blue, "No blue text on the cover — the brand palette is not being applied.");
            }

            [Fact]
            public void TheRunningHeaderIsOnEveryPageIncludingTheCover()
            {
                using var doc = Executed();
                foreach (var page in doc.GetPages())
                {
                    Assert.Contains("MASTER SERVICE AGREEMENT",
                        string.Concat(page.Letters.Where(l => l.GlyphRectangle.Bottom > 740).Select(l => l.Value)));
                }
            }

            [Fact]
            public void TheSignatureBlockIsWhollyOnOnePage()
            {
                // Both party labels have to land on the SAME page. They were split before, which
                // is what a counterparty notices first.
                using var doc = Executed();
                var pagesWithContractor = new List<int>();
                var pagesWithClient = new List<int>();

                foreach (var page in doc.GetPages())
                {
                    var text = string.Concat(page.Letters.Select(l => l.Value));
                    if (text.Contains("CONTRACTOR")) pagesWithContractor.Add(page.Number);
                    if (text.Contains("CLIENT")) pagesWithClient.Add(page.Number);
                }

                Assert.NotEmpty(pagesWithContractor.Intersect(pagesWithClient));
            }
        }
    }
}
