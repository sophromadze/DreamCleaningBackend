using DreamCleaningBackend.Helpers.Commercial;
using DreamCleaningBackend.Models.Commercial;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// COMMERCIAL INVOICE MONEY IS SERVER-DERIVED, AND IT IS DECIMAL.
    ///
    /// The browser previews a total; the server computes the one that gets stored and charged.
    /// These tests pin the rules that make the two agree, and the ones where getting it wrong
    /// costs a client real money.
    ///
    /// The reference figures are the same ones the contract module uses, deliberately: $849.99
    /// pre-tax at 8.875% is $75.44 tax and $925.43 total. An invoice raised against that contract
    /// has to reach the same numbers, or the agreement and the bill contradict each other.
    /// </summary>
    public class InvoiceCalculatorTests
    {
        private static InvoiceTotals Calc(
            IEnumerable<(decimal qty, decimal rate)> lines,
            InvoiceTaxType taxType = InvoiceTaxType.Exempt,
            decimal? taxRate = null,
            InvoiceDiscountType discountType = InvoiceDiscountType.None,
            decimal? discountValue = null)
        {
            return InvoiceCalculator.Calculate(new InvoiceTotalsInput
            {
                Lines = lines.Select(l => new InvoiceLineInput { Quantity = l.qty, UnitPrice = l.rate }).ToList(),
                TaxType = taxType,
                TaxRate = taxRate,
                DiscountType = discountType,
                DiscountValue = discountValue
            });
        }

        // ── Line arithmetic ───────────────────────────────────────────────────────────────────

        [Fact]
        public void LineAmount_IsQuantityTimesRate()
        {
            Assert.Equal(925.43m, InvoiceCalculator.LineAmount(1m, 925.43m));
            Assert.Equal(750.00m, InvoiceCalculator.LineAmount(3m, 250.00m));
        }

        /// <summary>Half a day, 2.5 hours: commercial work is not always billed in whole units.</summary>
        [Fact]
        public void LineAmount_SupportsFractionalQuantities()
        {
            Assert.Equal(312.50m, InvoiceCalculator.LineAmount(2.5m, 125.00m));
            Assert.Equal(62.50m, InvoiceCalculator.LineAmount(0.5m, 125.00m));
        }

        [Fact]
        public void SubTotal_SumsEveryLine()
        {
            var totals = Calc(new[] { (1m, 925.43m), (3m, 250.00m), (2.5m, 40.00m) });
            Assert.Equal(1775.43m, totals.SubTotal);
        }

        // ── Tax: the three modes ──────────────────────────────────────────────────────────────

        [Fact]
        public void TaxExempt_AddsNothing()
        {
            var totals = Calc(new[] { (1m, 925.43m) }, InvoiceTaxType.Exempt);

            Assert.Equal(0m, totals.TaxAmount);
            Assert.Equal(925.43m, totals.Total);
        }

        [Fact]
        public void TaxAdded_RidesOnTopOfTheSubtotal()
        {
            var totals = Calc(new[] { (1m, 849.99m) }, InvoiceTaxType.Added, 8.875m);

            // The reference contract's own figures.
            Assert.Equal(75.44m, totals.TaxAmount);
            Assert.Equal(925.43m, totals.Total);
        }

        /// <summary>
        /// TAX-INCLUSIVE LEAVES THE TOTAL ALONE. The agreed amount is what the client pays; the
        /// tax is split back out of it for our records only.
        /// </summary>
        [Fact]
        public void TaxIncluded_KeepsTheTotalAndSplitsTheTaxOut()
        {
            var totals = Calc(new[] { (1m, 925.43m) }, InvoiceTaxType.Included, 8.875m);

            Assert.Equal(925.43m, totals.Total);
            Assert.Equal(75.44m, totals.TaxAmount);
        }

        /// <summary>
        /// THE SPLIT IS BY SUBTRACTION, NOT BY MULTIPLICATION, and this is the case that proves
        /// why: at $300.00 inclusive there is no cent-valued subtotal S where
        /// S + round2(S x 8.875%) = 300.00 exactly. Deriving the tax as round2(preTax x rate)
        /// therefore drifts, and the invoice would print a subtotal and a tax that do not add back
        /// to the total the client is being asked to pay.
        ///
        /// The same reasoning as ContractPricingCalculator and the booking side's
        /// splitTaxInclusiveAmount - all three must stay on the subtraction rule.
        /// </summary>
        [Fact]
        public void TaxIncluded_SubtotalPlusTaxAlwaysAddsBackToTheTotalExactly()
        {
            foreach (var amount in new[] { 300.00m, 925.43m, 1000.00m, 12.34m, 4999.99m, 87.65m })
            {
                var totals = Calc(new[] { (1m, amount) }, InvoiceTaxType.Included, 8.875m);
                var preTax = totals.Total - totals.TaxAmount;

                Assert.Equal(amount, totals.Total);
                Assert.Equal(totals.Total, preTax + totals.TaxAmount);
            }
        }

        [Fact]
        public void MissingTaxRate_IsTreatedAsZeroRatherThanInflatingTheBill()
        {
            var added = Calc(new[] { (1m, 500m) }, InvoiceTaxType.Added, null);
            Assert.Equal(0m, added.TaxAmount);
            Assert.Equal(500m, added.Total);
        }

        // ── Discounts ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public void FixedDiscount_ComesOffTheSubtotal()
        {
            var totals = Calc(new[] { (1m, 1000m) }, discountType: InvoiceDiscountType.FixedAmount,
                discountValue: 150m);

            Assert.Equal(150m, totals.DiscountAmount);
            Assert.Equal(850m, totals.Total);
        }

        [Fact]
        public void PercentageDiscount_IsAPercentOfTheSubtotal()
        {
            var totals = Calc(new[] { (1m, 1000m) }, discountType: InvoiceDiscountType.Percentage,
                discountValue: 12.5m);

            Assert.Equal(125m, totals.DiscountAmount);
            Assert.Equal(875m, totals.Total);
        }

        /// <summary>
        /// A discount bigger than the bill is CAPPED, not carried through. Without this the total
        /// goes negative and the invoice claims the business owes the client money - which is a
        /// credit note, and this system does not issue those.
        /// </summary>
        [Fact]
        public void DiscountLargerThanTheSubtotal_IsCappedAndNeverGoesNegative()
        {
            var fixedOver = Calc(new[] { (1m, 100m) },
                discountType: InvoiceDiscountType.FixedAmount, discountValue: 500m);
            Assert.Equal(100m, fixedOver.DiscountAmount);
            Assert.Equal(0m, fixedOver.Total);

            var percentOver = Calc(new[] { (1m, 100m) },
                discountType: InvoiceDiscountType.Percentage, discountValue: 250m);
            Assert.Equal(100m, percentOver.DiscountAmount);
            Assert.Equal(0m, percentOver.Total);
        }

        /// <summary>
        /// TAX IS COMPUTED ON THE DISCOUNTED SUBTOTAL. Taxing the gross figure would charge the
        /// client tax on money they were never billed.
        /// </summary>
        [Fact]
        public void TaxIsChargedOnTheDiscountedSubtotal_NotTheGross()
        {
            var totals = Calc(new[] { (1m, 1000m) }, InvoiceTaxType.Added, 10m,
                InvoiceDiscountType.FixedAmount, 200m);

            Assert.Equal(200m, totals.DiscountAmount);
            Assert.Equal(80m, totals.TaxAmount);   // 10% of 800, not of 1000
            Assert.Equal(880m, totals.Total);
        }

        // ── Payments and balance ──────────────────────────────────────────────────────────────

        [Fact]
        public void AmountPaid_IsTheSignedSumSoReversalsSubtract()
        {
            Assert.Equal(500m, InvoiceCalculator.ResolveAmountPaid(new[] { 200m, 300m }));
            // A reversal is a negative row, not a deleted one - the history stays complete.
            Assert.Equal(200m, InvoiceCalculator.ResolveAmountPaid(new[] { 200m, 300m, -300m }));
        }

        [Fact]
        public void Balance_IsTotalMinusPaid()
        {
            Assert.Equal(3000m, InvoiceCalculator.ResolveBalance(5000m, 2000m));
            Assert.Equal(0m, InvoiceCalculator.ResolveBalance(5000m, 5000m));
        }

        /// <summary>
        /// THE BALANCE IS FLOORED AT ZERO. A negative "balance due" on an invoice reads as a
        /// refund the system has promised; an overpayment is surfaced separately instead.
        /// </summary>
        [Fact]
        public void Balance_NeverGoesNegative_AndOverpaymentIsReportedSeparately()
        {
            Assert.Equal(0m, InvoiceCalculator.ResolveBalance(925.43m, 1000m));
            Assert.Equal(74.57m, InvoiceCalculator.ResolveOverpayment(925.43m, 1000m));
            Assert.Equal(0m, InvoiceCalculator.ResolveOverpayment(925.43m, 925.43m));
        }

        // ── Due dates ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public void DueDate_FollowsTheChosenTerms()
        {
            var invoiceDate = new DateTime(2026, 9, 7);

            Assert.Equal(new DateTime(2026, 9, 7),
                InvoiceCalculator.ResolveDueDate(invoiceDate, InvoiceDueTerms.DueOnReceipt, null));
            Assert.Equal(new DateTime(2026, 9, 14),
                InvoiceCalculator.ResolveDueDate(invoiceDate, InvoiceDueTerms.Net7, null));
            Assert.Equal(new DateTime(2026, 9, 22),
                InvoiceCalculator.ResolveDueDate(invoiceDate, InvoiceDueTerms.Net15, null));
            Assert.Equal(new DateTime(2026, 10, 7),
                InvoiceCalculator.ResolveDueDate(invoiceDate, InvoiceDueTerms.Net30, null));
        }

        [Fact]
        public void DueDate_CustomKeepsWhateverTheAdminPicked()
        {
            var chosen = new DateTime(2026, 12, 1);
            Assert.Equal(chosen, InvoiceCalculator.ResolveDueDate(
                new DateTime(2026, 9, 7), InvoiceDueTerms.Custom, chosen));
        }
    }
}
