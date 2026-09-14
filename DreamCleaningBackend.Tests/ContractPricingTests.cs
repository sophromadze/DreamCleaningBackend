using DreamCleaningBackend.Helpers.Contracts;
using DreamCleaningBackend.Models.Contracts;
using DreamCleaningBackend.Services.Contracts;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// CONTRACT PRICING IS SERVER-DERIVED.
    ///
    /// An admin types three numbers - mode, amount, tax rate - and Exhibit B quotes six. Those six
    /// are also quoted back inside the body of the agreement (Sections 14 and 15 name the
    /// cancellation charge, the remaining balance and the lockout fee in dollars), so a cent of
    /// drift between them is not a rounding nit: it is a contract that contradicts itself.
    ///
    /// The reference agreement is the fixture: $849.99 pre-tax at 8.875% is $75.44 tax and $925.43
    /// total.
    ///
    /// THE CAPS ARE ALL BUILT ON THE PRE-TAX FEE. A 50% short-notice cancellation splits $849.99
    /// into $425.00 retained and $424.99 still payable; a failed-access charge is capped at
    /// $849.99; the aggregate liability cap is thirteen times it. Sales tax attaches to a taxable
    /// supply and a visit that did not happen is not one, so none of the three carries tax of its
    /// own - whatever tax the law puts on the charge actually made is added when it is made.
    /// </summary>
    public class ContractPricingTests
    {
        private static PricingSnapshot Recalculate(
            ContractPriceMode mode, decimal amount, decimal rate = 8.875m, decimal cancelPct = 50m)
        {
            var pricing = new PricingSnapshot
            {
                PriceMode = mode,
                PriceInput = amount,
                SalesTaxRatePercent = rate,
                CancellationPercent = cancelPct
            };
            ContractPricingCalculator.Recalculate(pricing);
            return pricing;
        }

        [Fact]
        public void PreTaxMode_ReproducesTheReferenceAgreementExactly()
        {
            var p = Recalculate(ContractPriceMode.PreTax, 849.99m);

            Assert.Equal(849.99m, p.PreTaxPrice);
            Assert.Equal(75.44m, p.SalesTaxAmount);
            Assert.Equal(925.43m, p.TotalPrice);

            // Section 15(b): capped at 50% of the PRE-TAX fee. 849.99 / 2 = 424.995, rounded
            // half-up to 425.00 - the figure Exhibit B2(d) quotes.
            Assert.Equal(425.00m, p.CancellationAmount);
            Assert.Equal(424.99m, p.RemainingBalance);

            // Section 14(b): a failed-access charge is capped at the visit's PRE-TAX fee, plus
            // whatever tax is legally applicable to that charge - not at the tax-inclusive total.
            Assert.Equal(849.99m, p.LockoutFee);

            // Section 29(b): thirteen times the pre-tax per-visit fee.
            Assert.Equal(13, p.LiabilityCapMultiple);
            Assert.Equal(11049.87m, p.LiabilityCapAmount);
        }

        /// <summary>
        /// Section 11(f) quotes the late charge monthly AND annually. Derived from the one stored
        /// rate so the pair can never contradict each other: a usury argument turns on exactly
        /// that figure, and two numbers typed separately eventually disagree.
        /// </summary>
        [Fact]
        public void TheAnnualLateChargeIsTwelveTimesTheMonthlyOne()
        {
            var p = new PricingSnapshot { PriceInput = 849.99m, LateChargePercent = 1m };
            ContractPricingCalculator.Recalculate(p);

            Assert.Equal(12m, p.LateChargeAnnualPercent);
        }

        [Fact]
        public void TaxInclusiveMode_SplitsTheTypedTotalWithoutLosingACent()
        {
            // The admin types what the client pays. The split must add back to it exactly, which
            // is why the tax is a subtraction rather than a second round2(subtotal * rate).
            var p = Recalculate(ContractPriceMode.TaxInclusive, 925.43m);

            Assert.Equal(925.43m, p.TotalPrice);
            Assert.Equal(p.TotalPrice, p.PreTaxPrice + p.SalesTaxAmount);
        }

        [Theory]
        [InlineData(300.00)]
        [InlineData(925.43)]
        [InlineData(1000.00)]
        [InlineData(499.99)]
        [InlineData(0.01)]
        public void TaxInclusiveSplit_AlwaysAddsBackToTheTypedAmount(decimal typed)
        {
            var p = Recalculate(ContractPriceMode.TaxInclusive, typed);
            Assert.Equal(typed, p.PreTaxPrice + p.SalesTaxAmount);
        }

        [Theory]
        [InlineData(849.99)]
        [InlineData(1.00)]
        [InlineData(1234.56)]
        [InlineData(0.03)]
        public void CancellationAndRemainingBalance_AlwaysAddBackToThePreTaxFee(decimal preTax)
        {
            // Section 15(c) guarantees that the original visit and its makeup never cost more than
            // one full service fee between them. Taking the remainder as a subtraction rather than
            // a second percentage is what makes that true for every amount, odd cents included.
            var p = Recalculate(ContractPriceMode.PreTax, preTax);
            Assert.Equal(p.PreTaxPrice, p.CancellationAmount + p.RemainingBalance);
        }

        /// <summary>
        /// THE BASIS IS THE PRE-TAX FEE, AND THE DIFFERENCE IS REAL MONEY.
        ///
        /// Both figures round cleanly, so a wrong basis does not look wrong - it just quietly
        /// charges the client $37.72 of sales tax on a visit nobody performed, which Contractor
        /// would then have no basis to remit. This is the assertion that pins it.
        /// </summary>
        [Fact]
        public void CancellationIsAPercentageOfThePreTaxFee_NotTheTaxInclusiveTotal()
        {
            var p = Recalculate(ContractPriceMode.PreTax, 849.99m);

            Assert.Equal(425.00m, p.CancellationAmount);
            Assert.NotEqual(462.72m, p.CancellationAmount);
        }

        /// <summary>
        /// Section 14(b) caps a failed-access charge at the visit's pre-tax fee. Same reasoning:
        /// the cap is on documented net loss, and tax rides on the charge actually made.
        /// </summary>
        [Fact]
        public void TheFailedAccessCapIsThePreTaxFee_NotTheTotal()
        {
            var p = Recalculate(ContractPriceMode.PreTax, 849.99m);

            Assert.Equal(849.99m, p.LockoutFee);
            Assert.NotEqual(p.TotalPrice, p.LockoutFee);
        }

        /// <summary>
        /// The liability cap follows the pre-tax fee too, and a zero multiple is a real (if
        /// unusual) choice rather than something to silently replace with the default.
        /// </summary>
        [Theory]
        [InlineData(13, 11049.87)]
        [InlineData(1, 849.99)]
        [InlineData(0, 0)]
        public void TheLiabilityCapIsAMultipleOfThePreTaxFee(int multiple, decimal expected)
        {
            var p = new PricingSnapshot
            {
                PriceMode = ContractPriceMode.PreTax,
                PriceInput = 849.99m,
                SalesTaxRatePercent = 8.875m,
                LiabilityCapMultiple = multiple
            };
            ContractPricingCalculator.Recalculate(p);

            Assert.Equal(expected, p.LiabilityCapAmount);
        }

        [Fact]
        public void ZeroTaxRate_LeavesThePriceUntouchedInBothModes()
        {
            var preTax = Recalculate(ContractPriceMode.PreTax, 500m, rate: 0m);
            Assert.Equal(500m, preTax.TotalPrice);
            Assert.Equal(0m, preTax.SalesTaxAmount);

            var inclusive = Recalculate(ContractPriceMode.TaxInclusive, 500m, rate: 0m);
            Assert.Equal(500m, inclusive.PreTaxPrice);
            Assert.Equal(0m, inclusive.SalesTaxAmount);
        }

        [Fact]
        public void NegativeInputsAreClampedRatherThanQuotedAsNegativeMoney()
        {
            var p = Recalculate(ContractPriceMode.PreTax, -100m);
            Assert.Equal(0m, p.PreTaxPrice);
            Assert.Equal(0m, p.TotalPrice);
        }

        [Fact]
        public void CancellationPercentIsClampedToARealPercentage()
        {
            var over = Recalculate(ContractPriceMode.PreTax, 100m, cancelPct: 400m);
            Assert.Equal(100m, over.CancellationPercent);
            // Even at the clamp, the two halves still add back to the pre-tax fee.
            Assert.Equal(over.PreTaxPrice, over.CancellationAmount + over.RemainingBalance);
        }
    }

    /// <summary>
    /// A contract spells its numbers out. If a token returned bare digits, changing a term from
    /// its default would visibly break the register of the document - "an initial term of 6
    /// months" inside a paragraph whose neighbours all read "twelve (12)".
    /// </summary>
    public class ContractTextFormatTests
    {
        [Theory]
        [InlineData(1, "one (1)")]
        [InlineData(3, "three (3)")]
        [InlineData(12, "twelve (12)")]
        [InlineData(15, "fifteen (15)")]
        [InlineData(24, "twenty-four (24)")]
        [InlineData(30, "thirty (30)")]
        [InlineData(48, "forty-eight (48)")]
        public void CountsAreSpelledOutAlongsideTheirDigits(int value, string expected)
        {
            Assert.Equal(expected, ContractTextFormat.WordsWithDigits(value));
        }

        [Fact]
        public void PercentagesKeepTheWordingTheAgreementUses()
        {
            Assert.Equal("fifty percent (50%)", ContractTextFormat.PercentWithDigits(50m));
            Assert.Equal("one and one-half percent (1.5%)", ContractTextFormat.PercentWithDigits(1.5m));
        }

        [Fact]
        public void TheTaxRateKeepsItsThreeDecimalsAndDropsNothing()
        {
            // 8.875% must never render as 8.88% or 8.9%: it is quoted in Exhibit B as the rate the
            // total was calculated from.
            Assert.Equal("8.875%", ContractTextFormat.FormatPercent(8.875m));
            Assert.Equal("50%", ContractTextFormat.FormatPercent(50m));
        }

        [Fact]
        public void MoneyIsAlwaysTwoDecimals()
        {
            Assert.Equal("$925.43", ContractTextFormat.Money(925.43m));
            Assert.Equal("$5,000.00", ContractTextFormat.Money(5000m));
        }

        [Fact]
        public void SpelledMoneyMatchesSection11g()
        {
            Assert.Equal("thirty-five dollars ($35.00)", ContractTextFormat.MoneyWithWords(35m));
        }

        [Fact]
        public void PhoneNumbersAreFormattedForTheNoticeBlock()
        {
            // Phones are stored digits-only like every other phone column in this codebase; the
            // notice block has to print one a person can dial.
            Assert.Equal("(929) 930-1525", ContractTextFormat.Phone("9299301525"));
            Assert.Equal("(929) 930-1525", ContractTextFormat.Phone("19299301525"));
        }

        [Fact]
        public void SlugsProduceAUsableExecutedFilename()
        {
            Assert.Equal("chick-tastic-llc", ContractTextFormat.Slug("Chick Tastic LLC"));
            Assert.Equal("client", ContractTextFormat.Slug("  "));
        }

        [Fact]
        public void AnUnsetEffectiveDateRendersABlankRatherThanACrash()
        {
            Assert.Equal("________________", ContractTextFormat.LongDate(null));
            Assert.Equal("September 6, 2026",
                ContractTextFormat.LongDate(new DateTime(2026, 9, 6)));
        }
    }
}
