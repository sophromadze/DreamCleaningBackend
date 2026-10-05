using DreamCleaningBackend.Models.Contracts;

namespace DreamCleaningBackend.Services.Contracts
{
    /// <summary>
    /// The ONLY place a contract price is turned into the six figures the agreement quotes. The
    /// admin types exactly three things - mode, amount, tax rate - and everything else is derived
    /// here, server-side. A computed total arriving from a browser is always discarded and
    /// recomputed, the same guard the booking pricing already applies.
    ///
    /// Rounding is half-up (<see cref="MidpointRounding.AwayFromZero"/>), matching the rest of
    /// the money math in this codebase.
    ///
    /// THE CANCELLATION, LOCKOUT AND LIABILITY FIGURES ARE BUILT ON THE PRE-TAX FEE. That is the
    /// basis the drafted agreement states, and it is the one that survives being read out loud:
    /// tax attaches to a supply, and a visit that did not happen is not one.
    /// </summary>
    public static class ContractPricingCalculator
    {
        /// <summary>
        /// Recomputes every derived field on <paramref name="pricing"/> in place from
        /// PriceMode + PriceInput + SalesTaxRatePercent + CancellationPercent.
        /// </summary>
        public static void Recalculate(PricingSnapshot pricing, ScheduleSnapshot? schedule = null)
        {
            if (pricing == null) throw new ArgumentNullException(nameof(pricing));
            if (!Enum.IsDefined(pricing.PricingBasis)) pricing.PricingBasis = ContractPricingBasis.PerVisit;

            var rate = Math.Max(0m, pricing.SalesTaxRatePercent) / 100m;
            var input = Math.Max(0m, pricing.PriceInput);

            if (pricing.PriceMode == ContractPriceMode.TaxInclusive)
            {
                // The typed amount IS what the client pays. Split it so subtotal + tax add back
                // to it EXACTLY - re-deriving the tax as round2(preTax * rate) drifts a cent on
                // amounts where no cent-valued subtotal satisfies the equation.
                pricing.TotalPrice = Round2(input);
                pricing.PreTaxPrice = rate == 0m ? pricing.TotalPrice : Round2(pricing.TotalPrice / (1m + rate));
                pricing.SalesTaxAmount = pricing.TotalPrice - pricing.PreTaxPrice;
            }
            else
            {
                // The typed amount is the pre-tax fee; tax rides on top.
                pricing.PreTaxPrice = Round2(input);
                pricing.SalesTaxAmount = Round2(pricing.PreTaxPrice * rate);
                pricing.TotalPrice = pricing.PreTaxPrice + pricing.SalesTaxAmount;
            }

            // ── Everything below is built on the PRE-TAX fee, never the tax-inclusive total ──
            //
            // Section 15(b) caps a short-notice cancellation charge at "fifty percent of the
            // pre-tax visit fee", and Section 14(b) caps a failed-access charge at "that visit's
            // pre-tax service fee, plus any tax legally applicable to the charge". Both used to be
            // derived from TotalPrice here, which was wrong in the same way twice: sales tax is
            // charged on a taxable SUPPLY, and a visit nobody performed is not one. Taking half of
            // $925.43 instead of half of $849.99 bills the client $37.72 of tax that was never
            // owed and that Contractor would have no basis to remit.
            //
            // These are CAPS on reasonable documented net loss, not automatic charges - which is
            // the other reason they carry no tax of their own. Whatever tax the law puts on the
            // charge that is actually made is added to that charge when it is made.

            // ── One visit's pre-tax value, which every per-visit cap is built on ──
            //
            // Per visit, that IS the fee. With a weekly flat fee the agreed price is the WEEK
            // ($875), and a visit's share of it ($875 / 6 = 145.8333...) is an allocation used only
            // by the caps. It is kept at FULL precision and each cap is rounded once, at the end -
            // rounding the allocation to $145.83 first would put the liability cap a cent off
            // ($437.49 instead of $437.50) and make the caps depend on the order of operations.
            if (pricing.PricingBasis == ContractPricingBasis.WeeklyFlatFee)
            {
                pricing.ScheduledVisitsPerFeePeriod = Math.Max(1, schedule?.VisitsPerPeriod
                    ?? pricing.ScheduledVisitsPerFeePeriod);
                pricing.PerVisitAllocation = pricing.PreTaxPrice / pricing.ScheduledVisitsPerFeePeriod;
            }
            else
            {
                pricing.ScheduledVisitsPerFeePeriod = 1;
                pricing.PerVisitAllocation = pricing.PreTaxPrice;
            }
            var visitValue = pricing.PerVisitAllocation;

            // Section 15(b): cap on the short-notice cancellation charge.
            var cancelPct = Clamp(pricing.CancellationPercent, 0m, 100m);
            pricing.CancellationPercent = cancelPct;
            pricing.CancellationAmount = Round2(visitValue * cancelPct / 100m);

            // What is still payable if the client reschedules a short-notice cancellation. Taken
            // as a subtraction, never a second percentage, so the two halves always add back to
            // the pre-tax fee exactly - Section 15(c) guarantees the aggregate for the original
            // visit and its makeup never exceeds one full service fee.
            pricing.RemainingBalance = Round2(visitValue - pricing.CancellationAmount);

            // Section 14(b): cap on a failed-access charge.
            pricing.LockoutFee = Round2(visitValue);

            // Section 29(b): the aggregate liability cap, a multiple of the pre-tax per-visit fee.
            // A multiple rather than a lookback in months, so the ceiling cannot drift when the
            // visit frequency or the billing cadence changes.
            pricing.LiabilityCapMultiple = Math.Max(0, pricing.LiabilityCapMultiple);
            pricing.LiabilityCapAmount = Round2(visitValue * pricing.LiabilityCapMultiple);

            pricing.LateChargePercent = Math.Max(0m, pricing.LateChargePercent);
            // Section 11(f) quotes the same charge monthly AND annually. Derived so the two can
            // never disagree - a usury argument turns on exactly that figure.
            pricing.LateChargeAnnualPercent = pricing.LateChargePercent * 12m;

            pricing.ReturnedPaymentFee = Math.Max(0m, pricing.ReturnedPaymentFee);
            pricing.PaymentDeadlineHours = Math.Max(0, pricing.PaymentDeadlineHours);
        }

