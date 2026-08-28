using DreamCleaningBackend.Services;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// Default cleaner pay rates per service type. The rule is name-based (the effective,
    /// human-facing service-type name — the custom "Pre-Arranged" label when there is one), so
    /// these tests pin the keyword matching against the names that actually exist in the catalog.
    ///
    /// Owner's rates (2026-08): regular 20, deep 21, move in/out 21, office 20, custom 20,
    /// post construction 25, heavy 25, filthy 28.
    ///
    /// The frontend mirror (getDefaultCleanerHourlyRate in order-pricing.calculator.ts) must stay
    /// in step with every case below.
    /// </summary>
    public class CleanerHourlyRateDefaultsTests
    {
        private const decimal NoDeepFee = 0m;
        private const decimal DeepFee = 50m;

        [Theory]
        [InlineData("Residential Cleaning")]
        [InlineData("Office Cleaning")]
        [InlineData("Regular")]
        [InlineData("Custom")]
        [InlineData("")]
        [InlineData(null)]
        public void RegularOrders_PayTheBaseRate(string? serviceTypeName)
        {
            Assert.Equal(20m, OrderPricingCalculator.GetDefaultCleanerHourlyRate(NoDeepFee, serviceTypeName));
        }

        [Fact]
        public void ResidentialDeepCleaning_PaysTheMidRate()
        {
            Assert.Equal(21m, OrderPricingCalculator.GetDefaultCleanerHourlyRate(DeepFee, "Residential Cleaning"));
        }

        /// <summary>
        /// A Custom ("Pre-Arranged") order labelled "Deep" carries NO deep-cleaning extra — that
        /// extra is deliberately filtered out of the custom extras grid — so the fee is 0 and the
        /// rate has to come off the NAME. It used to fall through to the $20 base and then warn
        /// the owner that his own correct $21 order was wrong (found in production, 2026-08).
        /// </summary>
        [Theory]
        [InlineData("Deep")]
        [InlineData("Deep Cleaning")]
        [InlineData("Super Deep Cleaning")]
        public void ADeepLabelWithNoDeepExtra_StillPaysTheMidRate(string serviceTypeName)
        {
            Assert.Equal(21m, OrderPricingCalculator.GetDefaultCleanerHourlyRate(NoDeepFee, serviceTypeName));
        }

        [Theory]
        [InlineData("Move In/Out Cleaning")]
        [InlineData("move-in-out cleaning")]
        [InlineData("Move Out Cleaning")]
        public void MoveInOut_PaysTheMidRate_EvenWithoutTheDeepExtra(string serviceTypeName)
        {
            Assert.Equal(21m, OrderPricingCalculator.GetDefaultCleanerHourlyRate(NoDeepFee, serviceTypeName));
        }

        [Theory]
        [InlineData("Heavy Condition Cleaning")]
        [InlineData("Heavy Conditional Cleaning")]
        [InlineData("Post Construction Cleaning")]
        [InlineData("post-construction cleaning")]
        public void HeavyConditionAndPostConstruction_PayTheTopRate(string serviceTypeName)
        {
            Assert.Equal(25m, OrderPricingCalculator.GetDefaultCleanerHourlyRate(NoDeepFee, serviceTypeName));
        }

        [Theory]
        [InlineData("Filthy Cleaning")]
        [InlineData("filthy")]
        public void Filthy_PaysTheHighestRate(string serviceTypeName)
        {
            Assert.Equal(28m, OrderPricingCalculator.GetDefaultCleanerHourlyRate(NoDeepFee, serviceTypeName));
        }

        /// <summary>
        /// Keyword order is part of the contract: a label naming BOTH heavy and filthy pays the
        /// filthy rate, because filthy is tested first.
        /// </summary>
        [Fact]
        public void Filthy_OutranksHeavy_WhenALabelNamesBoth()
        {
            Assert.Equal(28m, OrderPricingCalculator.GetDefaultCleanerHourlyRate(NoDeepFee, "Heavy / Filthy Cleaning"));
        }

        /// <summary>
        /// A name-matched rate wins over the deep-cleaning extra — a heavy-condition order with the
        /// deep extra attached must not fall back to the mid rate.
        /// </summary>
        [Fact]
        public void NameMatchedRate_TakesPrecedenceOverTheDeepCleaningExtra()
        {
            Assert.Equal(25m, OrderPricingCalculator.GetDefaultCleanerHourlyRate(DeepFee, "Heavy Condition Cleaning"));
            Assert.Equal(28m, OrderPricingCalculator.GetDefaultCleanerHourlyRate(DeepFee, "Filthy Cleaning"));
        }
    }
}
