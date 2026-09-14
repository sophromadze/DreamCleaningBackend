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
        public static void Recalculate(PricingSnapshot pricing)
        {
            if (pricing == null) throw new ArgumentNullException(nameof(pricing));

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

            // Section 15(b): cap on the short-notice cancellation charge.
            var cancelPct = Clamp(pricing.CancellationPercent, 0m, 100m);
            pricing.CancellationPercent = cancelPct;
            pricing.CancellationAmount = Round2(pricing.PreTaxPrice * cancelPct / 100m);

            // What is still payable if the client reschedules a short-notice cancellation. Taken
            // as a subtraction, never a second percentage, so the two halves always add back to
            // the pre-tax fee exactly - Section 15(c) guarantees the aggregate for the original
            // visit and its makeup never exceeds one full service fee.
            pricing.RemainingBalance = pricing.PreTaxPrice - pricing.CancellationAmount;

            // Section 14(b): cap on a failed-access charge.
            pricing.LockoutFee = pricing.PreTaxPrice;

            // Section 29(b): the aggregate liability cap, a multiple of the pre-tax per-visit fee.
            // A multiple rather than a lookback in months, so the ceiling cannot drift when the
            // visit frequency or the billing cadence changes.
            pricing.LiabilityCapMultiple = Math.Max(0, pricing.LiabilityCapMultiple);
            pricing.LiabilityCapAmount = Round2(pricing.PreTaxPrice * pricing.LiabilityCapMultiple);

            pricing.LateChargePercent = Math.Max(0m, pricing.LateChargePercent);
            // Section 11(f) quotes the same charge monthly AND annually. Derived so the two can
            // never disagree - a usury argument turns on exactly that figure.
            pricing.LateChargeAnnualPercent = pricing.LateChargePercent * 12m;

            pricing.ReturnedPaymentFee = Math.Max(0m, pricing.ReturnedPaymentFee);
            pricing.PaymentDeadlineHours = Math.Max(0, pricing.PaymentDeadlineHours);
        }

        private static decimal Round2(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

        private static decimal Clamp(decimal value, decimal min, decimal max) =>
            value < min ? min : value > max ? max : value;
    }
}
