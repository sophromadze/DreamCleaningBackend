using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Services
{
    /// <summary>
    /// SINGLE SOURCE OF TRUTH for turning a stored order into the reporting buckets the
    /// statistics and finances pages show: taxable cleaning revenue, sales tax, tips and
    /// discounts given — each already net of whatever was refunded.
    ///
    /// It exists because the raw columns cannot be summed naively:
    ///
    ///   1. Order.Tax is 8.875% of the DISCOUNTED subtotal (see OrderPricingCalculator
    ///      .CalculateTotals), so summing Order.SubTotal and Order.Tax side by side compares
    ///      a pre-discount number against a post-discount one. Every promo code, first-time
    ///      discount, subscription discount and loyalty discount pushes the reported tax
    ///      below 8.875% of the reported amount.
    ///   2. Refunds have to come off every bucket they touched, not just the revenue one.
    ///      A refund returns the customer their tax and tips too, so subtracting the gross
    ///      refund from revenue alone pushes the reported tax ABOVE 8.875%.
    ///
    /// Split() fixes both: revenue is the post-discount (taxable) base, and the refund is
    /// allocated pro-rata across revenue/tax/tips.
    ///
    /// Sales tax is charged ON TOP of the price — it was never inside SubTotal — so callers
    /// must NOT subtract Tax from Revenue when computing what the company keeps.
    ///
    /// ── Retained tax (2026-08) ───────────────────────────────────────────────────────
    /// Sales tax charged on a payment collected OUTSIDE Stripe (Cash / Zelle / Check /
    /// Other) is not remitted to the state, so for reporting purposes the company keeps it.
    /// Split therefore hands the tax back in TWO buckets — <see cref="OrderMoney.Tax"/>
    /// (owed to the state) and <see cref="OrderMoney.TaxRetained"/> (company money) — which
    /// always add back to the tax that was actually charged. ONLY THE REPORTS CHANGED: the
    /// order still stores, shows and charges the full tax to the customer everywhere else.
    /// </summary>
    public static class OrderRevenueMath
    {
        /// <summary>One order's money split into reporting buckets, net of refunds.</summary>
        public readonly struct OrderMoney
        {
            /// <summary>Taxable cleaning revenue: subtotal after discounts, before tax, without tips.</summary>
            public decimal Revenue { get; init; }

            /// <summary>
            /// Sales tax collected through Stripe — a pass-through owed to the state, never
            /// company money. Tax + TaxRetained is the whole tax the customer was charged.
            /// </summary>
            public decimal Tax { get; init; }

            /// <summary>
            /// Sales tax collected outside Stripe (Cash/Zelle/Check/Other). Not remitted, so it
            /// is company money and is reported inside <see cref="ReportedRevenue"/>, never as
            /// a pass-through. Broken out only so a report can say how much of the revenue it is.
            /// </summary>
            public decimal TaxRetained { get; init; }

            /// <summary>Cleaner tips + company-development tips.</summary>
            public decimal Tips { get; init; }

            /// <summary>
            /// Promo/first-time + subscription + loyalty discounts granted on this order.
            /// Informational: NOT reduced by refunds, because a discount was still given.
            /// </summary>
            public decimal Discounts { get; init; }

            /// <summary>
            /// What the reports call "Company Revenue": the taxable cleaning revenue plus the
            /// tax the company kept because it was never collected through Stripe. This — not
            /// Revenue — is what feeds TotalAmount / the daily Amount, so the retained tax
            /// flows through the gross margin into net income exactly like earned revenue.
            /// </summary>
            public decimal ReportedRevenue => Revenue + TaxRetained;
        }

        /// <summary>
        /// How much of an order's sales tax was collected OUTSIDE Stripe, and so is kept by the
        /// company rather than remitted.
        ///
        /// The order's own PaymentMethod covers the original charge; an order edit's additional
        /// amount can be collected by a different method (a card-paid order topped up in cash, or
        /// the reverse), and OrderUpdateHistory records both OriginalTax and NewTax — so the tax
        /// a top-up added is the exact difference, never an r/(1+r) back-derivation of the
        /// tax-inclusive AdditionalAmount.
        ///
        /// UNPAID top-ups fall in with the base order: their tax sits in Order.Tax but nobody has
        /// handed it over yet, and guessing how it will eventually be collected would be inventing
        /// a fact. Clamped into [0, tax] because edits can move tax in either direction.
        /// </summary>
        public static decimal ResolveRetainedTax(
            decimal tax,
            PaymentMethod orderPaymentMethod,
            decimal taxOnStripePaidAdditions,
            decimal taxOnManuallyPaidAdditions)
        {
            var retained = orderPaymentMethod == PaymentMethod.Normal
                // Card order: only the top-ups that were settled in cash/Zelle/check are kept.
                ? taxOnManuallyPaidAdditions
                // Manual order: everything except the top-ups that did go through Stripe.
                : tax - taxOnStripePaidAdditions;

            if (retained < 0m) return 0m;
            return retained > tax ? tax : retained;
        }

        /// <summary>
        /// Splits one order. Every argument is the raw stored column, so callers can pass
        /// values straight from an EF projection without loading the full entity.
        /// </summary>
        /// <param name="retainedTax">
        /// The part of <paramref name="tax"/> that was collected outside Stripe — resolve it with
        /// <see cref="ResolveRetainedTax"/>. Left at 0 for a report that does not make the
        /// distinction; every bucket then behaves exactly as it did before the split existed.
        /// </param>
        public static OrderMoney Split(
            decimal subTotal,
            decimal discountAmount,
            decimal subscriptionDiscountAmount,
            decimal loyaltyDiscountAmount,
            decimal tax,
            decimal tips,
            decimal companyDevelopmentTips,
            decimal totalRefundedAmount,
            decimal retainedTax = 0m)
        {
            var discounts = discountAmount + subscriptionDiscountAmount + loyaltyDiscountAmount;

            // The taxable base — mirrors CalculateTotals' discountedSubTotal, including its
            // clamp at zero, so (Tax + TaxRetained) / Revenue lands on exactly SalesTaxRate.
            var revenue = subTotal - discounts;
            if (revenue < 0m) revenue = 0m;

            var allTips = tips + companyDevelopmentTips;

            // What the customer was billed before gift cards / bubble points / reward credits —
            // the base a refund is allocated across. Those three are payment instruments applied
            // after tax, so they change what was CHARGED, not what was earned or owed to the state.
            // The WHOLE tax belongs in this base: a refund hands the customer back all of it,
            // whichever way it was collected.
            var billed = revenue + tax + allTips;

            var kept = 1m;
            if (billed > 0m && totalRefundedAmount > 0m)
            {
                // Clamped: a refund reconciled from the Stripe Dashboard can exceed this base
                // (it may include an amount collected outside the order), and a negative
                // kept-ratio would invent income.
                var refunded = Math.Min(totalRefundedAmount, billed);
                kept = (billed - refunded) / billed;
            }

            if (retainedTax < 0m) retainedTax = 0m;
            if (retainedTax > tax) retainedTax = tax;

            // The remitted bucket is the rounded whole MINUS the rounded retained bucket, rather
            // than a third independent rounding, so the two always add back to the order's tax
            // to the cent no matter how the refund ratio falls.
            var taxNet = OrderPricingCalculator.Round2(tax * kept);
            var retainedNet = OrderPricingCalculator.Round2(retainedTax * kept);
            if (retainedNet > taxNet) retainedNet = taxNet;

            return new OrderMoney
            {
                Revenue = OrderPricingCalculator.Round2(revenue * kept),
                Tax = taxNet - retainedNet,
                TaxRetained = retainedNet,
                Tips = OrderPricingCalculator.Round2(allTips * kept),
                Discounts = discounts
            };
        }
    }
}
