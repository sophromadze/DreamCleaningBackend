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

            // Section 15(b): the cancellation charge is a percentage of the SCHEDULED SERVICE FEE
            // as quoted in Exhibit B - the tax-inclusive per-visit total, not the pre-tax fee.
            var cancelPct = Clamp(pricing.CancellationPercent, 0m, 100m);
            pricing.CancellationPercent = cancelPct;
            pricing.CancellationAmount = Round2(pricing.TotalPrice * cancelPct / 100m);

            // What is still payable if the client reschedules a short-notice cancellation. Taken
            // as a subtraction, never a second percentage, so the two halves always add back to
            // the full fee - Section 15(b) guarantees the pair never exceeds it.
            pricing.RemainingBalance = pricing.TotalPrice - pricing.CancellationAmount;

            // Section 14: a lockout is charged at the full scheduled service fee.
            pricing.LockoutFee = pricing.TotalPrice;

            pricing.LateChargePercent = Math.Max(0m, pricing.LateChargePercent);
            pricing.ReturnedPaymentFee = Math.Max(0m, pricing.ReturnedPaymentFee);
            pricing.PaymentDeadlineHours = Math.Max(0, pricing.PaymentDeadlineHours);
        }

        private static decimal Round2(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

        private static decimal Clamp(decimal value, decimal min, decimal max) =>
            value < min ? min : value > max ? max : value;
    }
}
