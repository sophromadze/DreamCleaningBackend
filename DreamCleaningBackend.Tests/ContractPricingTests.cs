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
    /// total, and a 50% short-notice cancellation splits that into $462.72 retained and $462.71
    /// still payable.
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
            Assert.Equal(462.72m, p.CancellationAmount);
            Assert.Equal(462.71m, p.RemainingBalance);
            // Section 14: a lockout is charged at the FULL scheduled service fee.
            Assert.Equal(925.43m, p.LockoutFee);
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
        public void CancellationAndRemainingBalance_AlwaysAddBackToTheFullFee(decimal preTax)
        {
            // Section 15(b) guarantees the pair never exceeds the full scheduled service fee.
            // Taking the remainder as a subtraction rather than a second percentage is what makes
            // that true for every amount, including the odd-cent ones.
            var p = Recalculate(ContractPriceMode.PreTax, preTax);
            Assert.Equal(p.TotalPrice, p.CancellationAmount + p.RemainingBalance);
        }

        [Fact]
        public void CancellationIsAPercentageOfTheTaxInclusiveFee_NotThePreTaxFee()
        {
            // Exhibit B quotes the cancellation charge against the per-visit total the client
            // actually pays. Computing it off the pre-tax fee would quote $425.00, not $462.72.
            var p = Recalculate(ContractPriceMode.PreTax, 849.99m);
            Assert.Equal(462.72m, p.CancellationAmount);
            Assert.NotEqual(425.00m, p.CancellationAmount);
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
            // Even at the clamp, the two halves still add back to the full fee.
            Assert.Equal(over.TotalPrice, over.CancellationAmount + over.RemainingBalance);
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
