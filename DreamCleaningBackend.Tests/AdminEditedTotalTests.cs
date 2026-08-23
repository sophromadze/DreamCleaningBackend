using DreamCleaningBackend.Services;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// THE ADMIN-TYPED TOTAL.
    ///
    /// An admin editing an order can type what the customer owes instead of working back from a
    /// subtotal, exactly like Custom Pricing at booking. The figure is TAX-INCLUSIVE and
    /// POST-discount, so the editor splits it, keeps the order's recorded discounts untouched, and
    /// derives the SUBTOTAL from the two. The split tax rides along as TaxOverride.
    ///
    /// TaxOverrideBase is what makes the two callers coexist. It names the amount the tax was split
    /// out of:
    ///   - Custom Pricing splits a PRE-discount amount, so its base is the SubTotal (null default)
    ///     and any discount voids the override — the long-standing rule, unchanged.
    ///   - The admin editor splits a POST-discount amount, so its base is the discounted subtotal
    ///     and the discounts are expected.
    ///
    /// Collapsing those two into one guard is the regression this file exists to catch.
    /// </summary>
    public class AdminEditedTotalTests
    {
        /// <summary>Mirrors what the admin editor computes when 300.00 is typed.</summary>
        private const decimal Typed = 300.00m;
        private const decimal SplitSubTotal = 275.55m;
        private const decimal SplitTax = 24.45m;

        [Fact]
        public void SplittingTheTypedAmount_AddsBackToItExactly()
        {
            var split = OrderPricingCalculator.SplitTaxInclusiveAmount(Typed);

            Assert.Equal(SplitSubTotal, split.subTotal);
            Assert.Equal(SplitTax, split.tax);
            Assert.Equal(Typed, split.subTotal + split.tax);
        }

        /// <summary>
        /// The whole point: a discounted order still charges EXACTLY what was typed. Without the
        /// override the rate math lands a cent low (see the companion test below), which is the
        /// drift that makes a tax-inclusive field unusable.
        /// </summary>
        [Fact]
        public void TypedTotal_IsChargedToTheCent_WithADiscountOnTheOrder()
        {
            const decimal promo = 50.00m;

            var totals = OrderPricingCalculator.CalculateTotals(new OrderPricingCalculator.TotalsInput
            {
                // What the editor stores: the split subtotal with the discounts added back on.
                SubTotal = SplitSubTotal + promo,
                DiscountAmount = promo,
                TaxOverride = SplitTax,
                TaxOverrideBase = SplitSubTotal
            });

            Assert.Equal(SplitSubTotal, totals.DiscountedSubTotal);
            Assert.Equal(SplitTax, totals.Tax);
            Assert.Equal(Typed, totals.Total);
        }

        /// <summary>
        /// Pins the cent of drift the override exists to remove. 300.00 is one of the amounts no
        /// cent-valued subtotal can reach through the rate math: 275.55 overshoots to 300.01 and
        /// 275.54 undershoots to 299.99, so the tax HAS to come from the typed figure.
        /// </summary>
        [Fact]
        public void WithoutTheOverride_TheSameOrderMissesTheTypedTotalByACent()
        {
            const decimal promo = 50.00m;

            var totals = OrderPricingCalculator.CalculateTotals(new OrderPricingCalculator.TotalsInput
            {
                SubTotal = SplitSubTotal + promo,
                DiscountAmount = promo
            });

            Assert.Equal(300.01m, totals.Total);
            Assert.NotEqual(Typed, totals.Total);
        }

        /// <summary>
        /// CUSTOM PRICING, UNCHANGED: base defaults to the SubTotal, so a discount still voids the
        /// override and the tax reverts to the rate math.
        /// </summary>
        [Fact]
        public void CustomPricingDefault_StillVoidsTheOverrideOnAnyDiscount()
        {
            var withoutDiscount = OrderPricingCalculator.CalculateTotals(new OrderPricingCalculator.TotalsInput
            {
                SubTotal = SplitSubTotal,
                TaxOverride = SplitTax
            });
            Assert.Equal(SplitTax, withoutDiscount.Tax);

            var withDiscount = OrderPricingCalculator.CalculateTotals(new OrderPricingCalculator.TotalsInput
            {
                SubTotal = SplitSubTotal,
                DiscountAmount = 10.00m,
                TaxOverride = SplitTax
            });
            Assert.Equal(
                OrderPricingCalculator.Round2((SplitSubTotal - 10.00m) * OrderPricingCalculator.SalesTaxRate),
                withDiscount.Tax);
        }

        /// <summary>
        /// The base is VERIFIED, not trusted. A request whose base no longer matches the subtotal
        /// the order's discounts actually leave behind — a discount changed after the total was
        /// typed, or a hand-rolled API call — prices the ordinary way instead of quietly applying
        /// someone else's tax figure.
        /// </summary>
        [Fact]
        public void AStaleBase_FallsBackToTheRateMath()
        {
            var totals = OrderPricingCalculator.CalculateTotals(new OrderPricingCalculator.TotalsInput
            {
                SubTotal = SplitSubTotal + 50.00m,
                DiscountAmount = 75.00m,          // discount moved after the total was typed
                TaxOverride = SplitTax,
                TaxOverrideBase = SplitSubTotal   // no longer the amount being taxed
            });

            var discounted = SplitSubTotal + 50.00m - 75.00m;
            Assert.Equal(discounted, totals.DiscountedSubTotal);
            Assert.Equal(
                OrderPricingCalculator.Round2(discounted * OrderPricingCalculator.SalesTaxRate),
                totals.Tax);
        }

        /// <summary>
        /// THE ROUND TRIP, with every discount type an order can carry at once.
        ///
        /// The frontend solves for the subtotal and the re-scaled discounts (see
        /// shared/pricing/admin-total-solve.ts) and posts those. The server does NOT repeat the
        /// solve — it re-prices from what it received and uses TaxOverrideBase to verify the split.
        /// These are the exact numbers that solve produces for a $200 typed total on a $170 order
        /// carrying a 25% promo, a 15% subscription discount and 10% loyalty; if the two sides ever
        /// stop agreeing, the customer is charged something other than what the admin was shown.
        /// </summary>
        [Fact]
        public void SolvedNumbersFromTheEditor_RepriceToTheTypedTotal_WithEveryDiscountStacked()
        {
            var totals = OrderPricingCalculator.CalculateTotals(new OrderPricingCalculator.TotalsInput
            {
                SubTotal = 367.40m,
                DiscountAmount = 91.85m,              // 25%
                SubscriptionDiscountAmount = 55.11m,  // 15%
                LoyaltyDiscountAmount = 36.74m,       // 10%
                TaxOverride = 16.30m,
                TaxOverrideBase = 183.70m
            });

            Assert.Equal(183.70m, totals.DiscountedSubTotal);
            Assert.Equal(16.30m, totals.Tax);
            Assert.Equal(200.00m, totals.Total);
        }

        /// <summary>
        /// Subscription and loyalty are ALWAYS percentages (Subscription.DiscountPercentage and the
        /// locked Order.LoyaltyDiscountPercentage), so scaling them with the subtotal is exact.
        /// A promo code or special offer can be flat instead — see the gap documented in
        /// admin-total-solve.spec.ts — but that changes only which dollar figure is attributed to
        /// the discount, never whether this reprice lands on the typed total.
        /// </summary>
        [Theory]
        [InlineData(0, 0)]              // no discounts at all
        [InlineData(91.85, 0)]          // promo only
        [InlineData(0, 55.11)]          // subscription only
        [InlineData(91.85, 55.11)]      // both
        public void TheRepriceLandsOnTheTypedTotal_WhicheverDiscountsArePresent(
            decimal discount, decimal subscriptionDiscount)
        {
            const decimal discounted = 183.70m;

            var totals = OrderPricingCalculator.CalculateTotals(new OrderPricingCalculator.TotalsInput
            {
                SubTotal = discounted + discount + subscriptionDiscount,
                DiscountAmount = discount,
                SubscriptionDiscountAmount = subscriptionDiscount,
                TaxOverride = 16.30m,
                TaxOverrideBase = discounted
            });

            Assert.Equal(200.00m, totals.Total);
        }

        /// <summary>Tips sit outside the taxed amount, so they ride on top without disturbing it.</summary>
        [Fact]
        public void TipsAreAddedOnTop_AndDoNotDisturbTheTypedTotal()
        {
            var totals = OrderPricingCalculator.CalculateTotals(new OrderPricingCalculator.TotalsInput
            {
                SubTotal = SplitSubTotal,
                TaxOverride = SplitTax,
                TaxOverrideBase = SplitSubTotal,
                Tips = 40.00m
            });

            Assert.Equal(SplitTax, totals.Tax);
            Assert.Equal(Typed + 40.00m, totals.Total);
        }
    }
}
