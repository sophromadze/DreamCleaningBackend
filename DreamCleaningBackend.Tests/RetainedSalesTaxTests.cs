using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// SALES TAX COLLECTED OUTSIDE STRIPE IS COMPANY MONEY, NOT A PASS-THROUGH.
    ///
    /// Cash / Zelle / Check / Other payments are never remitted to the state, so the statistics
    /// and finances reports count that tax as revenue instead of as tax owed. The customer is
    /// unaffected: the order still stores, shows and charges the full tax everywhere else.
    ///
    /// What must stay true:
    ///   - a card order behaves exactly as it always did (nothing retained),
    ///   - a manual order's whole tax is retained and lands inside ReportedRevenue,
    ///   - Tax + TaxRetained is ALWAYS the tax the customer was charged, to the cent, so no
    ///     report can double-count or lose a penny of it,
    ///   - the mixed case is decided per top-up, from OrderUpdateHistory's own tax columns,
    ///   - refunds shrink both tax buckets by the same pro-rata factor as everything else.
    /// </summary>
    public class RetainedSalesTaxTests
    {
        private const decimal SubTotal = 400.00m;
        private const decimal Tax = 35.50m;   // 8.875% of 400

        private static OrderRevenueMath.OrderMoney Split(decimal retainedTax, decimal refunded = 0m)
            => OrderRevenueMath.Split(
                subTotal: SubTotal,
                discountAmount: 0m,
                subscriptionDiscountAmount: 0m,
                loyaltyDiscountAmount: 0m,
                tax: Tax,
                tips: 20.00m,
                companyDevelopmentTips: 0m,
                totalRefundedAmount: refunded,
                retainedTax: retainedTax);

        // ── The two ends of the range ────────────────────────────────────────────────

        [Fact]
        public void CardOrder_RemitsAllTax_AndReportsNoRetainedRevenue()
        {
            var retained = OrderRevenueMath.ResolveRetainedTax(
                Tax, PaymentMethod.Normal, taxOnStripePaidAdditions: 0m, taxOnManuallyPaidAdditions: 0m);
            Assert.Equal(0m, retained);

            var money = Split(retained);
            Assert.Equal(Tax, money.Tax);
            Assert.Equal(0m, money.TaxRetained);
            // Unchanged from before the split existed: revenue is the taxable base alone.
            Assert.Equal(SubTotal, money.Revenue);
            Assert.Equal(SubTotal, money.ReportedRevenue);
        }

        [Theory]
        [InlineData(PaymentMethod.Cash)]
        [InlineData(PaymentMethod.Zelle)]
        [InlineData(PaymentMethod.Check)]
        [InlineData(PaymentMethod.Other)]
        public void ManualOrder_RetainsItsWholeTax_AsRevenue(PaymentMethod method)
        {
            var retained = OrderRevenueMath.ResolveRetainedTax(
                Tax, method, taxOnStripePaidAdditions: 0m, taxOnManuallyPaidAdditions: 0m);
            Assert.Equal(Tax, retained);

            var money = Split(retained);
            Assert.Equal(0m, money.Tax);
            Assert.Equal(Tax, money.TaxRetained);
            // The taxable base is untouched — the tax moves buckets, it is not re-derived.
            Assert.Equal(SubTotal, money.Revenue);
            Assert.Equal(SubTotal + Tax, money.ReportedRevenue);
        }

        // ── Mixed payments: the order and its top-ups can disagree ───────────────────

        [Fact]
        public void CardOrder_ToppedUpInCash_RetainsOnlyTheTopUpsTax()
        {
            // A $35.50-tax order edited upward, with the extra collected by Zelle: the history
            // row's NewTax − OriginalTax is $4.44, and only that part escapes remittance.
            var retained = OrderRevenueMath.ResolveRetainedTax(
                Tax, PaymentMethod.Normal,
                taxOnStripePaidAdditions: 0m,
                taxOnManuallyPaidAdditions: 4.44m);

            Assert.Equal(4.44m, retained);

            var money = Split(retained);
            Assert.Equal(31.06m, money.Tax);
            Assert.Equal(4.44m, money.TaxRetained);
        }

        [Fact]
        public void ManualOrder_ToppedUpOnACard_RemitsOnlyTheTopUpsTax()
        {
            // The reverse: a cash job whose later top-up went through Stripe. Only the part that
            // Stripe actually collected is owed to the state.
            var retained = OrderRevenueMath.ResolveRetainedTax(
                Tax, PaymentMethod.Cash,
                taxOnStripePaidAdditions: 4.44m,
                taxOnManuallyPaidAdditions: 0m);

            Assert.Equal(31.06m, retained);

            var money = Split(retained);
            Assert.Equal(4.44m, money.Tax);
            Assert.Equal(31.06m, money.TaxRetained);
        }

        [Fact]
        public void EditsThatLowerTax_CannotPushEitherBucketOutOfRange()
        {
            // An edit can reduce the tax, so a delta sum can be negative or exceed the order's
            // current tax. Both directions clamp instead of inventing a bucket.
            Assert.Equal(0m, OrderRevenueMath.ResolveRetainedTax(
                Tax, PaymentMethod.Normal, 0m, taxOnManuallyPaidAdditions: -10m));
            Assert.Equal(Tax, OrderRevenueMath.ResolveRetainedTax(
                Tax, PaymentMethod.Normal, 0m, taxOnManuallyPaidAdditions: 999m));
            Assert.Equal(0m, OrderRevenueMath.ResolveRetainedTax(
                Tax, PaymentMethod.Cash, taxOnStripePaidAdditions: 999m, taxOnManuallyPaidAdditions: 0m));
        }

        // ── The invariant every report leans on ──────────────────────────────────────

        [Theory]
        [InlineData(0)]
        [InlineData(4.44)]
        [InlineData(17.75)]
        [InlineData(35.50)]
        public void TheTwoBuckets_AlwaysAddBackToTheTaxCharged(double retainedRaw)
        {
            var money = Split((decimal)retainedRaw);
            Assert.Equal(Tax, money.Tax + money.TaxRetained);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(4.44)]
        [InlineData(35.50)]
        public void ARefund_ShrinksBothBuckets_AndTheyStillAddUp(double retainedRaw)
        {
            // Billed = 400 + 35.50 + 20 tips = 455.50; a 227.75 refund is exactly half.
            var money = Split((decimal)retainedRaw, refunded: 227.75m);

            Assert.Equal(200.00m, money.Revenue);
            Assert.Equal(10.00m, money.Tips);
            // Half the tax, however it was split — and never a stray cent between the buckets.
            Assert.Equal(17.75m, money.Tax + money.TaxRetained);
        }

        [Fact]
        public void AFullyRefundedManualOrder_KeepsNoRetainedRevenue()
        {
            var money = Split(Tax, refunded: 455.50m);

            Assert.Equal(0m, money.Revenue);
            Assert.Equal(0m, money.Tax);
            Assert.Equal(0m, money.TaxRetained);
            Assert.Equal(0m, money.ReportedRevenue);
        }

        // ── Callers that don't make the distinction are unaffected ───────────────────

        [Fact]
        public void OmittingTheRetainedArgument_ReproducesTheOldBehaviour()
        {
            var money = OrderRevenueMath.Split(
                subTotal: 367.40m,
                discountAmount: 91.85m,
                subscriptionDiscountAmount: 0m,
                loyaltyDiscountAmount: 0m,
                tax: 24.45m,
                tips: 0m,
                companyDevelopmentTips: 0m,
                totalRefundedAmount: 0m);

            Assert.Equal(275.55m, money.Revenue);
            Assert.Equal(24.45m, money.Tax);
            Assert.Equal(0m, money.TaxRetained);
            Assert.Equal(money.Revenue, money.ReportedRevenue);
        }
    }
}
