using System.Text;
using System.Text.RegularExpressions;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models.Contracts;
using DreamCleaningBackend.Services.Commercial;
using DreamCleaningBackend.Services.Contracts;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using UglyToad.PdfPig;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// THE FAILURES IN A REAL GENERATED CONTRACT, DCC-2026-49303882 (2026-09-30).
    ///
    /// The admin chose Simplified scope, no minimum commitment and thirty days' notice. The PDF
    /// that came out carried the full A1-A9 Exhibit A with ruled blanks, "zero (0) months", an
    /// Initial Term End Date the day BEFORE commencement, "Required completion time: None", the
    /// hand-soap sentence, the old hardcoded supplies split in B2, and B3 = insurance.
    ///
    /// Every one of those came from the BODY, not the data: the database's "2.7" row had been
    /// seeded by Sweep It Real (a fork sharing the dev database and the template Name), and the
    /// seeder adopted it because it does not rewrite a version it already has. The form, DTO and
    /// snapshot were right; the rendering tests passed because they render the SEED, never the
    /// row. So these tests pin what the seeded body produces for that exact configuration - on the
    /// preview AND in the extracted PDF text - plus the two things that decide whether the seed
    /// reaches a draft at all: the description fitting its column, and Generate re-copying the
    /// template body into a draft saved before it changed.
    /// </summary>
    public class ContractGeneratedDocumentRegressionTests
    {
        static ContractGeneratedDocumentRegressionTests()
        {
            QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        }

        // ── Fixture: the configuration of DCC-2026-49303882, with neutral party data ─────────

        internal static ContractSnapshot Dcc49303882Configuration()
        {
            var snapshot = new ContractSnapshot
            {
                ContractNumber = "DCC-2026-00000001",
                VersionNumber = 1,
                EffectiveDate = new DateTime(2026, 10, 4),
                ContractTemplateVersion = ContractTemplateSeed.TemplateVersion,
                TemplateBodyText = ContractTemplateSeed.BodyText,
                PremisesType = "office",
                ScopeDetail = ScopeDetailMode.Simplified,
                Contractor = new ContractorSnapshot
                {
                    LegalEntityName = "Nodar Alania Inc.", Dba = "Dream Cleaning NYC",
                    EntityType = "a New York corporation",
                    Address = "8800 20th Ave, Apt 2B", City = "Brooklyn", State = "NY", Zip = "11214",
                    NoticeEmail = "hello@dreamcleaningnyc.com", Phone = "9299301525"
                },
                Client = new ClientSnapshot
                {
                    LegalEntityName = "Example Games LLC", EntityType = "a limited liability company",
                    FormationState = "New York", PrincipalAddress = "100 Example St", City = "Manhattan",
                    State = "NY", Zip = "10012", NoticeEmail = "ops@example.test", Phone = "2125550100"
                },
                ServiceLocation = new ServiceLocationSnapshot
                {
                    BusinessBrand = "Example Games", LocationName = "Manhattan",
                    Address = "100 Example St", City = "Manhattan", State = "NY", Zip = "10012"
                },
                ContractorSigner = new SignerSnapshot
                {
                    FirstName = "Nodar", LastName = "Alania", Title = "CEO", Email = "hello@dreamcleaningnyc.com"
                },
                ClientSigner = new SignerSnapshot
                {
                    FirstName = "Jamie", LastName = "Client", Title = "Office Manager", Email = "ops@example.test"
                },
                Schedule = new ScheduleSnapshot
                {
                    FrequencyUnit = "calendar week", VisitsPerPeriod = 6,
                    ServiceDays = new List<string> { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Sunday" },
                    ArrivalWindowStart = "9:30 PM", ArrivalWindowEnd = "10:30 PM",
                    CompletionTime = "", PerformedWhileClosed = true, FlexibleScheduling = true,
                    AccessType = "key provided by Client"
                },
                Term = new TermSnapshot
                {
                    InitialTermMonths = 0, MinimumCommitmentMonths = 0, TerminationNoticeDays = 30,
                    ServiceCommencementDate = new DateTime(2026, 10, 4)
                },
                Pricing = new PricingSnapshot
                {
                    PriceMode = ContractPriceMode.PreTax, PriceInput = 145.83m,
                    SalesTaxRatePercent = 8.875m, CancellationPercent = 50m, LiabilityCapMultiple = 3
                },
                SiteDetails = new SiteDetailsSnapshot(),
                Insurance = new InsuranceEndorsementsSnapshot(),
                Contacts = new OperationalContactsSnapshot
                {
                    ContractorOperationalEmail = "hello@dreamcleaningnyc.com",
                    ContractorSupervisorName = "Nodar Alania", ContractorSupervisorPhone = "9299301525",
                    ClientOperationalEmail = "ops@example.test",
                    ClientOnCallName = "Jamie Client", ClientOnCallPhone = "2125550100"
                },
                // What the admin selected on DCC-2026-49303882: Client for all four rows.
                Supplies = new SuppliesSnapshot
                {
                    EquipmentProvidedBy = SupplyProvider.Client,
                    TrashLinersProvidedBy = SupplyProvider.Client,
                    PaperTowelsProvidedBy = SupplyProvider.Client,
                    ToiletTissueProvidedBy = SupplyProvider.Client
                },
                Scope = ContractScopeTemplateSeed.All().First(t => t.Name == "Office").Structure.Clone()
            };
            ContractPricingCalculator.Recalculate(snapshot.Pricing);
            return snapshot;
        }

        internal static ContractSignatureBlockDto Block() => new()
        {
            Contractor = new ContractSignaturePartyDto { PartyLabel = "CONTRACTOR", EntityName = "Nodar Alania Inc.", SignerName = "Nodar Alania" },
            Client = new ContractSignaturePartyDto { PartyLabel = "CLIENT", EntityName = "Example Games LLC", SignerName = "Jamie Client" }
        };

        /// <summary>The preview's plain text and the text extracted back out of the real PDF.</summary>
        private static (RenderedContract Rendered, string Pdf, int Pages) Generate(ContractSnapshot snapshot)
        {
            var rendered = ContractRenderer.Render(snapshot);
            var bytes = new ContractPdfService()
                .GenerateDocument(rendered, snapshot, Block(), certificate: null, draftWatermark: true);
            using var pdf = PdfDocument.Open(bytes);
            var sb = new StringBuilder();
            foreach (var page in pdf.GetPages()) sb.AppendLine(page.Text);
            return (rendered, sb.ToString(), pdf.NumberOfPages);
        }

        /// <summary>Case- and whitespace-insensitive: the PDF wraps lines wherever it likes.</summary>
        private static string Squash(string value) =>
            Regex.Replace(value, @"\s+", string.Empty).ToLowerInvariant();

        private static void AbsentFromBoth(string needle, RenderedContract rendered, string pdf)
        {
            Assert.DoesNotContain(needle, rendered.PlainText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Squash(needle), Squash(pdf));
        }

        /// <summary>
        /// For an ALL-CAPS exhibit label: matched with its case, because the same words appear in
        /// lower case inside ordinary clauses ("identify equipment that must remain operating").
        /// </summary>
        private static void LabelAbsentFromBoth(string label, RenderedContract rendered, string pdf)
        {
            Assert.DoesNotContain(label, rendered.PlainText, StringComparison.Ordinal);
            Assert.DoesNotContain(Regex.Replace(label, @"\s+", string.Empty), Regex.Replace(pdf, @"\s+", string.Empty));
        }

        private static void PresentInBoth(string needle, RenderedContract rendered, string pdf)
        {
            Assert.Contains(needle, rendered.PlainText);
            Assert.Contains(Squash(needle), Squash(pdf));
        }

        /// <summary>An exhibit row: "Label: Value" in the preview text, two cells in the PDF.</summary>
        private static void RowInBoth(string label, string value, RenderedContract rendered, string pdf)
        {
            Assert.Contains(rendered.Blocks, b => b.Kind == ContractBlockKind.ExhibitRow
                && b.Label == label && b.Text.StartsWith(value, StringComparison.Ordinal));
            Assert.Contains(Squash(label), Squash(pdf));
            Assert.Contains(Squash(value), Squash(pdf));
        }

        private static readonly string[] DetailedExhibitHeadings =
        {
            "A1. INCLUDED AREAS AND TASKS", "A2. EXCLUDED AREAS", "A3. LIMITED KITCHEN SCOPE",
            "A4. FLOOR CLEANING", "A5. RESTROOM CLEANING", "A6. TRASH AND WASTE",
            "A7. INTERIOR GLASS AND WINDOWS", "A8. BASELINE AND DEEP CLEANING"
        };

        private static readonly string[] SiteDetailLabels =
        {
            "APPROXIMATE SERVICED SQUARE FOOTAGE", "CUSTOMER RESTROOM AND FIXTURE COUNTS",
            "EMPLOYEE RESTROOM AND FIXTURE COUNTS", "INCLUDED KITCHEN EQUIPMENT", "TOUCHPOINTS AND CLEARED SURFACES",
            "INTERIOR GLASS AND WINDOW LOCATIONS", "FOOD-CONTACT OR DINING-TABLE SANITIZING",
            "ACCESS METHOD AND CLOSEOUT PROCEDURE", "EQUIPMENT THAT MUST REMAIN OPERATING",
            "WASTE, RECYCLING AND SEPARATELY COLLECTED", "SITE, LANDLORD OR BRAND REQUIREMENTS",
            "BASELINE WALKTHROUGH", "INITIAL-WORK CHANGE ORDER", "FLOOR AND SURFACE MATERIALS",
            "FOOD-SERVICE PERMIT HOLDER"
        };

        private static readonly string[] SiteDetailTokens =
        {
            "ACCESS_METHOD_REFERENCE", "BASELINE_WALKTHROUGH", "CUSTOMER_RESTROOM_COUNTS",
            "EMPLOYEE_RESTROOM_COUNTS", "EQUIPMENT_RESTRICTIONS", "INTERIOR_GLASS_LOCATIONS",
            "KITCHEN_EQUIPMENT_SURFACES", "SQUARE_FOOTAGE", "TOUCHPOINT_LOCATIONS", "WASTE_RECEPTACLE_LOCATIONS"
        };

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  1-2. Simplified scope
        // ══════════════════════════════════════════════════════════════════════════════════════

        [Fact]
        public void SimplifiedScopePdfContainsNoDetailedExhibit()
        {
            var (rendered, pdf, _) = Generate(Dcc49303882Configuration());

            foreach (var heading in DetailedExhibitHeadings) AbsentFromBoth(heading, rendered, pdf);
            foreach (var label in SiteDetailLabels) LabelAbsentFromBoth(label, rendered, pdf);
            AbsentFromBoth("A9.", rendered, pdf);
            AbsentFromBoth("SERVICE PREMISES:", rendered, pdf);

            PresentInBoth("The Parties have agreed the service scope separately", rendered, pdf);
            PresentInBoth("Material additional work or services outside the agreed scope require "
                          + "separate approval in accordance with Section 9.", rendered, pdf);
        }

        /// <summary>The simplified Exhibit A is a few sentences, not pages.</summary>
        [Fact]
        public void SimplifiedExhibitAIsShort()
        {
            var text = ContractRenderer.Render(Dcc49303882Configuration()).PlainText;
            var start = text.IndexOf("EXHIBIT A", StringComparison.Ordinal);
            var end = text.IndexOf("EXHIBIT B", StringComparison.Ordinal);
            Assert.True(start > 0 && end > start);
            Assert.True(end - start < 700, $"Simplified Exhibit A is {end - start} characters.");
        }

        [Fact]
        public void SimplifiedScopeRequiresNoSiteDetails()
        {
            var rendered = ContractRenderer.Render(Dcc49303882Configuration());

            foreach (var token in SiteDetailTokens) Assert.DoesNotContain(token, rendered.UnresolvedTokens);
            Assert.Empty(rendered.UnresolvedTokens);
            Assert.Empty(ContractService.DescribeMissingFields(rendered.UnresolvedTokens));
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  3-4. Omitted scope
        // ══════════════════════════════════════════════════════════════════════════════════════

        [Fact]
        public void OmittedScopeProducesNoExhibitAAndNoDanglingReference()
        {
            var snapshot = Dcc49303882Configuration();
            snapshot.ScopeDetail = ScopeDetailMode.Omitted;
            var (rendered, pdf, _) = Generate(snapshot);

            AbsentFromBoth("Exhibit A", rendered, pdf);
            AbsentFromBoth("Exhibits A and B", rendered, pdf);
            AbsentFromBoth("identified in Exhibit A", rendered, pdf);
            AbsentFromBoth("set out in Exhibit A", rendered, pdf);
            AbsentFromBoth("Exhibit A controls", rendered, pdf);
            AbsentFromBoth("references beginning with A", rendered, pdf);
            Assert.DoesNotContain(rendered.Blocks, b => b.Kind == ContractBlockKind.Heading && b.Text == "EXHIBIT A");

            PresentInBoth("Contractor shall provide the commercial cleaning services mutually agreed "
                          + "by the Parties", rendered, pdf);
            // The signature clause executes only what exists.
            Assert.Contains("including Sections 1 through 36, Exhibit B, and the representations", rendered.PlainText);
            Assert.Empty(rendered.UnresolvedTokens);
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  5-6, 16. Detailed scope: every blank optional value disappears
        // ══════════════════════════════════════════════════════════════════════════════════════

        [Fact]
        public void DetailedScopeHidesEveryBlankOptionalField()
        {
            var snapshot = Dcc49303882Configuration();
            snapshot.ScopeDetail = ScopeDetailMode.Detailed;
            var (rendered, pdf, _) = Generate(snapshot);

            // The exhibit exists...
            PresentInBoth("A1. INCLUDED AREAS AND TASKS", rendered, pdf);
            // ...but no blank site detail, and no blank or "None" row anywhere.
            foreach (var label in SiteDetailLabels) LabelAbsentFromBoth(label, rendered, pdf);
            AbsentFromBoth("Required completion time", rendered, pdf);
            AbsentFromBoth("________", rendered, pdf);
            Assert.DoesNotMatch(@":\s*None\b", rendered.PlainText);
            AbsentFromBoth("Not applicable", rendered, pdf);
            Assert.Empty(rendered.UnresolvedTokens);
        }

        [Fact]
        public void AFilledSiteDetailStillPrints()
        {
            var snapshot = Dcc49303882Configuration();
            snapshot.ScopeDetail = ScopeDetailMode.Detailed;
            snapshot.SiteDetails.ApproximateSquareFootage = "3,200 square feet";

            var text = ContractRenderer.Render(snapshot).PlainText;
            Assert.Contains("APPROXIMATE SERVICED SQUARE FOOTAGE: 3,200 square feet.", text);
            Assert.DoesNotContain("CUSTOMER RESTROOM AND FIXTURE COUNTS", text);
        }

        [Fact]
        public void ABlankCompletionTimeIsNotMentionedAnywhere()
        {
            var (rendered, pdf, _) = Generate(Dcc49303882Configuration());

            AbsentFromBoth("Required completion time", rendered, pdf);
            AbsentFromBoth("completion time is stated", rendered, pdf);
            AbsentFromBoth("completion deadline", rendered, pdf);
            PresentInBoth("Changes to the day or arrival window require mutual written confirmation", rendered, pdf);
        }

        [Fact]
        public void AnAgreedCompletionTimeIsStatedAndReferenced()
        {
            var snapshot = Dcc49303882Configuration();
            snapshot.Schedule.CompletionTime = "by 6:00 AM";

            var text = ContractRenderer.Render(snapshot).PlainText;
            Assert.Contains("Required completion time: by 6:00 AM", text);
            Assert.Contains("The required completion time is stated in Exhibit B.", text);
            Assert.Contains("day, arrival window or completion deadline", text);
        }

        [Theory]
        [InlineData(ScopeDetailMode.Detailed)]
        [InlineData(ScopeDetailMode.Simplified)]
        [InlineData(ScopeDetailMode.Omitted)]
        public void NoOptionalValueEverRendersAsNoneNotApplicableOrABlank(ScopeDetailMode mode)
        {
            var snapshot = Dcc49303882Configuration();
            snapshot.ScopeDetail = mode;
            var (rendered, pdf, _) = Generate(snapshot);

            AbsentFromBoth("________", rendered, pdf);
            AbsentFromBoth("Not applicable", rendered, pdf);
            Assert.DoesNotContain(rendered.PlainText.Replace("\r", string.Empty).Split('\n'),
                line => Regex.IsMatch(line, @":\s*None\.?\s*$"));
            Assert.DoesNotContain(rendered.Blocks, b => b.Kind == ContractBlockKind.ExhibitRow
                && (b.Text.Trim() is "" or "None" or "None." or "Not applicable"));
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  7-9. No minimum commitment
        // ══════════════════════════════════════════════════════════════════════════════════════

        [Fact]
        public void NoMinimumCommitmentContractHasNoZeroMonthConstruct()
        {
            var (rendered, pdf, _) = Generate(Dcc49303882Configuration());

            AbsentFromBoth("zero (0)", rendered, pdf);
            AbsentFromBoth("Minimum Commitment End Date", rendered, pdf);
            AbsentFromBoth("Initial Term End Date", rendered, pdf);
            AbsentFromBoth("Minimum Commitment Period", rendered, pdf);
            AbsentFromBoth("Initial Term", rendered, pdf);
            AbsentFromBoth("-month anniversary", rendered, pdf);
            // The impossible date: an Initial Term ending the day before commencement.
            AbsentFromBoth("October 3, 2026", rendered, pdf);
        }

        [Fact]
        public void NoMinimumCommitmentContractRunsMonthToMonthFromCommencement()
        {
            var (rendered, pdf, _) = Generate(Dcc49303882Configuration());

            PresentInBoth("It begins on the Service Commencement Date stated in Exhibit B and continues "
                          + "on a month-to-month basis until terminated in accordance with Section 4.", rendered, pdf);
            PresentInBoth("No minimum service commitment applies.", rendered, pdf);
            RowInBoth("Service Commencement Date", "October 4, 2026", rendered, pdf);
        }

        [Fact]
        public void ThirtyDayTerminationAppearsAndNoOtherPeriod()
        {
            var (rendered, pdf, _) = Generate(Dcc49303882Configuration());

            PresentInBoth("Either Party may terminate ongoing Services with at least thirty (30) calendar "
                          + "days' written notice", rendered, pdf);
            AbsentFromBoth("sixty (60) calendar days' written notice", rendered, pdf);
        }

        [Fact]
        public void SectionThirtySixSaysOnlyThatNoCommitmentApplies()
        {
            var text = ContractRenderer.Render(Dcc49303882Configuration()).PlainText;
            var section = text.Substring(text.IndexOf("36. CANCELLATION", StringComparison.Ordinal));
            section = section.Substring(0, section.IndexOf("EXHIBIT A", StringComparison.Ordinal));

            Assert.Contains("(h) Minimum commitment. No minimum service commitment applies.", section);
            Assert.DoesNotContain("Initial Term", section);
            Assert.DoesNotContain("zero", section);
        }

        /// <summary>The derived dates do not exist without a commitment - not zero-month dates.</summary>
        [Fact]
        public void TheCommitmentDatesAreNullWithoutACommitment()
        {
            var term = new TermSnapshot { ServiceCommencementDate = new DateTime(2026, 10, 4) };

            Assert.Null(term.ResolveMinimumCommitmentEndDate());
            Assert.Null(term.ResolveInitialTermEndDate());

            term.MinimumCommitmentMonths = 6;
            term.InitialTermMonths = 6;
            Assert.Equal(new DateTime(2027, 4, 4), term.ResolveMinimumCommitmentEndDate());
            Assert.Equal(new DateTime(2027, 4, 3), term.ResolveInitialTermEndDate());
        }

        [Fact]
        public void ACustomCommitmentStillPrintsItsTermAndDates()
        {
            var snapshot = Dcc49303882Configuration();
            snapshot.Term.MinimumCommitmentMonths = 6;
            snapshot.Term.InitialTermMonths = 6;

            var text = ContractRenderer.Render(snapshot).PlainText;
            Assert.Contains("The Initial Term runs for six (6) months from the Service Commencement Date", text);
            Assert.Contains("Minimum Commitment End Date: April 4, 2027", text);
            Assert.Contains("Initial Term End Date: April 3, 2027", text);
            Assert.DoesNotContain("No minimum service commitment applies.", text);
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  10-11. Supplies reach the document; no hand soap
        // ══════════════════════════════════════════════════════════════════════════════════════

        [Fact]
        public void TheSelectedSupplyAllocationReachesTheContract()
        {
            var (rendered, pdf, _) = Generate(Dcc49303882Configuration());

            RowInBoth("Cleaning equipment, tools, chemicals, products and ordinary cleaning supplies", "Client", rendered, pdf);
            RowInBoth("Trash bags and liners", "Client", rendered, pdf);
            RowInBoth("Paper towels", "Client", rendered, pdf);
            RowInBoth("Toilet tissue", "Client", rendered, pdf);
            PresentInBoth("Client supplies, at its own cost, the cleaning equipment", rendered, pdf);

            // The old hardcoded split is nowhere, and B2 no longer restates supplies at all.
            AbsentFromBoth("Contractor supplies the included labor, equipment, products", rendered, pdf);
            AbsentFromBoth("Client supplies toilet tissue, paper towels and trash can liners", rendered, pdf);
            var b2 = rendered.PlainText.Substring(rendered.PlainText.IndexOf("B2. PRICING AND PAYMENT", StringComparison.Ordinal));
            b2 = b2.Substring(0, b2.IndexOf("B3.", StringComparison.Ordinal));
            Assert.DoesNotContain("supplies", b2, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void AMixedAllocationPrintsEachPartysItems()
        {
            var snapshot = Dcc49303882Configuration();
            snapshot.Supplies = new SuppliesSnapshot
            {
                EquipmentProvidedBy = SupplyProvider.Contractor,
                TrashLinersProvidedBy = SupplyProvider.Contractor,
                PaperTowelsProvidedBy = SupplyProvider.Client,
                ToiletTissueProvidedBy = SupplyProvider.Client
            };

            var text = ContractRenderer.Render(snapshot).PlainText;
            Assert.Contains("Cleaning equipment, tools, chemicals, products and ordinary cleaning supplies: Contractor", text);
            Assert.Contains("Trash bags and liners: Contractor", text);
            Assert.Contains("Paper towels: Client", text);
            Assert.Contains("Toilet tissue: Client", text);
        }

        [Fact]
        public void AnUnansweredAllocationIsFlaggedNeverAssumed()
        {
            var snapshot = Dcc49303882Configuration();
            snapshot.Supplies = new SuppliesSnapshot();

            var rendered = ContractRenderer.Render(snapshot);
            Assert.Contains("TRASH_LINERS_PROVIDED_BY", rendered.UnresolvedTokens);
            Assert.Contains("EQUIPMENT_PROVIDED_BY", rendered.UnresolvedTokens);
            Assert.DoesNotContain("Trash bags and liners: Client", rendered.PlainText);
            Assert.DoesNotContain("Trash bags and liners: Contractor", rendered.PlainText);
            Assert.Contains("Supplies: who provides trash bags and liners",
                ContractService.DescribeMissingFields(rendered.UnresolvedTokens));
        }

        [Theory]
        [InlineData(ScopeDetailMode.Detailed)]
        [InlineData(ScopeDetailMode.Simplified)]
        [InlineData(ScopeDetailMode.Omitted)]
        public void TheGeneratedPdfNeverMentionsHandSoap(ScopeDetailMode mode)
        {
            var snapshot = Dcc49303882Configuration();
            snapshot.ScopeDetail = mode;
            var (rendered, pdf, _) = Generate(snapshot);

            AbsentFromBoth("hand soap", rendered, pdf);
            AbsentFromBoth("soap", rendered, pdf);
            AbsentFromBoth("dispenser", rendered, pdf);
            Assert.DoesNotContain("soap", ContractTemplateSeed.BodyText, StringComparison.OrdinalIgnoreCase);
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  12-15. Exhibit B numbering: B3 supplies, B4 contacts, B5 only when populated
        // ══════════════════════════════════════════════════════════════════════════════════════

        [Fact]
        public void ExhibitBIsNumberedB1ToB4WithNoEmptyB5()
        {
            var (rendered, pdf, _) = Generate(Dcc49303882Configuration());
            var subs = rendered.Blocks.Where(b => b.Kind == ContractBlockKind.SubHeading && b.Text.StartsWith("B"))
                .Select(b => b.Text).ToList();

            Assert.Equal(new[]
            {
                "B1. SERVICE DATES AND SCHEDULE", "B2. PRICING AND PAYMENT",
                "B3. SUPPLIES, EQUIPMENT AND CONSUMABLES", "B4. AUTHORIZED REPRESENTATIVES AND CONTACTS"
            }, subs);
            AbsentFromBoth("B5.", rendered, pdf);
            AbsentFromBoth("ADDITIONAL INSURANCE ENDORSEMENTS", rendered, pdf);
            AbsentFromBoth("B3. INSURANCE", rendered, pdf);
            AbsentFromBoth("Additional endorsements: None", rendered, pdf);
            AbsentFromBoth("Premium adjustment", rendered, pdf);
            // Baseline insurance stays in Section 20.
            PresentInBoth("20. INSURANCE", rendered, pdf);
        }

        [Fact]
        public void B5AppearsOnlyWithAnAgreedEndorsementAndOnlyItsFilledRows()
        {
            var snapshot = Dcc49303882Configuration();
            snapshot.Insurance.AgreedEndorsements = "Additional insured: the building landlord";

            var rendered = ContractRenderer.Render(snapshot);
            Assert.Contains(rendered.Blocks, b => b.Kind == ContractBlockKind.SubHeading
                && b.Text == "B5. ADDITIONAL INSURANCE ENDORSEMENTS");
            Assert.Contains("Additional insured: the building landlord", rendered.PlainText);
            Assert.DoesNotContain("Insurer, policy, endorsement form", rendered.PlainText);
            Assert.DoesNotContain("Agreed additional premium", rendered.PlainText);
        }

        [Fact]
        public void B4DropsBlankOptionalContactRows()
        {
            var text = ContractRenderer.Render(Dcc49303882Configuration()).PlainText;

            Assert.Contains("Client primary on-call contact: Jamie Client", text);
            Assert.DoesNotContain("backup on-call contact:", text);
            Assert.DoesNotContain("approval email:", text);
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  17. The warning banner, and the send gate, follow the configuration
        // ══════════════════════════════════════════════════════════════════════════════════════

        [Fact]
        public void TheBannerIgnoresEverythingTheConfigurationHides()
        {
            var snapshot = Dcc49303882Configuration();
            // Everything optional or hidden is blank: site details, B5, backups, completion time.
            var rendered = ContractRenderer.Render(snapshot);
            Assert.Empty(rendered.UnresolvedTokens);

            // A genuinely required value still is flagged.
            snapshot.Contacts.ClientOnCallName = null;
            snapshot.Contacts.ClientOnCallPhone = null;
            snapshot.Client.Phone = null;
            Assert.Equal(new[] { "CLIENT_ON_CALL_CONTACT" }, ContractRenderer.Render(snapshot).UnresolvedTokens);
        }

        [Fact]
        public async Task SendingIsRefusedOnlyForGenuinelyMissingRequiredInformation()
        {
            await using var harness = await ServiceHarness.CreateAsync();

            // Complete configuration: the send is not refused for missing information. (It then
            // reaches the mail step, which the harness records.)
            var complete = await harness.AddPreviewedContractAsync(Dcc49303882Configuration());
            await harness.Service.SendForClientReviewAsync(complete, adminId: 1);
            Assert.Single(harness.SentMail);

            // An unanswered supplies allocation blocks both sends, and says what is missing.
            var unset = Dcc49303882Configuration();
            unset.Supplies = new SuppliesSnapshot();
            var incomplete = await harness.AddPreviewedContractAsync(unset);

            var review = await Assert.ThrowsAsync<ContractWorkflowException>(
                () => harness.Service.SendForClientReviewAsync(incomplete, adminId: 1));
            Assert.Contains("Supplies: who provides paper towels", review.Message);
            var signing = await Assert.ThrowsAsync<ContractWorkflowException>(
                () => harness.Service.SendForSignatureAsync(incomplete, adminId: 1, expiryDays: null));
            Assert.Contains("Supplies: who provides toilet tissue", signing.Message);
            Assert.Single(harness.SentMail);
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  18. Historical snapshots are untouched
        // ══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// A version frozen on an older body re-renders the words it was signed with. The legacy
        /// tokens keep their old behaviour - ruled blank, "None" - because this body reads them.
        /// </summary>
        [Fact]
        public void AFrozenOlderBodyStillRendersExactlyAsSigned()
        {
            var snapshot = Dcc49303882Configuration();
            snapshot.ScopeDetail = ScopeDetailMode.Detailed;
            snapshot.Term.MinimumCommitmentMonths = 10;
            snapshot.Term.InitialTermMonths = 10;
            snapshot.TemplateBodyText =
                "## 3. TERM\n"
                + "(a) The Initial Term runs for {{INITIAL_TERM_MONTHS}} months, ending {{INITIAL_TERM_END_DATE}}.\n"
                + "REQUIRED COMPLETION TIME: {{COMPLETION_TIME}}.\n"
                + "CUSTOMER RESTROOM AND FIXTURE COUNTS: {{CUSTOMER_RESTROOM_COUNTS}}.\n"
                + "(a) Contractor supplies ... Hand soap and its dispensers are not included.\n";
            var before = ContractRenderer.Render(snapshot);

            Assert.Contains("The Initial Term runs for ten (10) months, ending August 3, 2027.", before.PlainText);
            Assert.Contains("REQUIRED COMPLETION TIME: None.", before.PlainText);
            Assert.Contains("CUSTOMER RESTROOM AND FIXTURE COUNTS: ________________.", before.PlainText);
            Assert.Contains("Hand soap and its dispensers are not included.", before.PlainText);

            // Deterministic: the same frozen snapshot hashes the same forever.
            Assert.Equal(before.Sha256, ContractRenderer.Render(snapshot).Sha256);
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  The root cause: getting the finished body into the database and into the draft
        // ══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// THE SECOND WAY NEW WORDING NEVER ARRIVED. The seeded description outgrew its column
        /// (539 > 500). MariaDB refused the insert, startup logged it and carried on, and the old
        /// version stayed the default. The in-memory provider ignores lengths, so check the
        /// seeded strings against the model's own limits here.
        /// </summary>
        [Fact]
        public void TheSeededTemplateFitsItsDatabaseColumns()
        {
            int Max(string property) => ((System.ComponentModel.DataAnnotations.StringLengthAttribute)
                typeof(ContractTemplate).GetProperty(property)!
                    .GetCustomAttributes(typeof(System.ComponentModel.DataAnnotations.StringLengthAttribute), false)
                    .Single()).MaximumLength;

            Assert.True(ContractTemplateSeed.TemplateDescription.Length <= Max(nameof(ContractTemplate.Description)),
                $"TemplateDescription is {ContractTemplateSeed.TemplateDescription.Length} characters.");
            Assert.True(ContractTemplateSeed.TemplateName.Length <= Max(nameof(ContractTemplate.Name)));
            Assert.True(ContractTemplateSeed.TemplateVersion.Length <= Max(nameof(ContractTemplate.Version)));
        }

        /// <summary>
        /// A row already at the current version keeps its wording - an admin's edit, or a row some
        /// other application seeded into a shared database. The seeder cannot tell which, so it
        /// never guesses (it logs); the cure for the second case is a database per application.
        /// </summary>
        [Fact]
        public async Task ARowAlreadyAtTheCurrentVersionIsNeverOverwritten()
        {
            await using var db = ServiceHarness.NewContext();
            var created = new DateTime(2026, 9, 26, 16, 25, 42, DateTimeKind.Utc);
            const string edited = "## 1. SERVICES\n(a) A SuperAdmin's own wording.\n@SIGNATURE_BLOCK\n";
            db.ContractTemplates.Add(new ContractTemplate
            {
                Name = ContractTemplateSeed.TemplateName,
                Version = ContractTemplateSeed.TemplateVersion,
                BodyText = edited, IsActive = true, IsDefault = true,
                CreatedAt = created, UpdatedAt = created.AddDays(2)
            });
            await db.SaveChangesAsync();

            await new ContractSeedService(db, NullLogger<ContractSeedService>.Instance).SeedAsync();

            var row = await db.ContractTemplates.SingleAsync(t => t.Version == ContractTemplateSeed.TemplateVersion);
            Assert.Equal(edited, row.BodyText);
        }

        /// <summary>
        /// A DRAFT SAVED BEFORE THE BODY WAS CORRECTED renders the corrected body when the admin
        /// presses Generate without re-saving - the same copy a save would have made.
        /// </summary>
        [Fact]
        public async Task GeneratingADraftRecopiesItsTemplatesCurrentBody()
        {
            await using var harness = await ServiceHarness.CreateAsync();
            var stale = Dcc49303882Configuration();
            stale.TemplateBodyText =
                "## EXHIBIT A\nA1. INCLUDED AREAS AND TASKS\nAPPROXIMATE SERVICED SQUARE FOOTAGE: {{SQUARE_FOOTAGE}}.\n"
                + "Initial Term End Date: {{INITIAL_TERM_END_DATE}}.\n@SIGNATURE_BLOCK\n";
            var contractId = await harness.AddDraftAsync(stale);

            var version = await harness.Service.GeneratePreviewAsync(contractId, adminId: 1);

            var frozen = ContractSnapshot.Parse(version.FullSnapshotJson);
            Assert.Equal(ContractTemplateSeed.BodyText, frozen.TemplateBodyText);
            Assert.DoesNotContain("A1. INCLUDED AREAS", version.RenderedDocumentHtml);
            Assert.DoesNotContain("________", version.RenderedDocumentHtml);
            Assert.Contains("The Parties have agreed the service scope separately", version.RenderedDocumentHtml);
            Assert.Empty(ContractRenderer.Render(frozen).UnresolvedTokens);
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  Length: the regenerated document is materially shorter
        // ══════════════════════════════════════════════════════════════════════════════════════

        [Fact]
        public void TheSimplifiedNoCommitmentContractIsMateriallyShorterThanTheDetailedOne()
        {
            var simplified = Generate(Dcc49303882Configuration()).Pages;
            var detailedSnapshot = Dcc49303882Configuration();
            detailedSnapshot.ScopeDetail = ScopeDetailMode.Detailed;
            var detailed = Generate(detailedSnapshot).Pages;

            Assert.True(simplified < detailed, $"simplified {simplified} pages, detailed {detailed}");
            Assert.True(simplified <= 22, $"simplified contract is {simplified} pages");
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  Section 17(e) — Background Checks (2026-10-01): new contracts only
        // ══════════════════════════════════════════════════════════════════════════════════════

        private const string BackgroundCheckClause =
            "(e) Background Checks. All personnel with access to the Premises must have passed a "
            + "background check before being assigned to the site.";

        /// <summary>The seeded body exactly as it was before 17(e): the clause line removed.</summary>
        private static string BodyWithoutSectionSeventeenE()
        {
            var body = Regex.Replace(ContractTemplateSeed.BodyText,
                Regex.Escape(BackgroundCheckClause) + @"\r?\n", string.Empty);
            Assert.NotEqual(ContractTemplateSeed.BodyText, body);
            return body;
        }

        /// <summary>The lines of Section 17, heading excluded, up to the next section heading.</summary>
        private static string[] SectionSeventeenLines()
        {
            var lines = Regex.Split(ContractTemplateSeed.BodyText, @"\r?\n");
            var start = Array.IndexOf(lines, "## 17. PERSONNEL AND SUBCONTRACTORS");
            Assert.True(start >= 0, "Section 17 heading not found");
            return lines.Skip(start + 1).TakeWhile(l => !l.StartsWith("## ")).Where(l => l.Length > 0).ToArray();
        }

        /// <summary>
        /// 17(e) sits directly after 17(d), and 17(a)-(d) are word for word what they were — the
        /// clause is an addition, not a rewording of the paragraphs around it.
        /// </summary>
        [Fact]
        public void SectionSeventeenEFollowsSeventeenDAndTheExistingLanguageIsUnchanged()
        {
            Assert.Equal(new[]
            {
                "(a) Contractor controls the staffing, work scheduling and supervision of its personnel, subject to the agreed service schedule. Contractor may substitute qualified personnel; substitution alone is not a breach.",
                "(b) Client may request removal of an individual for a documented, reasonable safety, security, or material performance concern. Requests may not be discriminatory, retaliatory, or otherwise unlawful. Contractor shall investigate promptly, take appropriate interim measures, and provide a qualified replacement within a reasonable time. Contractor retains employment and supervisory decisions, subject to applicable law.",
                "(c) Contractor may perform through its employees or qualified subcontractors and remains responsible for performance of the Services in either case.",
                "(d) Contractor shall use personnel qualified and trained for their assigned tasks and require subcontractors to comply with the applicable service, confidentiality, safety, employment, and insurance obligations. Contractor shall provide advance notice before giving a new subcontracting entity unattended access to the Premises. Client may raise reasonable documented security objections, and the Parties shall promptly resolve them without creating a general right to direct Contractor's workforce. Subcontracting does not release Contractor from its responsibilities under this Agreement.",
                BackgroundCheckClause
            }, SectionSeventeenLines());

            // Once, and only in Section 17.
            Assert.Single(Regex.Matches(ContractTemplateSeed.BodyText, Regex.Escape(BackgroundCheckClause)));
        }

        /// <summary>No main section moved: the numbered headings still run 1 through 36 in order.</summary>
        [Fact]
        public void NoMainSectionIsRenumbered()
        {
            var numbers = Regex.Matches(ContractTemplateSeed.BodyText, @"^## (\d+)\. ", RegexOptions.Multiline)
                .Select(m => int.Parse(m.Groups[1].Value))
                .ToList();

            // A heading may appear twice in a row as guarded variants (Section 3's commitment /
            // no-commitment forms), so the numbers never go backwards and cover 1-36 exactly.
            Assert.True(numbers.Zip(numbers.Skip(1)).All(p => p.Second >= p.First), string.Join(",", numbers));
            Assert.Equal(Enumerable.Range(1, 36), numbers.Distinct());
            Assert.Contains("## 17. PERSONNEL AND SUBCONTRACTORS", ContractTemplateSeed.BodyText);
            Assert.Contains("## 18. PERSONNEL COORDINATION", ContractTemplateSeed.BodyText);
            Assert.Contains("including Sections 1 through 36, {{EXHIBITS_SIGNED}}", ContractTemplateSeed.BodyText);
        }

        /// <summary>The clause reaches the preview text and the generated PDF, in every scope mode.</summary>
        [Theory]
        [InlineData(ScopeDetailMode.Detailed)]
        [InlineData(ScopeDetailMode.Simplified)]
        [InlineData(ScopeDetailMode.Omitted)]
        public void ANewContractPrintsSectionSeventeenE(ScopeDetailMode mode)
        {
            var snapshot = Dcc49303882Configuration();
            snapshot.ScopeDetail = mode;
            var (rendered, pdf, _) = Generate(snapshot);

            PresentInBoth(BackgroundCheckClause, rendered, pdf);
        }

        /// <summary>
        /// A body change is a version bump, or it reaches no database (see the 2.1 note on
        /// ContractTemplateSeed). The previous number is retired so the picker offers only 17(e).
        /// </summary>
        [Fact]
        public async Task TheSeederInsertsTheNewVersionAndRetiresTheOldOneWithoutRewritingIt()
        {
            await using var db = ServiceHarness.NewContext();
            var previousVersion = ContractTemplateSeed.SupersededVersions.Last();
            var previousBody = BodyWithoutSectionSeventeenE();
            db.ContractTemplates.Add(new ContractTemplate
            {
                Name = ContractTemplateSeed.TemplateName,
                Version = previousVersion,
                BodyText = previousBody,
                IsActive = true,
                IsDefault = true
            });
            await db.SaveChangesAsync();

            await new ContractSeedService(db, NullLogger<ContractSeedService>.Instance).SeedAsync();

            var previous = await db.ContractTemplates.SingleAsync(t => t.Version == previousVersion);
            var current = await db.ContractTemplates.SingleAsync(t => t.Version == ContractTemplateSeed.TemplateVersion);

            Assert.Equal(previousBody, previous.BodyText);
            Assert.DoesNotContain(BackgroundCheckClause, previous.BodyText);
            Assert.False(previous.IsActive);
            Assert.False(previous.IsDefault);

            Assert.Contains(BackgroundCheckClause, current.BodyText);
            Assert.True(current.IsActive);
            Assert.True(current.IsDefault);
        }

        /// <summary>
        /// A contract generated from the active template carries 17(e) in its frozen snapshot and
        /// its rendered document.
        /// </summary>
        [Fact]
        public async Task GeneratingANewContractFreezesSectionSeventeenEIntoTheVersion()
        {
            await using var harness = await ServiceHarness.CreateAsync();
            var contractId = await harness.AddDraftAsync(Dcc49303882Configuration());

            var version = await harness.Service.GeneratePreviewAsync(contractId, adminId: 1);

            var frozen = ContractSnapshot.Parse(version.FullSnapshotJson);
            Assert.Equal(ContractTemplateSeed.TemplateVersion, frozen.ContractTemplateVersion);
            Assert.Contains(BackgroundCheckClause, frozen.TemplateBodyText);
            Assert.Contains(BackgroundCheckClause, ContractRenderer.Render(frozen).PlainText);
            Assert.Equal(ContractRenderer.Render(frozen).Sha256, version.DocumentHashSha256);
        }

        /// <summary>
        /// AN EXISTING CONTRACT IS NOT TOUCHED. A signed contract frozen on the pre-17(e) body keeps
        /// its row, its version, its rendered document, its hash and its stored PDF byte for byte
        /// through a re-seed and through another contract being generated — and re-rendering it
        /// still produces the document that was signed, without 17(e).
        /// </summary>
        [Fact]
        public async Task AnExistingStoredContractAndItsPdfAreUnchanged()
        {
            await using var harness = await ServiceHarness.CreateAsync();

            var oldSnapshot = Dcc49303882Configuration();
            oldSnapshot.ContractTemplateVersion = ContractTemplateSeed.SupersededVersions.Last();
            oldSnapshot.TemplateBodyText = BodyWithoutSectionSeventeenE();
            var oldRendered = ContractRenderer.Render(oldSnapshot);
            Assert.DoesNotContain("Background Checks", oldRendered.PlainText);

            var pdfPath = Path.Combine(Path.GetTempPath(), $"dc-existing-contract-{Guid.NewGuid():N}.pdf");
            var pdfBytes = new ContractPdfService()
                .GenerateDocument(oldRendered, oldSnapshot, Block(), certificate: null, draftWatermark: false);
            await File.WriteAllBytesAsync(pdfPath, pdfBytes);
            try
            {
                var contract = new Contract
                {
                    ContractNumber = "DCC-2026-00000042",
                    ContractTemplateId = 1, ContractorProfileId = 1, ContractClientId = 1,
                    ContractServiceLocationId = 1, CreatedByAdminId = 1,
                    Status = ContractStatus.FullySigned,
                    DraftSnapshotJson = oldSnapshot.ToJson()
                };
                harness.Db.Contracts.Add(contract);
                await harness.Db.SaveChangesAsync();

                var stored = new ContractVersion
                {
                    ContractId = contract.Id,
                    VersionNumber = 1,
                    FullSnapshotJson = oldSnapshot.ToJson(),
                    RenderedDocumentHtml = oldRendered.Html,
                    RenderedDocumentPath = pdfPath,
                    DocumentHashSha256 = oldRendered.Sha256,
                    GeneratedAt = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc),
                    GeneratedByAdminId = 1
                };
                harness.Db.ContractVersions.Add(stored);
                await harness.Db.SaveChangesAsync();
                contract.CurrentVersionId = stored.Id;
                await harness.Db.SaveChangesAsync();

                string Fingerprint(Contract c, ContractVersion v) => string.Join("\u001f",
                    c.ContractNumber, c.Status, c.ContractTemplateId, c.CurrentVersionId, c.DraftSnapshotJson,
                    v.VersionNumber, v.FullSnapshotJson, v.RenderedDocumentHtml, v.RenderedDocumentPath,
                    v.DocumentHashSha256, v.GeneratedAt.ToString("O"), v.IsSuperseded);
                var before = Fingerprint(contract, stored);

                // Everything that happens on the next startup and the next new contract.
                await new ContractSeedService(harness.Db, NullLogger<ContractSeedService>.Instance).SeedAsync();
                var newId = await harness.AddDraftAsync(Dcc49303882Configuration());
                var newVersion = await harness.Service.GeneratePreviewAsync(newId, adminId: 1);
                Assert.Contains(BackgroundCheckClause, ContractSnapshot.Parse(newVersion.FullSnapshotJson).TemplateBodyText);

                harness.Db.ChangeTracker.Clear();
                var contractAfter = await harness.Db.Contracts.SingleAsync(c => c.Id == contract.Id);
                var versionAfter = await harness.Db.ContractVersions.SingleAsync(v => v.ContractId == contract.Id);
                Assert.Equal(before, Fingerprint(contractAfter, versionAfter));
                Assert.Equal(pdfBytes, await File.ReadAllBytesAsync(pdfPath));

                var reRendered = ContractRenderer.Render(ContractSnapshot.Parse(versionAfter.FullSnapshotJson));
                Assert.Equal(versionAfter.DocumentHashSha256, reRendered.Sha256);
                Assert.DoesNotContain("Background Checks", reRendered.PlainText);
            }
            finally
            {
                try { File.Delete(pdfPath); } catch { }
            }
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        // EXHIBIT B4'S CLIENT NOTICE EMAIL IS ITS OWN FIELD (2026-10-02)
        //
        // The row used to print the client record's email and had no input of its own. It now
        // reads OperationalContactsSnapshot.ClientNoticeEmail first. Null means a snapshot from
        // before the field and renders exactly as it always did; blank falls back to the client
        // record, then the approval email, then the operational email.
        // ══════════════════════════════════════════════════════════════════════════════════════

        private static ContractSnapshot WithThreeDifferentClientEmails()
        {
            var snapshot = Dcc49303882Configuration();
            snapshot.Client.NoticeEmail = "ap@example.test";
            snapshot.Contacts.ClientApprovalEmail = "approvals@example.test";
            snapshot.Contacts.ClientNoticeEmail = "legal@example.test";
            snapshot.Contacts.ClientOperationalEmail = "facilities@example.test";
            return snapshot;
        }

        [Fact]
        public void ASeparateClientNoticeEmailIsRenderedOnThePreviewAndThePdf()
        {
            var (rendered, pdf, _) = Generate(WithThreeDifferentClientEmails());

            RowInBoth("Client approval email", "approvals@example.test", rendered, pdf);
            RowInBoth("Client notice email", "legal@example.test", rendered, pdf);
            RowInBoth("Client operational email", "facilities@example.test", rendered, pdf);

            // Not borrowed from the client record when the field is filled.
            Assert.DoesNotContain(rendered.Blocks, b => b.Kind == ContractBlockKind.ExhibitRow
                && b.Label == "Client notice email" && b.Text.Contains("ap@example.test"));
            Assert.Empty(rendered.UnresolvedTokens);
        }

        [Fact]
        public void ApprovalNoticeAndOperationalEmailsEachKeepTheirOwnValue()
        {
            var tokens = ContractPlaceholders.Build(WithThreeDifferentClientEmails());

            Assert.Equal("approvals@example.test", tokens["CLIENT_APPROVAL_EMAIL"]);
            Assert.Equal("legal@example.test", tokens["CLIENT_NOTICE_EMAIL"]);
            Assert.Equal("facilities@example.test", tokens["CLIENT_OPERATIONAL_EMAIL"]);
        }

        [Fact]
        public void TheClientNoticeEmailSurvivesTheSnapshotRoundTripOnItsOwn()
        {
            var parsed = ContractSnapshot.Parse(WithThreeDifferentClientEmails().ToJson());

            Assert.Equal("legal@example.test", parsed.Contacts.ClientNoticeEmail);
            Assert.Equal("approvals@example.test", parsed.Contacts.ClientApprovalEmail);
            Assert.Equal("facilities@example.test", parsed.Contacts.ClientOperationalEmail);
            Assert.Equal("ap@example.test", parsed.Client.NoticeEmail);
        }

        /// <summary>Blank (saved by the new form): client record → approval → operational.</summary>
        [Theory]
        [InlineData("ap@example.test", "approvals@example.test", "facilities@example.test", "ap@example.test")]
        [InlineData("", "approvals@example.test", "facilities@example.test", "approvals@example.test")]
        [InlineData(null, "  ", "facilities@example.test", "facilities@example.test")]
        public void ABlankClientNoticeEmailFallsBackInOrder(
            string? clientRecord, string? approval, string? operational, string expected)
        {
            var snapshot = Dcc49303882Configuration();
            snapshot.Client.NoticeEmail = clientRecord;
            snapshot.Contacts.ClientApprovalEmail = approval;
            snapshot.Contacts.ClientOperationalEmail = operational;
            snapshot.Contacts.ClientNoticeEmail = "";

            var (rendered, pdf, _) = Generate(snapshot);

            RowInBoth("Client notice email", expected, rendered, pdf);
            // Falling back READS the other field; it never writes into it.
            Assert.Equal("", snapshot.Contacts.ClientNoticeEmail);
            Assert.Equal(approval, snapshot.Contacts.ClientApprovalEmail);
            Assert.Equal(operational, snapshot.Contacts.ClientOperationalEmail);
        }

        /// <summary>
        /// Null = a snapshot written before the field existed: the client record's email and
        /// nothing else, even when it is blank and an approval email is on file - that is what the
        /// version printed when it was generated.
        /// </summary>
        [Theory]
        [InlineData("ap@example.test", "ap@example.test")]
        [InlineData(null, "")]
        public void ASnapshotFromBeforeTheFieldRendersExactlyAsBefore(string? clientRecord, string expected)
        {
            var snapshot = Dcc49303882Configuration();
            snapshot.Client.NoticeEmail = clientRecord;
            snapshot.Contacts.ClientApprovalEmail = "approvals@example.test";
            snapshot.Contacts.ClientOperationalEmail = "facilities@example.test";
            var legacyJson = snapshot.ToJson().Replace("\"clientNoticeEmail\":null,", string.Empty);
            Assert.DoesNotContain("clientNoticeEmail\"", legacyJson);

            var legacy = ContractSnapshot.Parse(legacyJson);
            Assert.Null(legacy.Contacts.ClientNoticeEmail);
            Assert.Equal(expected, ContractPlaceholders.Build(legacy)["CLIENT_NOTICE_EMAIL"]);
        }

        [Fact]
        public async Task GeneratingAPreviewFreezesTheSeparateClientNoticeEmailIntoTheVersion()
        {
            await using var harness = await ServiceHarness.CreateAsync();
            var contractId = await harness.AddDraftAsync(WithThreeDifferentClientEmails());

            var version = await harness.Service.GeneratePreviewAsync(contractId, adminId: 1);

            var frozen = ContractSnapshot.Parse(version.FullSnapshotJson);
            Assert.Equal("legal@example.test", frozen.Contacts.ClientNoticeEmail);
            Assert.Contains("legal@example.test", version.RenderedDocumentHtml);
            Assert.Equal(ContractRenderer.Render(frozen).Sha256, version.DocumentHashSha256);
        }

        /// <summary>
        /// A signed contract frozen before the field - the sharp case, whose client record has no
        /// email while an approval email IS on file - keeps its row, version, rendered document,
        /// hash and stored PDF byte for byte while a new contract with a separate notice email is
        /// generated, and re-rendering it (what Regenerate does) still produces the signed text.
        /// </summary>
        [Fact]
        public async Task AnExistingSignedContractIsUnchangedByTheSeparateNoticeEmail()
        {
            await using var harness = await ServiceHarness.CreateAsync();

            var signed = Dcc49303882Configuration();
            signed.Client.NoticeEmail = null;
            signed.Contacts.ClientApprovalEmail = "approvals@example.test";
            var signedJson = signed.ToJson().Replace("\"clientNoticeEmail\":null,", string.Empty);
            var signedRendered = ContractRenderer.Render(ContractSnapshot.Parse(signedJson));

            var pdfPath = Path.Combine(Path.GetTempPath(), $"dc-signed-notice-{Guid.NewGuid():N}.pdf");
            var pdfBytes = new ContractPdfService().GenerateDocument(
                signedRendered, signed, Block(), certificate: null, draftWatermark: false);
            await File.WriteAllBytesAsync(pdfPath, pdfBytes);
            try
            {
                var contract = new Contract
                {
                    ContractNumber = "DCC-2026-00000043",
                    ContractTemplateId = 1, ContractorProfileId = 1, ContractClientId = 1,
                    ContractServiceLocationId = 1, CreatedByAdminId = 1,
                    Status = ContractStatus.FullySigned,
                    DraftSnapshotJson = signedJson
                };
                harness.Db.Contracts.Add(contract);
                await harness.Db.SaveChangesAsync();
                var stored = new ContractVersion
                {
                    ContractId = contract.Id, VersionNumber = 1,
                    FullSnapshotJson = signedJson,
                    RenderedDocumentHtml = signedRendered.Html,
                    RenderedDocumentPath = pdfPath,
                    DocumentHashSha256 = signedRendered.Sha256,
                    GeneratedAt = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc),
                    GeneratedByAdminId = 1
                };
                harness.Db.ContractVersions.Add(stored);
                await harness.Db.SaveChangesAsync();
                contract.CurrentVersionId = stored.Id;
                await harness.Db.SaveChangesAsync();

                string Fingerprint(Contract c, ContractVersion v) => string.Join("\u001f",
                    c.Status, c.CurrentVersionId, c.DraftSnapshotJson, v.FullSnapshotJson,
                    v.RenderedDocumentHtml, v.RenderedDocumentPath, v.DocumentHashSha256);
                var before = Fingerprint(contract, stored);

                var newId = await harness.AddDraftAsync(WithThreeDifferentClientEmails());
                await harness.Service.GeneratePreviewAsync(newId, adminId: 1);

                harness.Db.ChangeTracker.Clear();
                var contractAfter = await harness.Db.Contracts.SingleAsync(c => c.Id == contract.Id);
                var versionAfter = await harness.Db.ContractVersions.SingleAsync(v => v.ContractId == contract.Id);
                Assert.Equal(before, Fingerprint(contractAfter, versionAfter));
                Assert.Equal(pdfBytes, await File.ReadAllBytesAsync(pdfPath));

                var reRendered = ContractRenderer.Render(ContractSnapshot.Parse(versionAfter.FullSnapshotJson));
                Assert.Equal(versionAfter.DocumentHashSha256, reRendered.Sha256);
                Assert.DoesNotContain(reRendered.Blocks, b => b.Kind == ContractBlockKind.ExhibitRow
                    && b.Label == "Client notice email" && b.Text.Contains("approvals@example.test"));
            }
            finally
            {
                try { File.Delete(pdfPath); } catch { }
            }
        }

        // ── A ContractService over an in-memory database ────────────────────────────────────

        private sealed class ServiceHarness : IAsyncDisposable
        {
            public ApplicationDbContext Db { get; private init; } = null!;
            public ContractService Service { get; private init; } = null!;
            public List<string> SentMail => _sentSink;
            private string _root = string.Empty;
            private int _templateId;

            public static ApplicationDbContext NewContext() =>
                new(new DbContextOptionsBuilder<ApplicationDbContext>()
                    .UseInMemoryDatabase($"contract-regression-{Guid.NewGuid()}", b => b.EnableNullChecks(false))
                    .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                    .Options);

            public static async Task<ServiceHarness> CreateAsync()
            {
                var root = Path.Combine(Path.GetTempPath(), $"dc-contract-regression-{Guid.NewGuid():N}");
                var config = new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Contracts:StoragePath"] = root,
                        ["Frontend:BaseUrl"] = "https://example.test"
                    })
                    .Build();
                var db = NewContext();
                var sent = new List<string>();
                var email = RecurringDiscountRegressionTests.Stub<IEmailService>((method, args) =>
                {
                    sent.Add(method.Name);
                    return Task.CompletedTask;
                });

                var harness = new ServiceHarness
                {
                    Db = db,
                    Service = new ContractService(
                        db,
                        new ContractPdfService(),
                        new ContractStorage(config, new PurgeTestEnvironment(root)),
                        new ContractNotificationService(email, config, NullLogger<ContractNotificationService>.Instance),
                        new BillingSettingsService(db),
                        NullLogger<ContractService>.Instance)
                };
                harness._root = root;
                harness._sentSink = sent;

                await new ContractSeedService(db, NullLogger<ContractSeedService>.Instance).SeedAsync();
                harness._templateId = (await db.ContractTemplates.SingleAsync(t => t.IsDefault)).Id;
                return harness;
            }

            private List<string> _sentSink = new();

            public async Task<int> AddDraftAsync(ContractSnapshot snapshot)
            {
                snapshot.ContractTemplateId = _templateId;
                var contract = new Contract
                {
                    ContractNumber = $"DCC-2026-{Random.Shared.Next(10000000, 99999999)}",
                    ContractTemplateId = _templateId,
                    ContractorProfileId = 1, ContractClientId = 1, ContractServiceLocationId = 1,
                    CreatedByAdminId = 1,
                    Status = ContractStatus.Draft,
                    DraftSnapshotJson = snapshot.ToJson()
                };
                Db.Contracts.Add(contract);
                await Db.SaveChangesAsync();
                return contract.Id;
            }

            public async Task<int> AddPreviewedContractAsync(ContractSnapshot snapshot)
            {
                var id = await AddDraftAsync(snapshot);
                await Service.GeneratePreviewAsync(id, adminId: 1);
                return id;
            }

            public async ValueTask DisposeAsync()
            {
                await Db.DisposeAsync();
                try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
            }
        }
    }
}
