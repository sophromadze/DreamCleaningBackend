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
    /// allocated pro-rata across revenue/tax/tips. Because revenue and tax shrink by the
    /// same factor, Tax stays exactly 8.875% of Revenue no matter how the period's refunds
    /// and discounts fall.
    ///
    /// Sales tax is charged ON TOP of the price — it was never inside SubTotal — so callers
    /// must NOT subtract Tax from Revenue when computing what the company keeps.
    /// </summary>
    public static class OrderRevenueMath
    {
        /// <summary>One order's money split into reporting buckets, net of refunds.</summary>
        public readonly struct OrderMoney
        {
            /// <summary>Taxable cleaning revenue: subtotal after discounts, before tax, without tips.</summary>
            public decimal Revenue { get; init; }

            /// <summary>Sales tax collected — a pass-through owed to the state, never company money.</summary>
            public decimal Tax { get; init; }

            /// <summary>Cleaner tips + company-development tips.</summary>
            public decimal Tips { get; init; }

            /// <summary>
            /// Promo/first-time + subscription + loyalty discounts granted on this order.
            /// Informational: NOT reduced by refunds, because a discount was still given.
            /// </summary>
            public decimal Discounts { get; init; }
        }

        /// <summary>
        /// Splits one order. Every argument is the raw stored column, so callers can pass
        /// values straight from an EF projection without loading the full entity.
        /// </summary>
        public static OrderMoney Split(
            decimal subTotal,
            decimal discountAmount,
            decimal subscriptionDiscountAmount,
            decimal loyaltyDiscountAmount,
            decimal tax,
            decimal tips,
            decimal companyDevelopmentTips,
            decimal totalRefundedAmount)
        {
            var discounts = discountAmount + subscriptionDiscountAmount + loyaltyDiscountAmount;

            // The taxable base — mirrors CalculateTotals' discountedSubTotal, including its
            // clamp at zero, so Tax / Revenue lands on exactly SalesTaxRate.
            var revenue = subTotal - discounts;
            if (revenue < 0m) revenue = 0m;

            var allTips = tips + companyDevelopmentTips;

            // What the customer was billed before gift cards / bubble points / reward credits —
            // the base a refund is allocated across. Those three are payment instruments applied
            // after tax, so they change what was CHARGED, not what was earned or owed to the state.
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

            return new OrderMoney
            {
                Revenue = OrderPricingCalculator.Round2(revenue * kept),
                Tax = OrderPricingCalculator.Round2(tax * kept),
                Tips = OrderPricingCalculator.Round2(allTips * kept),
                Discounts = discounts
            };
        }
    }
}
