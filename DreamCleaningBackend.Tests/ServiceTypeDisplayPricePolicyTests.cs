using DreamCleaningBackend.Helpers;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// ServiceType.DisplayPrice is marketing text for a type the calculator can't price (Filthy
    /// Cleaning). Amount and unit travel together, the unit is one of a fixed list the website knows
    /// how to word, and empty means "priced after assessment". Mirrored by the frontend's
    /// shared/pricing/display-price.ts.
    /// </summary>
    public class ServiceTypeDisplayPricePolicyTests
    {
        [Theory]
        [InlineData(ServiceTypeDisplayPricePolicy.PerHourPerCleaner)]
        [InlineData(ServiceTypeDisplayPricePolicy.PerHour)]
        [InlineData(ServiceTypeDisplayPricePolicy.From)]
        public void AcceptsAnAmountWithAKnownUnit(string unit)
        {
            var (amount, storedUnit, error) = ServiceTypeDisplayPricePolicy.Resolve(100m, $"  {unit} ");
            Assert.Null(error);
            Assert.Equal(100m, amount);
            Assert.Equal(unit, storedUnit);
        }

        [Fact]
        public void BothEmpty_MeansNoDisplayPrice()
        {
            Assert.Equal((null, null, null), ServiceTypeDisplayPricePolicy.Resolve(null, "  "));
        }

        [Theory]
        [InlineData(100, null)]
        [InlineData(null, "per-hour")]
        [InlineData(0, "per-hour")]
        [InlineData(-5, "per-hour")]
        [InlineData(100, "per hour")]
        [InlineData(100, "Per-Hour")]
        public void RefusesAnIncompleteOrUnknownValue(int? amount, string? unit)
        {
            var (storedAmount, storedUnit, error) =
                ServiceTypeDisplayPricePolicy.Resolve(amount, unit);
            Assert.NotNull(error);
            Assert.Null(storedAmount);
            Assert.Null(storedUnit);
        }

        [Fact]
        public void RefusesFractionsOfACent()
        {
            Assert.NotNull(ServiceTypeDisplayPricePolicy.Resolve(99.995m, "from").Error);
            Assert.Null(ServiceTypeDisplayPricePolicy.Resolve(99.95m, "from").Error);
        }
    }
}
