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
    /// post construction 21, heavy 25, filthy 28.
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

        /// <summary>A custom order labelled "Deep" pays the deep rate off the label alone.</summary>
        [Fact]
        public void CustomOrderLabelledDeep_PaysTheMidRate()
        {
            Assert.Equal(21m, OrderPricingCalculator.GetDefaultCleanerHourlyRate(DeepFee, "Deep"));
        }

        [Theory]
        [InlineData("Move In/Out Cleaning")]
        [InlineData("move-in-out cleaning")]
        [InlineData("Move Out Cleaning")]
        public void MoveInOut_PaysTheMidRate_EvenWithoutTheDeepExtra(string serviceTypeName)
        {
            Assert.Equal(21m, OrderPricingCalculator.GetDefaultCleanerHourlyRate(NoDeepFee, serviceTypeName));
        }

        /// <summary>
        /// Post construction used to share the heavy-condition top rate. It pays the MID rate now
        /// (owner's call, 2026-08) — this test is the record of that, so a future "tidy-up" that
        /// folds it back in with heavy fails here instead of quietly overpaying every build-out job.
        /// </summary>
        [Theory]
        [InlineData("Post Construction Cleaning")]
        [InlineData("post-construction cleaning")]
        public void PostConstruction_PaysTheMidRate(string serviceTypeName)
        {
            Assert.Equal(21m, OrderPricingCalculator.GetDefaultCleanerHourlyRate(NoDeepFee, serviceTypeName));
        }

        [Theory]
        [InlineData("Heavy Condition Cleaning")]
        [InlineData("Heavy Conditional Cleaning")]
        public void HeavyCondition_PaysTheTopRate(string serviceTypeName)
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
