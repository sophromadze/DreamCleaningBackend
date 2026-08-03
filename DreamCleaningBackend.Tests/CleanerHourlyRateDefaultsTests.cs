using DreamCleaningBackend.Services;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// Default cleaner pay rates per service type. The rule is name-based (the effective,
    /// human-facing service-type name — the custom "Pre-Arranged" label when there is one), so
    /// these tests pin the keyword matching against the names that actually exist in the catalog.
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
        [InlineData("Filthy Cleaning")]
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

        /// <summary>
        /// The top rate wins over the deep-cleaning extra — a heavy-condition order with the deep
        /// extra attached must not fall back to the mid rate.
        /// </summary>
        [Fact]
        public void TopRate_TakesPrecedenceOverTheDeepCleaningExtra()
        {
            Assert.Equal(25m, OrderPricingCalculator.GetDefaultCleanerHourlyRate(DeepFee, "Heavy Condition Cleaning"));
        }
    }
}
