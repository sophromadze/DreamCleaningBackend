using System.Text;
using System.Text.RegularExpressions;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.DTOs.Commercial;
using DreamCleaningBackend.Models.Commercial;
using DreamCleaningBackend.Models.Contracts;
using DreamCleaningBackend.Services.Commercial;
using DreamCleaningBackend.Services.Contracts;
using UglyToad.PdfPig;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// A WEEKLY FLAT FEE (template v3.0, 2026-09-30).
    ///
    /// The client in DCC-2026-12918497 pays $875 a week for six visits a week, invoiced weekly. The
    /// contract must say exactly that - "$875.00 per calendar week", tax $77.66 computed on the
    /// week, total $952.66 - and never present "$145.83 per completed scheduled visit" as the
    /// agreed price. The per-visit caps still need one visit's value; that is an ALLOCATION of the
    /// weekly fee (875 / 6) kept at full precision, with only each cap rounded.
    ///
    /// The same contract also said "One (1) scheduled cleaning visit per calendar week" with six
    /// days ticked: the form's default count of 1 was never changed and the only warning was off
    /// for flexible schedules. The count is authoritative and is never rewritten; a mismatch warns.
    /// </summary>
    public class ContractWeeklyFlatFeeTests
    {
        static ContractWeeklyFlatFeeTests()
        {
            QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        }

        private static readonly List<string> SixDays =
            new() { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Sunday" };

        /// <summary>DCC-2026-12918497's configuration, with neutral party data.</summary>
        private static ContractSnapshot Weekly(decimal fee = 875m, int visits = 6,
            ContractPricingBasis basis = ContractPricingBasis.WeeklyFlatFee)
        {
            var snapshot = new ContractSnapshot
            {
                ContractNumber = "DCC-2026-00000002",
                VersionNumber = 1,
                EffectiveDate = new DateTime(2026, 10, 2),
                TemplateBodyText = ContractTemplateSeed.BodyText,
                PremisesType = "office",
                ScopeDetail = ScopeDetailMode.Simplified,
                Contractor = new ContractorSnapshot
                {
                    LegalEntityName = "Nodar Alania Inc.", Dba = "Dream Cleaning NYC",
                    EntityType = "a New York corporation", Address = "8800 20th Ave, Apt 2B",
                    City = "Brooklyn", State = "NY", Zip = "11214",
                    NoticeEmail = "hello@dreamcleaningnyc.com", Phone = "9299301525"
                },
                Client = new ClientSnapshot
                {
                    LegalEntityName = "Example Office LLC", EntityType = "a limited liability company",
                    PrincipalAddress = "1 Example Plaza", City = "Manhattan", State = "NY", Zip = "10001",
                    NoticeEmail = "ops@example.test", Phone = "2125550100"
                },
                ServiceLocation = new ServiceLocationSnapshot
                {
                    BusinessBrand = "Example", Address = "1 Example Plaza", City = "Manhattan",
                    State = "NY", Zip = "10001"
                },
                ContractorSigner = new SignerSnapshot { FirstName = "Nodar", LastName = "Alania", Title = "CEO", Email = "hello@dreamcleaningnyc.com" },
                ClientSigner = new SignerSnapshot { FirstName = "Jamie", LastName = "Client", Email = "ops@example.test" },
                Schedule = new ScheduleSnapshot
                {
                    FrequencyUnit = "calendar week", VisitsPerPeriod = visits,
                    ServiceDays = new List<string>(SixDays), ServiceDay = "Monday",
                    ArrivalWindowStart = "9:30 AM", ArrivalWindowEnd = "10:30 AM",
                    FlexibleScheduling = true, PerformedWhileClosed = true, AccessType = "key provided by Client"
                },
                Billing = new BillingCadenceSnapshot { Frequency = ContractBillingFrequency.Weekly, IntervalCount = 1 },
                Term = new TermSnapshot { ServiceCommencementDate = new DateTime(2026, 10, 4) },
                Pricing = new PricingSnapshot
                {
                    PricingBasis = basis, PriceMode = ContractPriceMode.PreTax, PriceInput = fee,
                    SalesTaxRatePercent = 8.875m, CancellationPercent = 50m, LiabilityCapMultiple = 3
                },
                Contacts = new OperationalContactsSnapshot
                {
                    ClientOnCallName = "Jamie Client", ClientOnCallPhone = "2125550100",
                    ContractorSupervisorName = "Nodar Alania"
                },
                Supplies = new SuppliesSnapshot
                {
                    EquipmentProvidedBy = SupplyProvider.Contractor, TrashLinersProvidedBy = SupplyProvider.Client,
                    PaperTowelsProvidedBy = SupplyProvider.Client, ToiletTissueProvidedBy = SupplyProvider.Client
                },
                Scope = ContractScopeTemplateSeed.All().First(t => t.Name == "Office").Structure.Clone()
            };
            ContractPricingCalculator.Recalculate(snapshot.Pricing, snapshot.Schedule);
            return snapshot;
        }

        private static (RenderedContract Rendered, string Pdf) Generate(ContractSnapshot snapshot)
        {
            var rendered = ContractRenderer.Render(snapshot);
            var block = new ContractSignatureBlockDto
            {
                Contractor = new ContractSignaturePartyDto { PartyLabel = "CONTRACTOR", EntityName = "x", SignerName = "x" },
                Client = new ContractSignaturePartyDto { PartyLabel = "CLIENT", EntityName = "y", SignerName = "y" }
            };
            var bytes = new ContractPdfService().GenerateDocument(rendered, snapshot, block, null, true);
            using var pdf = PdfDocument.Open(bytes);
            var sb = new StringBuilder();
            foreach (var page in pdf.GetPages()) sb.AppendLine(page.Text);
            return (rendered, sb.ToString());
        }

        private static string Squash(string s) => Regex.Replace(s, @"\s+", string.Empty).ToLowerInvariant();

        // ── 1, 14: per visit is unchanged ─────────────────────────────────────────────────────

        [Fact]
        public void PerVisitPricingIsUnchanged()
        {
            var snapshot = Weekly(fee: 150m, visits: 3, basis: ContractPricingBasis.PerVisit);
            var p = snapshot.Pricing;

            Assert.Equal(150.00m, p.PreTaxPrice);
            Assert.Equal(13.31m, p.SalesTaxAmount);
            Assert.Equal(163.31m, p.TotalPrice);
            Assert.Equal(150m, p.PerVisitAllocation);
            Assert.Equal(75.00m, p.CancellationAmount);
            Assert.Equal(150.00m, p.LockoutFee);
            Assert.Equal(450.00m, p.LiabilityCapAmount);

            var text = ContractRenderer.Render(snapshot).PlainText;
            Assert.Contains("The recurring service fee is $150.00 before tax per completed scheduled visit.", text);
            Assert.Contains("Recurring pre-tax fee: $150.00 per completed scheduled visit.", text);
            Assert.DoesNotContain("per calendar week for the agreed", text);
            Assert.DoesNotContain("Weekly pre-tax service fee", text);
            Assert.DoesNotContain("allocated share", text);
        }

        /// <summary>A snapshot frozen before the basis existed deserialises as per visit.</summary>
        [Fact]
        public void ALegacySnapshotReadsAsPerVisit()
        {
            var json = Weekly(basis: ContractPricingBasis.PerVisit).ToJson()
                .Replace("\"pricingBasis\":0,", string.Empty);
            Assert.DoesNotContain("pricingBasis", json);
            Assert.Equal(ContractPricingBasis.PerVisit, ContractSnapshot.Parse(json).Pricing.PricingBasis);
        }

        // ── 2, 5, 6: the weekly figures, taxed once on the week ───────────────────────────────

        [Fact]
        public void TheWeeklyFeeIsTaxedOnceOnTheWeek()
        {
            var p = Weekly().Pricing;

            Assert.Equal(875.00m, p.PreTaxPrice);
            Assert.Equal(77.66m, p.SalesTaxAmount);   // 875 x 8.875% = 77.65625
            Assert.Equal(952.66m, p.TotalPrice);
            Assert.Equal(6, p.ScheduledVisitsPerFeePeriod);
        }

        [Fact]
        public void ATaxInclusiveWeeklyFeeSplitsExactly()
        {
            var snapshot = Weekly(fee: 952.66m);
            snapshot.Pricing.PriceMode = ContractPriceMode.TaxInclusive;
            ContractPricingCalculator.Recalculate(snapshot.Pricing, snapshot.Schedule);

            Assert.Equal(952.66m, snapshot.Pricing.TotalPrice);
            Assert.Equal(875.00m, snapshot.Pricing.PreTaxPrice);
            Assert.Equal(77.66m, snapshot.Pricing.SalesTaxAmount);
        }

        // ── 9-12: the allocation keeps full precision; only each cap is rounded ──────────────

        [Fact]
        public void TheAllocationKeepsFullPrecision()
        {
            var p = Weekly().Pricing;
            Assert.Equal(875m / 6m, p.PerVisitAllocation);
            Assert.NotEqual(145.83m, p.PerVisitAllocation);
            Assert.StartsWith("145.8333333", p.PerVisitAllocation.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        [Fact]
        public void TheCapsAreBuiltOnTheUnroundedAllocation()
        {
            var p = Weekly().Pricing;

            Assert.Equal(Math.Round(875m / 6m * 0.5m, 2, MidpointRounding.AwayFromZero), p.CancellationAmount);
            Assert.Equal(72.92m, p.CancellationAmount);
            Assert.Equal(145.83m, p.LockoutFee);
            // The one that proves it: 145.8333... x 3 = 437.50, whereas 145.83 x 3 = 437.49.
            Assert.Equal(437.50m, p.LiabilityCapAmount);
        }

        [Theory]
        [InlineData(875, 5, 175.00, 87.50, 525.00)]
        [InlineData(1000, 3, 333.33, 166.67, 1000.00)]
        public void OtherWeeklySchedulesAllocateTheSameWay(
            decimal fee, int visits, decimal failedAccess, decimal cancellation, decimal liability)
        {
            var p = Weekly(fee, visits).Pricing;
            Assert.Equal(failedAccess, p.LockoutFee);
            Assert.Equal(cancellation, p.CancellationAmount);
            Assert.Equal(liability, p.LiabilityCapAmount);
        }

        // ── 3, 16, 17: the visit count is authoritative and is never overwritten ─────────────

        [Fact]
        public void SixVisitsPerWeekStaysSixWhateverTheBillingCadence()
        {
            var snapshot = Weekly();
            var text = ContractRenderer.Render(snapshot).PlainText;

            Assert.Contains("Six (6) scheduled cleaning visits per calendar week", text);
            Assert.DoesNotContain("One (1) scheduled cleaning visit", text);
            Assert.Equal(6, snapshot.Schedule.VisitsPerPeriod);
        }

        [Fact]
        public void AMismatchBetweenVisitCountAndDaysWarnsAndChangesNothing()
        {
            var schedule = Weekly(visits: 1).Schedule;   // DCC-2026-12918497 as first drafted

            var warning = ContractService.DescribeScheduleMismatch(schedule);
            Assert.NotNull(warning);
            Assert.Contains("1 visit per calendar week", warning);
            Assert.Contains("6 regular service days", warning);
            Assert.Equal(1, schedule.VisitsPerPeriod);
            Assert.Equal(6, schedule.ServiceDays.Count);

            schedule.VisitsPerPeriod = 6;
            Assert.Null(ContractService.DescribeScheduleMismatch(schedule));

            schedule.ServiceDays.Remove("Sunday");                   // 6 visits, 5 days
            Assert.Contains("6 visits per calendar week", ContractService.DescribeScheduleMismatch(schedule));
        }

        // ── 7, 8: the document ───────────────────────────────────────────────────────────────

        [Fact]
        public void TheContractAndPdfStateTheWeeklyPrice()
        {
            var (rendered, pdf) = Generate(Weekly());
            var text = rendered.PlainText;
            var squashedPdf = Squash(pdf);

            foreach (var needle in new[]
            {
                "The recurring service fee is $875.00 before tax per calendar week for the agreed recurring service schedule of six (6) scheduled cleaning visits per calendar week.",
                "Exhibit B shows the weekly calculation at 8.875% sales tax: $875.00 before tax, $77.66 sales tax and $952.66 total per calendar week.",
                "Six (6) scheduled cleaning visits per calendar week"
            })
            {
                Assert.Contains(needle, text);
                Assert.Contains(Squash(needle), squashedPdf);
            }

            Assert.Contains(rendered.Blocks, b => b.Kind == ContractBlockKind.ExhibitRow
                && b.Label == "Weekly pre-tax service fee" && b.Text.StartsWith("$875.00 per calendar week"));
            Assert.Contains(rendered.Blocks, b => b.Kind == ContractBlockKind.ExhibitRow
                && b.Label == "Sales tax at 8.875%" && b.Text.StartsWith("$77.66 per weekly billing period"));
            Assert.Contains(rendered.Blocks, b => b.Kind == ContractBlockKind.ExhibitRow
                && b.Label == "Weekly total at that tax rate" && b.Text.StartsWith("$952.66 per calendar week"));
            Assert.Contains(rendered.Blocks, b => b.Kind == ContractBlockKind.ExhibitRow
                && b.Label == "Billing cadence" && b.Text == "Invoices are issued weekly.");
            Assert.Contains(Squash("Weekly pre-tax service fee"), squashedPdf);
            Assert.Empty(rendered.UnresolvedTokens);
        }

        [Fact]
        public void NoPerVisitPriceIsPresentedAsTheAgreedFee()
        {
            var (rendered, pdf) = Generate(Weekly());
            var squashedPdf = Squash(pdf);

            foreach (var forbidden in new[]
            {
                "per completed scheduled visit",
                "$145.83 per visit",
                "$145.83 before tax",
                "Recurring pre-tax fee",
                "One (1) scheduled cleaning visit"
            })
            {
                Assert.DoesNotContain(forbidden, rendered.PlainText);
                Assert.DoesNotContain(Squash(forbidden), squashedPdf);
            }

            // Where $145.83 does appear, it is the failed-access CAP, labelled as an allocation.
            var failed = rendered.Blocks.Single(b => b.Kind == ContractBlockKind.ExhibitRow && b.Label == "Failed access");
            Assert.StartsWith("Capped at one visit's allocated share of the weekly pre-tax fee, initially $145.83", failed.Text);
            Assert.Contains("this allocation is for calculation only and does not change the weekly fee", rendered.PlainText);
            Assert.Contains(rendered.Blocks, b => b.Label == "Aggregate liability cap" && b.Text.Contains("initially $437.50"));
            Assert.Contains(rendered.Blocks, b => b.Label == "Short-notice cancellation" && b.Text.Contains("initially $72.92"));
        }

        // ── 15, 4: invoicing bills the week, never six allocations ───────────────────────────

        [Fact]
        public void AWeeklyInvoiceBillsOneWeeklyFee()
        {
            var snapshot = Weekly();
            var invoice = new CommercialInvoice
            {
                ServiceDatesJson = System.Text.Json.JsonSerializer.Serialize(new[]
                {
                    new DateTime(2026, 10, 4), new DateTime(2026, 10, 5), new DateTime(2026, 10, 6),
                    new DateTime(2026, 10, 7), new DateTime(2026, 10, 8), new DateTime(2026, 10, 9)
                })
            };
            var result = new CreateNextInvoiceResultDto();

            RecurringInvoiceService.ApplyContractPricing(snapshot, new BillingSettings(), invoice, result);
            InvoiceService.RecomputeTotalsFromRows(invoice);

            var line = Assert.Single(invoice.Items);
            Assert.Equal(1m, line.Quantity);
            Assert.Equal(875.00m, line.UnitPrice);
            Assert.StartsWith("Weekly commercial cleaning service fee - 6 scheduled visits per week", line.Description);
            Assert.Equal(875.00m, invoice.SubTotal);
            Assert.Equal(77.66m, invoice.TaxAmount);
            Assert.Equal(952.66m, invoice.Total);
            Assert.Equal(InvoiceTaxType.Added, invoice.TaxType);
            Assert.Empty(result.Warnings);
        }

        [Fact]
        public void APerVisitInvoiceStillBillsVisits()
        {
            var snapshot = Weekly(fee: 150m, visits: 3, basis: ContractPricingBasis.PerVisit);
            var invoice = new CommercialInvoice
            {
                ServiceDatesJson = System.Text.Json.JsonSerializer.Serialize(new[]
                    { new DateTime(2026, 10, 5), new DateTime(2026, 10, 7), new DateTime(2026, 10, 9) })
            };

            RecurringInvoiceService.ApplyContractPricing(snapshot, new BillingSettings(), invoice, new CreateNextInvoiceResultDto());
            InvoiceService.RecomputeTotalsFromRows(invoice);

            var line = Assert.Single(invoice.Items);
            Assert.Equal(3m, line.Quantity);
            Assert.Equal(150.00m, line.UnitPrice);
            Assert.Equal(450.00m, invoice.SubTotal);
        }

        [Fact]
        public void AShortWeekStillBillsTheFlatFeeAndSaysSo()
        {
            var invoice = new CommercialInvoice
            {
                ServiceDatesJson = System.Text.Json.JsonSerializer.Serialize(new[]
                    { new DateTime(2026, 10, 8), new DateTime(2026, 10, 9) })
            };
            var result = new CreateNextInvoiceResultDto();

            RecurringInvoiceService.ApplyContractPricing(Weekly(), new BillingSettings(), invoice, result);
            InvoiceService.RecomputeTotalsFromRows(invoice);

            Assert.Equal(875.00m, invoice.SubTotal);
            Assert.Contains(result.Warnings, w => w.Contains("flat weekly fee") && w.Contains("2 scheduled visit"));
        }

        // ── compatibility: weekly fee needs a weekly schedule and weekly invoicing ────────────

        [Theory]
        [InlineData("calendar week", ContractBillingFrequency.Weekly, true)]
        [InlineData("calendar month", ContractBillingFrequency.Weekly, false)]
        [InlineData("calendar week", ContractBillingFrequency.Monthly, false)]
        [InlineData("calendar week", ContractBillingFrequency.PerServiceVisit, false)]
        public void AWeeklyFeeNeedsAWeeklyScheduleAndWeeklyInvoices(
            string unit, ContractBillingFrequency billing, bool allowed)
        {
            var reason = ContractPricingCalculator.IncompatibilityReason(
                new PricingSnapshot { PricingBasis = ContractPricingBasis.WeeklyFlatFee },
                new ScheduleSnapshot { FrequencyUnit = unit, VisitsPerPeriod = 6 },
                new BillingCadenceSnapshot { Frequency = billing });

            Assert.Equal(allowed, reason == null);
            Assert.Null(ContractPricingCalculator.IncompatibilityReason(
                new PricingSnapshot { PricingBasis = ContractPricingBasis.PerVisit },
                new ScheduleSnapshot { FrequencyUnit = unit },
                new BillingCadenceSnapshot { Frequency = billing }));
        }

        // ── 13: executed documents do not move ───────────────────────────────────────────────

        [Fact]
        public void AFrozenPerVisitVersionRendersAsSigned()
        {
            var snapshot = Weekly(fee: 145.83m, basis: ContractPricingBasis.PerVisit);
            snapshot.TemplateBodyText =
                "(a) The recurring service fee is {{PRE_TAX_PRICE}} before tax per completed scheduled visit.\n"
                + "|Aggregate liability cap|{{LIABILITY_CAP_MULTIPLE}} times, initially {{LIABILITY_CAP_AMOUNT}}.\n";
            var before = ContractRenderer.Render(snapshot);

            Assert.Contains("$145.83 before tax per completed scheduled visit.", before.PlainText);
            Assert.Contains("initially $437.49.", before.PlainText);
            Assert.Equal(before.Sha256, ContractRenderer.Render(ContractSnapshot.Parse(snapshot.ToJson())).Sha256);
        }
    }
}
