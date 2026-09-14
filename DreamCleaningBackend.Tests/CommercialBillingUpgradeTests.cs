using System;
using System.Collections.Generic;
using System.Linq;
using DreamCleaningBackend.DTOs.Commercial;
using DreamCleaningBackend.Helpers.Commercial;
using DreamCleaningBackend.Helpers.Contracts;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Models.Commercial;
using DreamCleaningBackend.Models.Contracts;
using DreamCleaningBackend.Services.Commercial;
using DreamCleaningBackend.Services.Contracts;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// THE 2026-09 COMMERCIAL BILLING UPGRADE, pinned rule by rule.
    ///
    /// Everything asserted here is pure: a calculator, a formatter, a mapper or a set of defaults.
    /// That is deliberate — each of these decisions is one somebody could plausibly "simplify"
    /// later, and a pure test says exactly what would break.
    ///
    /// The reference figures are the ones the rest of the commercial module already uses: $849.99
    /// pre-tax at 8.875% is $75.44 tax and $925.43 total. An invoice, a contract and an ACH debit
    /// raised against that agreement all have to reconcile against those numbers.
    /// </summary>
    public class CommercialBillingUpgradeTests
    {
        // ══════════════════════════════════════════════════════════════════════════════════════
        //  1. Tax-inclusive invoices state a REAL tax amount, and the total never moves
        // ══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// The headline promise of tax-inclusive mode: the amount typed IS the amount charged.
        ///
        /// Nothing is added on top, and the split is taken by SUBTRACTION so subtotal + tax equals
        /// the total to the cent. Deriving the tax as round2(preTax x rate) instead drifts a cent
        /// on amounts where no cent-valued subtotal satisfies the equation, and the invoice would
        /// then print three figures that do not add up.
        /// </summary>
        [Theory]
        [InlineData(925.43)]
        [InlineData(300.00)]
        [InlineData(1000.00)]
        [InlineData(0.01)]
        [InlineData(12345.67)]
        public void TaxInclusive_SplitsExactly_AndLeavesTheTotalUntouched(double amount)
        {
            var typed = (decimal)amount;

            var totals = InvoiceCalculator.Calculate(new InvoiceTotalsInput
            {
                Lines = new List<InvoiceLineInput> { new() { Quantity = 1m, UnitPrice = typed } },
                TaxType = InvoiceTaxType.Included,
                TaxRate = 8.875m
            });

            Assert.Equal(typed, totals.Total);
            Assert.Equal(totals.Total, totals.SubTotal - totals.DiscountAmount);

            // The property that actually matters on the printed invoice.
            var taxExclusiveSubtotal = totals.Total - totals.TaxAmount;
            Assert.Equal(totals.Total, taxExclusiveSubtotal + totals.TaxAmount);
        }

        /// <summary>
        /// The specific figures from the reference agreement, so the contract and its invoice
        /// cannot quote different money for the same visit.
        /// </summary>
        [Fact]
        public void TaxInclusive_ReferenceInvoice_SplitsTo849And7544()
        {
            var totals = InvoiceCalculator.Calculate(new InvoiceTotalsInput
            {
                Lines = new List<InvoiceLineInput> { new() { Quantity = 1m, UnitPrice = 925.43m } },
                TaxType = InvoiceTaxType.Included,
                TaxRate = 8.875m
            });

            Assert.Equal(925.43m, totals.Total);
            Assert.Equal(75.44m, totals.TaxAmount);
            Assert.Equal(849.99m, totals.Total - totals.TaxAmount);
        }

        /// <summary>
        /// The tax is a NUMBER on every surface, including tax-inclusive - which used to print the
        /// bare word "Included" and therefore stated the tax nowhere at all.
        ///
        /// The "(included)" suffix keeps the one thing the old wording got right: the figure is
        /// already inside the total rather than added to it.
        /// </summary>
        [Fact]
        public void TaxValue_ShowsTheAmount_EvenWhenIncluded()
        {
            var inclusive = new PublicInvoiceDto
            {
                TaxType = InvoiceTaxType.Included, TaxRate = 8.875m, TaxAmount = 75.44m
            };

            var value = InvoicePdfService.TaxValue(inclusive);

            Assert.Contains("75.44", value);
            Assert.Contains("included", value, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual("Included", value);

            // The rate is stated in the LABEL, inclusive or added — a client checking a sales-tax
            // return needs both the rate and the dollars.
            Assert.Contains("8.875", InvoicePdfService.TaxLabel(inclusive));
        }

        [Fact]
        public void TaxValue_StillSaysExempt_WhenThereIsNoTax()
        {
            var exempt = new PublicInvoiceDto { TaxType = InvoiceTaxType.Exempt };
            Assert.Equal("Exempt", InvoicePdfService.TaxValue(exempt));
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  2. The ACH processing fee
        // ══════════════════════════════════════════════════════════════════════════════════════

        private static BillingSettings FeeSettings(
            bool enabled = true, decimal rate = 0.8m, decimal cap = 5.00m) => new()
        {
            AchCustomerFeeEnabled = enabled,
            AchCustomerFeeRatePercent = rate,
            AchCustomerFeeCapAmount = cap
        };

        /// <summary>
        /// The worked example from the specification: a $925.43 balance is 0.8% = $7.40, which is
        /// above the cap, so the customer pays $5.00 and the bank is debited $930.43.
        /// </summary>
        [Fact]
        public void AchFee_ReferenceInvoice_IsCappedAtFiveDollars()
        {
            var fee = AchProcessingFeeCalculator.Resolve(925.43m, FeeSettings());

            Assert.Equal(5.00m, fee);
            Assert.Equal(930.43m, AchProcessingFeeCalculator.ResolveTotalCharge(925.43m, fee));
        }

        /// <summary>
        /// Below the cap the fee is the real percentage, to the cent. $300.00 x 0.8% = $2.40.
        /// A flat $5.00 on every payment would over-charge every small invoice.
        /// </summary>
        [Theory]
        [InlineData(300.00, 2.40)]
        [InlineData(100.00, 0.80)]
        [InlineData(625.00, 5.00)]   // exactly at the cap
        [InlineData(624.00, 4.99)]   // one dollar under it: still the percentage
        [InlineData(10.00, 0.08)]
        public void AchFee_UsesThePercentage_UntilTheCapIsReached(double balance, double expected)
        {
            var fee = AchProcessingFeeCalculator.Resolve((decimal)balance, FeeSettings());
            Assert.Equal((decimal)expected, fee);
        }

        [Fact]
        public void AchFee_IsZero_WhenDisabled()
        {
            Assert.Equal(0m, AchProcessingFeeCalculator.Resolve(925.43m, FeeSettings(enabled: false)));
        }

        [Fact]
        public void AchFee_IsZero_ForANonPositiveBalance()
        {
            Assert.Equal(0m, AchProcessingFeeCalculator.Resolve(0m, FeeSettings()));
            Assert.Equal(0m, AchProcessingFeeCalculator.Resolve(-10m, FeeSettings()));
        }

        /// <summary>
        /// CARD PAYMENTS CARRY NO FEE. The owner's decision was to price ACH; silently extending
        /// a surcharge to cards would be a commercial change nobody made.
        /// </summary>
        [Fact]
        public void AchFee_AppliesToBankDebitsOnly()
        {
            var settings = FeeSettings();

            Assert.Equal(5.00m, AchProcessingFeeCalculator.Resolve(
                925.43m, settings, InvoicePaymentRecordMethod.AchBankTransfer));

            Assert.Equal(0m, AchProcessingFeeCalculator.Resolve(
                925.43m, settings, InvoicePaymentRecordMethod.Card));
        }

        /// <summary>
        /// A zero cap means NO CAP, not "no fee" — switching the fee off is what the enabled flag
        /// is for, and a zero cap silently disabling it would be a setting nobody could find.
        /// </summary>
        [Fact]
        public void AchFee_ZeroCapMeansUncapped()
        {
            var fee = AchProcessingFeeCalculator.Resolve(10_000m, FeeSettings(cap: 0m));
            Assert.Equal(80.00m, fee);
        }

        /// <summary>
        /// THE FEE IS NEVER APPLIED TO THE INVOICE. Settling a $925.43 invoice with a $930.43
        /// debit must credit $925.43 and record $5.00 separately: crediting the gross would make
        /// every online payment look overpaid, and dropping the fee would leave the customer's
        /// money unaccounted for.
        /// </summary>
        [Fact]
        public void AchFee_IsRecordedBesideTheAmount_NotInsideIt()
        {
            const decimal balance = 925.43m;
            var fee = AchProcessingFeeCalculator.Resolve(balance, FeeSettings());
            var charged = AchProcessingFeeCalculator.ResolveTotalCharge(balance, fee);

            // What the settlement handler does with the gross Stripe amount.
            var appliedToInvoice = InvoiceCalculator.Round2(charged - fee);

            Assert.Equal(930.43m, charged);
            Assert.Equal(925.43m, appliedToInvoice);
            Assert.Equal(0m, InvoiceCalculator.ResolveBalance(balance, appliedToInvoice));
            Assert.Equal(0m, InvoiceCalculator.ResolveOverpayment(balance, appliedToInvoice));
        }

        /// <summary>
        /// The customer-facing wording. "Fee" alone reads as a hidden markup, and "No fee" on the
        /// manual route would be a promise about somebody else's bank.
        /// </summary>
        [Fact]
        public void AchFee_LabelsSayWhatTheChargeIs()
        {
            Assert.Equal("ACH Processing Fee", AchProcessingFeeCalculator.CustomerFacingLabel);
            Assert.Contains("Dream Cleaning", AchProcessingFeeCalculator.ManualAchNoFeeNote);
            Assert.DoesNotContain("No Fee", AchProcessingFeeCalculator.ManualAchNoFeeNote,
                StringComparison.OrdinalIgnoreCase);
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  3. Service dates: one date, several dates, a period — or nothing
        // ══════════════════════════════════════════════════════════════════════════════════════

        [Fact]
        public void ServiceDates_OneCleaning_IsADate_NotARange()
        {
            var display = ServiceDateFormatter.Describe(
                new[] { new DateTime(2026, 9, 7) }, null, null);

            Assert.Equal(ServiceDateFormatter.SingleLabel, display.Label);
            Assert.Equal("September 7, 2026", display.Text);
            Assert.DoesNotContain("-", display.Text);
        }

        [Fact]
        public void ServiceDates_AStartEqualToTheEnd_IsAlsoJustADate()
        {
            var day = new DateTime(2026, 9, 7);
            var display = ServiceDateFormatter.Describe(null, day, day);

            Assert.Equal(ServiceDateFormatter.SingleLabel, display.Label);
            Assert.Equal("September 7, 2026", display.Text);
        }

        /// <summary>
        /// Four Wednesdays are LISTED, not collapsed into a month. The client can check a list of
        /// dates against their own diary; they cannot check "October 1-31" against anything.
        /// </summary>
        [Fact]
        public void ServiceDates_SeveralVisits_AreListed()
        {
            var display = ServiceDateFormatter.Describe(new[]
            {
                new DateTime(2026, 10, 7), new DateTime(2026, 10, 14),
                new DateTime(2026, 10, 21), new DateTime(2026, 10, 28)
            }, null, null);

            Assert.Equal(ServiceDateFormatter.ListLabel, display.Label);
            Assert.Equal("October 7, 14, 21, 28, 2026", display.Text);
        }

        [Fact]
        public void ServiceDates_ARangeWithinOneMonth_ReadsAsAPeriod()
        {
            var display = ServiceDateFormatter.Describe(
                null, new DateTime(2026, 10, 1), new DateTime(2026, 10, 31));

            Assert.Equal(ServiceDateFormatter.PeriodLabel, display.Label);
            Assert.Equal("October 1-31, 2026", display.Text);
        }

        /// <summary>
        /// AN INVOICE WITH NOTHING RECORDED SAYS NOTHING. The failure this whole area exists to
        /// prevent is a plausible-looking range no schedule supports — "September 8-12" for a
        /// single Monday cleaning. A blank is honest; an invented period looks authoritative.
        /// </summary>
        [Fact]
        public void ServiceDates_NothingRecorded_RendersNothing()
        {
            var display = ServiceDateFormatter.Describe(null, null, null);

            Assert.False(display.HasValue);
            Assert.Equal(string.Empty, display.Text);
            Assert.Equal(string.Empty, display.Label);
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  4. Recurring billing: which cleanings the next invoice covers
        // ══════════════════════════════════════════════════════════════════════════════════════

        private static ServiceScheduleInput Schedule(
            ContractBillingFrequency frequency,
            IEnumerable<DayOfWeek>? days = null,
            string unit = "calendar week",
            int visits = 1,
            int interval = 1,
            DateTime? previousPeriodEnd = null,
            DateTime? previousFirstService = null,
            DateTime? anchor = null,
            int deadlineHours = 48) => new()
        {
            ServiceDays = days?.ToList() ?? new List<DayOfWeek>(),
            FrequencyUnit = unit,
            VisitsPerPeriod = visits,
            BillingFrequency = frequency,
            BillingIntervalCount = interval,
            PreviousPeriodEnd = previousPeriodEnd,
            PreviousFirstServiceDate = previousFirstService,
            AnchorDate = anchor,
            Today = new DateTime(2026, 9, 8),
            PaymentDeadlineHours = deadlineHours
        };

        /// <summary>
        /// THE MONTHLY EXAMPLE FROM THE SPECIFICATION.
        ///
        /// Previous service September 7, due September 5 → next service October 7, due October 5.
        /// A monthly single-visit contract is scheduled by DAY OF THE MONTH, so advancing it by
        /// weekday would be wrong: September 7 2026 is a Monday and October 7 is a Wednesday.
        /// </summary>
        [Fact]
        public void NextInvoice_Monthly_AdvancesTheDayOfTheMonth()
        {
            var result = ServiceScheduleCalculator.Next(Schedule(
                ContractBillingFrequency.Monthly,
                days: new[] { DayOfWeek.Monday },
                unit: "calendar month",
                previousFirstService: new DateTime(2026, 9, 7)));

            Assert.True(result.HasSchedule);
            Assert.Equal(new DateTime(2026, 10, 7), Assert.Single(result.ServiceDates));
            Assert.Equal(new DateTime(2026, 10, 5), result.DueDate);
        }

        /// <summary>
        /// THE WEEKLY EXAMPLE. Every Wednesday, previous service September 9 → next September 16,
        /// due September 14 at 48 hours before.
        /// </summary>
        [Fact]
        public void NextInvoice_Weekly_AdvancesToTheNextServiceDay()
        {
            var result = ServiceScheduleCalculator.Next(Schedule(
                ContractBillingFrequency.Weekly,
                days: new[] { DayOfWeek.Wednesday },
                previousPeriodEnd: new DateTime(2026, 9, 9)));

            Assert.True(result.HasSchedule);
            Assert.Equal(new DateTime(2026, 9, 16), Assert.Single(result.ServiceDates));
            Assert.Equal(new DateTime(2026, 9, 14), result.DueDate);
        }

        /// <summary>
        /// THREE VISITS A WEEK, BILLED WEEKLY. The generated invoice names the next Monday,
        /// Wednesday and Friday — not one day, and not a bare range.
        /// </summary>
        [Fact]
        public void NextInvoice_ThreeDaysAWeek_ListsAllThree()
        {
            var result = ServiceScheduleCalculator.Next(Schedule(
                ContractBillingFrequency.Weekly,
                days: new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday },
                visits: 3,
                previousPeriodEnd: new DateTime(2026, 9, 13)));   // a Sunday

            Assert.True(result.HasSchedule);
            Assert.Equal(3, result.ServiceDates.Count);
            Assert.Equal(new DateTime(2026, 9, 14), result.ServiceDates[0]);   // Monday
            Assert.Equal(new DateTime(2026, 9, 16), result.ServiceDates[1]);   // Wednesday
            Assert.Equal(new DateTime(2026, 9, 18), result.ServiceDates[2]);   // Friday

            // The due date follows the FIRST visit of the period, because Exhibit B promises
            // payment ahead of service.
            Assert.Equal(new DateTime(2026, 9, 12), result.DueDate);
        }

        /// <summary>
        /// WEEKLY CLEANING, MONTHLY BILLING. The invoice covers a calendar month and names every
        /// Wednesday inside it — this is the case where treating the two schedules as one produces
        /// an invoice for a quarter of the work.
        /// </summary>
        [Fact]
        public void NextInvoice_WeeklyServiceBilledMonthly_CoversEveryVisitInThePeriod()
        {
            var result = ServiceScheduleCalculator.Next(Schedule(
                ContractBillingFrequency.Monthly,
                days: new[] { DayOfWeek.Wednesday },
                previousPeriodEnd: new DateTime(2026, 9, 30)));

            Assert.True(result.HasSchedule);
            Assert.Equal(new DateTime(2026, 10, 1), result.PeriodStart);
            Assert.Equal(new DateTime(2026, 10, 31), result.PeriodEnd);

            Assert.Equal(new[]
            {
                new DateTime(2026, 10, 7), new DateTime(2026, 10, 14),
                new DateTime(2026, 10, 21), new DateTime(2026, 10, 28)
            }, result.ServiceDates);
        }

        /// <summary>Biweekly is Weekly with an interval of two — one code path, not a family.</summary>
        [Fact]
        public void NextInvoice_EveryTwoWeeks_SpansAFortnight()
        {
            var result = ServiceScheduleCalculator.Next(Schedule(
                ContractBillingFrequency.Weekly,
                days: new[] { DayOfWeek.Wednesday },
                interval: 2,
                previousPeriodEnd: new DateTime(2026, 9, 13)));

            Assert.True(result.HasSchedule);
            Assert.Equal(new DateTime(2026, 9, 14), result.PeriodStart);
            Assert.Equal(new DateTime(2026, 9, 27), result.PeriodEnd);
            Assert.Equal(2, result.ServiceDates.Count);
        }

        /// <summary>Per-visit billing invoices ONE cleaning; the period collapses onto that date.</summary>
        [Fact]
        public void NextInvoice_PerVisit_BillsASingleCleaning()
        {
            var result = ServiceScheduleCalculator.Next(Schedule(
                ContractBillingFrequency.PerServiceVisit,
                days: new[] { DayOfWeek.Monday, DayOfWeek.Friday },
                previousPeriodEnd: new DateTime(2026, 9, 8)));

            Assert.True(result.HasSchedule);
            var only = Assert.Single(result.ServiceDates);
            Assert.Equal(new DateTime(2026, 9, 11), only);          // the next Friday
            Assert.Equal(only, result.PeriodStart);
            Assert.Equal(only, result.PeriodEnd);
        }

        /// <summary>
        /// NOT ENOUGH TO GO ON MEANS NOTHING IS INVENTED. The caller leaves the invoice's dates
        /// blank and tells the admin why — which is the whole point of producing a draft.
        /// </summary>
        [Fact]
        public void NextInvoice_WithNoScheduleAtAll_RefusesToGuess()
        {
            var result = ServiceScheduleCalculator.Next(Schedule(
                ContractBillingFrequency.Monthly,
                unit: "calendar month",
                previousFirstService: null,
                anchor: null));

            Assert.False(result.HasSchedule);
            Assert.Empty(result.ServiceDates);
            Assert.False(string.IsNullOrWhiteSpace(result.Reason));
        }

        [Fact]
        public void NextInvoice_WeekdayDriven_WithNoDaysRecorded_RefusesToGuess()
        {
            var result = ServiceScheduleCalculator.Next(Schedule(
                ContractBillingFrequency.Weekly,
                days: Array.Empty<DayOfWeek>(),
                previousPeriodEnd: new DateTime(2026, 9, 9)));

            Assert.False(result.HasSchedule);
            Assert.Contains("service days", result.Reason, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The 31st of January bills on the 28th of February, not the 3rd of March.</summary>
        [Fact]
        public void NextInvoice_MonthEnd_IsClampedRatherThanOverflowing()
        {
            Assert.Equal(new DateTime(2027, 2, 28),
                ServiceScheduleCalculator.AddMonthsClamped(new DateTime(2027, 1, 31), 1));
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  5. Contract defaults
        // ══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// A NEW contract is committed for six months, then month-to-month on sixty days notice,
        /// under New York law in Kings County.
        ///
        /// Defaults only — every generated version freezes its own copy, so changing these can
        /// never move a contract that already exists.
        /// </summary>
        [Fact]
        public void NewContract_TermDefaults_AreSixSixtyAndMonthToMonth()
        {
            var term = new TermSnapshot();

            Assert.Equal(6, term.InitialTermMonths);
            Assert.Equal(6, term.MinimumCommitmentMonths);
            Assert.Equal(60, term.TerminationNoticeDays);
            Assert.Equal("month-to-month", term.RenewalType);
            Assert.Equal("New York", term.GoverningLawState);
            Assert.Equal("Kings County", term.VenueCounty);
        }

        /// <summary>
        /// Tax-inclusive at 8.875%, and NO returned-payment fee.
        ///
        /// The $35 fee is retired: it passed a processor's own failed-debit cost to the client as
        /// a flat contractual charge. The FIELD survives so historical contracts keep rendering
        /// what they agreed to.
        /// </summary>
        [Fact]
        public void NewContract_PricingDefaults_AreTaxInclusiveWithNoFailedPaymentFee()
        {
            var pricing = new PricingSnapshot();

            Assert.Equal(ContractPriceMode.TaxInclusive, pricing.PriceMode);
            Assert.Equal(8.875m, pricing.SalesTaxRatePercent);
            Assert.Equal(0m, pricing.ReturnedPaymentFee);
        }

        [Fact]
        public void NewInvoiceDefaults_AreTaxInclusiveAt8875()
        {
            var settings = new BillingSettings();

            Assert.Equal(InvoiceTaxType.Included, settings.DefaultTaxType);
            Assert.Equal(8.875m, settings.DefaultTaxRate);
            Assert.Equal(ContractPriceMode.TaxInclusive, settings.DefaultContractPriceMode);

            // And the ACH fee ships switched on at Stripe's own pricing.
            Assert.True(settings.AchCustomerFeeEnabled);
            Assert.Equal(0.8m, settings.AchCustomerFeeRatePercent);
            Assert.Equal(5.00m, settings.AchCustomerFeeCapAmount);
        }

        /// <summary>
        /// At zero the returned-payment CLAUSE disappears from the document rather than promising
        /// a $0.00 charge. The Exhibit B row survives and reads "no returned or failed payment
        /// fee", because it also carries the late charge and dropping the whole row would lose it.
        /// </summary>
        [Fact]
        public void RetiredFailedPaymentFee_DropsTheClause_NotTheExhibitRow()
        {
            var snapshot = ReferenceSnapshot();
            snapshot.Pricing.ReturnedPaymentFee = 0m;

            var tokens = ContractPlaceholders.Build(snapshot);

            Assert.Equal(ContractPlaceholders.OmitLineSentinel, tokens["RETURNED_PAYMENT_FEE_WORDS"]);
            Assert.Equal("no", tokens["RETURNED_PAYMENT_FEE"]);
        }

        /// <summary>
        /// A HISTORICAL CONTRACT IS UNTOUCHED. Its frozen snapshot still carries $35, so the
        /// executed document renders exactly the words that were signed.
        /// </summary>
        [Fact]
        public void HistoricalFailedPaymentFee_StillRendersWhatWasAgreed()
        {
            var snapshot = ReferenceSnapshot();
            snapshot.Pricing.ReturnedPaymentFee = 35m;

            var tokens = ContractPlaceholders.Build(snapshot);

            Assert.Contains("35.00", tokens["RETURNED_PAYMENT_FEE"]);
            Assert.Contains("thirty-five", tokens["RETURNED_PAYMENT_FEE_WORDS"]);
            Assert.DoesNotContain(ContractPlaceholders.OmitLineSentinel,
                tokens["RETURNED_PAYMENT_FEE_WORDS"]);
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  5b. The seeded agreement body is the attorney-drafted one
        // ══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// THE SEEDED BODY CARRIES THE v2.0 STRUCTURE.
        ///
        /// The earlier 1.0/1.1 bodies were the pre-review wording and are retired. This is the
        /// alarm for the failure mode that would otherwise be silent: a body that seeds, flags
        /// itself default, and quietly keeps issuing superseded language because somebody reverted
        /// a merge. It checks the landmarks that only exist in the drafted agreement.
        /// </summary>
        [Fact]
        public void SeededTemplate_IsTheAttorneyDraftedAgreement()
        {
            var body = ContractTemplateSeed.BodyText;

            Assert.Equal("2.1", ContractTemplateSeed.TemplateVersion);

            // Sections that only exist in the drafted version.
            Assert.Contains("## 18. PERSONNEL COORDINATION", body);
            Assert.Contains("## 11. INVOICING, ADVANCE PAYMENT AND CHARGES", body);
            Assert.Contains("### B4. AUTHORIZED REPRESENTATIVES AND CONTACTS", body);
            Assert.Contains("{{SCOPE_TABLE:area-tasks}}", body);

            // The non-solicit / non-hire clause is GONE, replaced by Section 18's express
            // statement that no such restriction is imposed. Reintroducing it would contradict
            // the section immediately around it.
            Assert.DoesNotContain("NON-SOLICITATION", body);
            Assert.DoesNotContain("{{NON_SOLICIT_MONTHS}}", body);
            Assert.DoesNotContain("{{NON_HIRE_DAMAGES}}", body);
            Assert.Contains("imposes no restriction or fee on lawful solicitation", body);

            // The liability cap is a multiple of the per-visit fee, not a lookback in months.
            Assert.Contains("{{LIABILITY_CAP_MULTIPLE}}", body);
            Assert.DoesNotContain("{{LIABILITY_CAP_MONTHS}}", body);
        }

        /// <summary>
        /// The retired versions are named, so the seeder can take them out of the picker.
        /// Selecting superseded legal text is not a choice anybody should be offered.
        /// </summary>
        [Fact]
        public void SupersededVersions_AreNamedSoTheSeederCanRetireThem()
        {
            Assert.Contains("1.0", ContractTemplateSeed.SupersededVersions);
            Assert.Contains("1.1", ContractTemplateSeed.SupersededVersions);

            // 2.0 is the soap draft. It seeded into real databases before the owner's rule was
            // applied, so retiring it is what takes the hand-soap wording out of the picker - the
            // seeder cannot correct that row in place, it can only stop offering it.
            Assert.Contains("2.0", ContractTemplateSeed.SupersededVersions);
            Assert.DoesNotContain(
                ContractTemplateSeed.TemplateVersion, ContractTemplateSeed.SupersededVersions);
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  6. Multiple regular service days
        // ══════════════════════════════════════════════════════════════════════════════════════

        [Fact]
        public void ServiceDays_ThreeVisitsAWeek_RenderAsAList()
        {
            var snapshot = ReferenceSnapshot();
            snapshot.Schedule.VisitsPerPeriod = 3;
            snapshot.Schedule.ServiceDays = new List<string> { "Monday", "Wednesday", "Friday" };

            var tokens = ContractPlaceholders.Build(snapshot);

            Assert.Equal("Monday, Wednesday and Friday", tokens["SERVICE_DAYS"]);
            // The alias keeps a template body written before multiple days existed rendering the
            // right weekdays - including one a SuperAdmin has since edited.
            Assert.Equal("Monday, Wednesday and Friday", tokens["SERVICE_DAY"]);

            // The surrounding sentence has to agree in NUMBER with the list above it.
            Assert.Equal("days", tokens["SERVICE_DAY_NOUN"]);
            Assert.Equal("are", tokens["SERVICE_DAY_VERB"]);
            Assert.Contains("Those days", tokens["SERVICE_DAY_FIXED_TEXT"]);
        }

        [Fact]
        public void ServiceDays_OneVisit_StaysSingular()
        {
            var snapshot = ReferenceSnapshot();
            snapshot.Schedule.ServiceDays = new List<string> { "Sunday" };

            var tokens = ContractPlaceholders.Build(snapshot);

            Assert.Equal("Sunday", tokens["SERVICE_DAYS"]);
            Assert.Equal("day", tokens["SERVICE_DAY_NOUN"]);
            Assert.Equal("is", tokens["SERVICE_DAY_VERB"]);
            Assert.Contains("That day", tokens["SERVICE_DAY_FIXED_TEXT"]);
        }

        /// <summary>
        /// A PRE-2026-09 SNAPSHOT HAS AN EMPTY LIST AND ITS DAY IN THE LEGACY COLUMN. Reading the
        /// list without the fallback would blank the service day on every contract signed before
        /// multiple days existed.
        /// </summary>
        [Fact]
        public void ServiceDays_LegacySnapshot_FallsBackToTheSingleDay()
        {
            var schedule = new ScheduleSnapshot { ServiceDay = "Sunday", ServiceDays = new List<string>() };

            Assert.Equal(new[] { DayOfWeek.Sunday }, schedule.ResolveServiceDays());
            Assert.Equal(new[] { "Sunday" }, schedule.ResolveServiceDayNames());
        }

        /// <summary>Monday-first, so a day picker's click order never reaches the agreement.</summary>
        [Fact]
        public void ServiceDays_AreOrderedMondayFirst()
        {
            var schedule = new ScheduleSnapshot
            {
                ServiceDays = new List<string> { "Friday", "Sunday", "Monday", "Wednesday" }
            };

            Assert.Equal(
                new[] { "Monday", "Wednesday", "Friday", "Sunday" },
                schedule.ResolveServiceDayNames());
        }

        [Fact]
        public void BillingCadence_IsStatedInWords_NotAsEveryOneWeeks()
        {
            var snapshot = ReferenceSnapshot();

            snapshot.Billing = new BillingCadenceSnapshot
            { Frequency = ContractBillingFrequency.Weekly, IntervalCount = 1 };
            Assert.Equal("weekly", ContractPlaceholders.Build(snapshot)["BILLING_CADENCE_TEXT"]);

            snapshot.Billing = new BillingCadenceSnapshot
            { Frequency = ContractBillingFrequency.Weekly, IntervalCount = 2 };
            Assert.Contains("two (2) weeks", ContractPlaceholders.Build(snapshot)["BILLING_CADENCE_TEXT"]);

            snapshot.Billing = new BillingCadenceSnapshot
            { Frequency = ContractBillingFrequency.PerServiceVisit, IntervalCount = 1 };
            Assert.Contains("each scheduled service visit",
                ContractPlaceholders.Build(snapshot)["BILLING_CADENCE_TEXT"]);
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  7. A business account seeds the CONTACT, never the legal entity name
        // ══════════════════════════════════════════════════════════════════════════════════════

        private static User BusinessUser() => new()
        {
            Id = 55,
            FirstName = "Nodar",
            LastName = "Alania",
            Email = "nodar@example.invalid",
            Phone = "7185550123",
            PasswordHash = "x",
            PasswordSalt = "x",
            Role = UserRole.Customer,
            IsBusiness = true
        };

        /// <summary>
        /// THE BUG THIS FIXES: a business-flagged account produced a commercial client whose LEGAL
        /// ENTITY NAME was the person's name, while the Primary billing contact sat empty.
        ///
        /// A company is not its owner. "Nodar Alania" is a person; the legal entity name is
        /// printed on a contract as a statement about a registered company, so a blank is a gap
        /// staff fill in and a person's name there is a false document that reads as correct.
        /// </summary>
        [Fact]
        public void BusinessAccount_LeavesTheLegalEntityNameEmpty()
        {
            var client = BusinessClientMapper.BuildFromAccount(BusinessUser(), null);

            Assert.Equal(string.Empty, client.LegalEntityName);
            Assert.DoesNotContain("Nodar", client.LegalEntityName);
            Assert.DoesNotContain("Alania", client.LegalEntityName);

            // EntityType is blank for the same reason: it is a legal characterisation, so a
            // default would be a statement nobody has checked.
            Assert.Equal(string.Empty, client.EntityType);
        }

        /// <summary>
        /// The account holder's identity goes where it belongs — the primary billing contact —
        /// with their email in BOTH the contact and the billing/notice slot.
        /// </summary>
        [Fact]
        public void BusinessAccount_SeedsThePrimaryBillingContact()
        {
            var user = BusinessUser();
            var contact = BusinessClientMapper.BuildBillingContactFromAccount(user, contractClientId: 7);

            Assert.Equal(7, contact.ContractClientId);
            Assert.Equal("Nodar", contact.FirstName);
            Assert.Equal("Alania", contact.LastName);
            Assert.Equal("nodar@example.invalid", contact.Email);
            Assert.Equal("7185550123", contact.Phone);
            Assert.True(contact.IsPrimaryBillingContact);
            Assert.Equal(user.Id, contact.UserId);

            // The billing/notice email on the CLIENT is the same address.
            var client = BusinessClientMapper.BuildFromAccount(user, null);
            Assert.Equal("nodar@example.invalid", client.NoticeEmail);
        }

        /// <summary>
        /// TITLE IS LEFT EMPTY. Ticking a business flag says nothing about whether this person is
        /// the Owner, a manager or an office administrator — and the title is printed under a name
        /// on a signature block, so assuming one puts an unverified claim of authority into a
        /// legal document.
        /// </summary>
        [Fact]
        public void BusinessAccount_DoesNotAssumeTheContactIsTheOwner()
        {
            var contact = BusinessClientMapper.BuildBillingContactFromAccount(BusinessUser(), 7);

            Assert.True(string.IsNullOrEmpty(contact.Title));
        }

        /// <summary>
        /// A no-email cash customer's placeholder address must never reach a billing field: an
        /// invoice addressed to it silently fails.
        /// </summary>
        [Fact]
        public void BusinessAccount_PlaceholderEmail_NeverBecomesABillingAddress()
        {
            var user = BusinessUser();
            user.Email = "cash-customer-55@no-email.invalid";

            Assert.Null(BusinessClientMapper.BuildFromAccount(user, null).NoticeEmail);
            Assert.Null(BusinessClientMapper.BuildBillingContactFromAccount(user, 7).Email);
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  8. Recording a payment
        // ══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// AN ALREADY-PAID INVOICE IS NOT PAYABLE AGAIN. Correcting a mistake on one is what the
        /// reversal endpoint is for; a second payment on top would look like an overpayment
        /// nobody made.
        /// </summary>
        [Fact]
        public void PaidInvoice_OffersNoRecordPaymentAction()
        {
            Assert.False(InvoiceStatusPolicy.CanRecordPayment(InvoiceStatus.Paid, balanceDue: 0m));
            Assert.False(InvoiceStatusPolicy.CanRecordPayment(InvoiceStatus.Sent, balanceDue: 0m));

            // Still offered while money is genuinely owed, whatever the status says.
            Assert.True(InvoiceStatusPolicy.CanRecordPayment(InvoiceStatus.Overdue, balanceDue: 100m));
            Assert.True(InvoiceStatusPolicy.CanRecordPayment(InvoiceStatus.PartiallyPaid, balanceDue: 25m));

            // Draft and Void are absorbing, balance or no balance.
            Assert.False(InvoiceStatusPolicy.CanRecordPayment(InvoiceStatus.Draft, balanceDue: 100m));
            Assert.False(InvoiceStatusPolicy.CanRecordPayment(InvoiceStatus.Void, balanceDue: 100m));
        }

        /// <summary>
        /// The manual "Mark as Paid" path writes a MANUAL payment, and the DTO carries the fields
        /// an auditable record needs. Structural, because "do not pretend a bank transfer came
        /// through Stripe" is a rule about a field nobody can see going wrong.
        /// </summary>
        [Fact]
        public void ManualPayment_CarriesEverythingAnAuditNeeds()
        {
            var dto = typeof(RecordInvoicePaymentDto);

            Assert.NotNull(dto.GetProperty(nameof(RecordInvoicePaymentDto.Amount)));
            Assert.NotNull(dto.GetProperty(nameof(RecordInvoicePaymentDto.PaymentDate)));
            Assert.NotNull(dto.GetProperty(nameof(RecordInvoicePaymentDto.PaymentMethod)));
            Assert.NotNull(dto.GetProperty(nameof(RecordInvoicePaymentDto.TransactionReference)));
            Assert.NotNull(dto.GetProperty(nameof(RecordInvoicePaymentDto.InternalNote)));
            Assert.NotNull(dto.GetProperty(nameof(RecordInvoicePaymentDto.AcknowledgeProcessingPayment)));

            // And the row it produces names the provider, so a manual payment can never be read as
            // a Stripe one - they reconcile against completely different records.
            Assert.Equal("Manual", InvoiceService.ProviderLabel(InvoicePaymentProvider.Manual));
            Assert.Contains("Stripe", InvoiceService.ProviderLabel(InvoicePaymentProvider.Stripe));
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  9. Company identity: the trading name leads
        // ══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// "DBA Dream Cleaning NYC" first, "Nodar Alania Inc." underneath.
        ///
        /// The customer booked Dream Cleaning NYC, will look for it on a bank statement, and has
        /// probably never seen the corporate name — leading with the entity made the invoice read
        /// as though it came from a stranger. Resolved on the DTO so the web page, the PDF and the
        /// email cannot each decide it differently.
        /// </summary>
        [Fact]
        public void CompanyHeader_LeadsWithTheTradingName()
        {
            var company = new PublicCompanyDto
            {
                LegalName = "Nodar Alania Inc.",
                DbaName = "Dream Cleaning NYC"
            };

            Assert.Equal("DBA Dream Cleaning NYC", company.PrimaryName);
            Assert.Equal("Nodar Alania Inc.", company.SecondaryName);
        }

        /// <summary>With no trading name the legal name leads and is not printed twice.</summary>
        [Fact]
        public void CompanyHeader_WithoutADba_ShowsTheLegalNameOnce()
        {
            var company = new PublicCompanyDto { LegalName = "Nodar Alania Inc." };

            Assert.Equal("Nodar Alania Inc.", company.PrimaryName);
            Assert.Null(company.SecondaryName);
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  10. Scope templates: archiving never touches a signed contract
        // ══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Archiving hides a category or an item from NEW contracts and leaves everything already
        /// built with them alone — which holds structurally, because a contract carries a deep
        /// copy rather than a reference.
        /// </summary>
        [Fact]
        public void ArchivedScopeRows_AreDroppedFromNewContractsOnly()
        {
            var master = new ScopeStructure
            {
                Groups = new List<ScopeGroup>
                {
                    new()
                    {
                        Key = "included-areas", Title = "Included Areas",
                        Items = new List<ScopeItem>
                        {
                            new() { Label = "Dining area", Selected = true },
                            new() { Label = "Ball pit", Selected = true, Archived = true }
                        }
                    },
                    new() { Key = "retired", Title = "Retired category", Archived = true }
                }
            };

            // What a new contract copies.
            var offered = master.WithoutArchived();
            var group = Assert.Single(offered.Groups);
            Assert.Equal("included-areas", group.Key);
            Assert.Equal("Dining area", Assert.Single(group.Items).Label);

            // The master row is untouched - WithoutArchived clones.
            Assert.Equal(2, master.Groups.Count);
            Assert.Equal(2, master.Groups[0].Items.Count);
        }

        /// <summary>
        /// A contract's OWN snapshot is never filtered. It is a frozen copy of what was agreed, so
        /// a row archived on the master afterwards still renders on the executed document.
        /// </summary>
        [Fact]
        public void SignedContractScope_IsNotFilteredByLaterArchiving()
        {
            var snapshot = ReferenceSnapshot();
            snapshot.Scope = new ScopeStructure
            {
                Groups = new List<ScopeGroup>
                {
                    new()
                    {
                        Key = "included-areas", Title = "Included Areas", Inline = true,
                        Items = new List<ScopeItem>
                        {
                            // Archived on the master AFTER this contract was signed.
                            new() { Label = "Ball pit", Selected = true, Archived = true }
                        }
                    }
                }
            };

            var rendered = ContractRenderer.Render(snapshot);

            Assert.Contains("Ball pit", rendered.PlainText);
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  Fixtures
        // ══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// A minimal but complete snapshot. Invented values throughout - no real client, address
        /// or bank detail appears in any test in this project, and none may be added.
        /// </summary>
        private static ContractSnapshot ReferenceSnapshot() => new()
        {
            ContractNumber = "DCC-2026-48392175",
            VersionNumber = 1,
            EffectiveDate = new DateTime(2026, 9, 1),
            TemplateBodyText = "## 1. SERVICES\n{{SCOPE:included-areas}}",
            PremisesType = "restaurant",
            Contractor = new ContractorSnapshot
            {
                LegalEntityName = "Test Contractor Inc.", Dba = "Test Cleaning",
                EntityType = "a New York corporation", Address = "1 Example St",
                City = "Brooklyn", State = "NY", Zip = "11214",
                NoticeEmail = "contractor@example.invalid"
            },
            Client = new ClientSnapshot
            {
                LegalEntityName = "Example Client LLC", EntityType = "a limited liability company",
                PrincipalAddress = "2 Example Ave", City = "Brooklyn", State = "NY", Zip = "11215"
            },
            ServiceLocation = new ServiceLocationSnapshot
            {
                Address = "3 Example Blvd", City = "Brooklyn", State = "NY", Zip = "11216"
            },
            Schedule = new ScheduleSnapshot(),
            Billing = new BillingCadenceSnapshot(),
            Term = new TermSnapshot(),
            Pricing = new PricingSnapshot { PriceInput = 925.43m },
            Advanced = new AdvancedTermsSnapshot(),
            Scope = new ScopeStructure()
        };
    }
}