        /// <summary>
        /// Why a pricing basis cannot be used with this schedule and billing cadence, or null.
        ///
        /// A weekly flat fee needs a WEEKLY service schedule - the allocation divides the fee by
        /// the visits in one week, and "per two calendar weeks" or "per calendar month" has no
        /// fixed number of them - and WEEKLY invoicing (every N weeks bills N weekly fees). A
        /// monthly or per-visit invoice for a weekly fee would have to guess how many weeks it
        /// covers, so it is refused rather than guessed.
        /// </summary>
        public static string? IncompatibilityReason(
            PricingSnapshot pricing, ScheduleSnapshot? schedule, BillingCadenceSnapshot? billing)
        {
            if (pricing?.PricingBasis != ContractPricingBasis.WeeklyFlatFee) return null;

            if (!string.Equals(schedule?.FrequencyUnit?.Trim(), "calendar week", StringComparison.OrdinalIgnoreCase))
                return "A weekly flat fee needs the service schedule to be set per calendar week.";
            if ((schedule?.VisitsPerPeriod ?? 0) < 1)
                return "A weekly flat fee needs at least one scheduled visit per week.";
            if ((billing?.Frequency ?? ContractBillingFrequency.Monthly) != ContractBillingFrequency.Weekly)
                return "A weekly flat fee must be invoiced weekly (or every N weeks). Change the billing "
                    + "cadence, or price per visit.";
            return null;
        }

        private static decimal Round2(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

        private static decimal Clamp(decimal value, decimal min, decimal max) =>
            value < min ? min : value > max ? max : value;
    }
}
