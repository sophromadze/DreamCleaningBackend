using DreamCleaningBackend.Services;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// What the Deep / Super Deep multipliers apply to (2026-10). Both the PRICE multiplier and the
    /// new TIME multiplier scale the base service only — the service type's base price / duration and
    /// the home lines (bedrooms, bathrooms, sq.ft, studio, levels). Extras keep their own price and
    /// minutes, and the deep extra's own fee and minutes are added once, flat. Mirrored case for case
    /// by shared/pricing/deep-cleaning-multiplier-scope.spec.ts, which asserts the same numbers.
    ///
    /// Numbers are the repository seed (base 120.00 / 90 min, bedrooms 25.00 / 30, bathrooms
    /// 35.00 / 45, Deep Cleaning 50.00 / 60 min at x1.5), so the arithmetic can be checked by hand.
    /// </summary>
    public class DeepCleaningMultiplierScopeTests
    {
        private const int BedroomsId = 1, BathroomsId = 2;

        private static OrderPricingCalculator.ServiceLineInput Service(
            int id, decimal cost, decimal minutes, string key, int quantity,
            decimal? zeroQuantityCost = null, decimal? zeroQuantityDuration = null)
            => new()
            {
                ServiceId = id, Cost = cost, TimeDuration = minutes, ServiceKey = key, Quantity = quantity,
                ZeroQuantityCost = zeroQuantityCost, ZeroQuantityDuration = zeroQuantityDuration
            };

        private static OrderPricingCalculator.ExtraServiceLineInput Deep(decimal timeMultiplier = 1m)
            => new()
            {
                ExtraServiceId = 1, Name = "Deep Cleaning", Price = 50m, Duration = 60m,
                PriceMultiplier = 1.5m, TimeMultiplier = timeMultiplier, IsDeepCleaning = true, Quantity = 1
            };

        private static OrderPricingCalculator.ExtraServiceLineInput SuperDeep()
            => new()
            {
                ExtraServiceId = 2, Name = "Super Deep Cleaning", Price = 100m, Duration = 120m,
                PriceMultiplier = 2.0m, TimeMultiplier = 1.5m, IsSuperDeepCleaning = true, Quantity = 1
            };

        /// <summary>Windows: $15 and 20 minutes per window.</summary>
        private static OrderPricingCalculator.ExtraServiceLineInput Windows(int count)
            => new()
            {
                ExtraServiceId = 4, Name = "Window Cleaning", Price = 15m, Duration = 20m,
                HasQuantity = true, Quantity = count
            };

        /// <summary>Organizing: $30 and 60 minutes per hour.</summary>
        private static OrderPricingCalculator.ExtraServiceLineInput Organizing(decimal hours)
            => new()
            {
                ExtraServiceId = 6, Name = "Organizing Service", Price = 30m, Duration = 60m,
                HasHours = true, Hours = hours, Quantity = 1
            };

        private static OrderPricingCalculator.ExtraServiceLineInput SameDay()
            => new()
            {
                ExtraServiceId = 3, Name = "Same Day Service", Price = 75m, Duration = 0m,
                IsSameDayService = true, Quantity = 1
            };

        /// <summary>Two bedrooms, one bathroom: home = 120 + 50 + 35 = $205.00 and 90 + 60 + 45 = 195 min.</summary>
        private static OrderPricingCalculator.QuoteInput Home(params OrderPricingCalculator.ExtraServiceLineInput[] extras)
            => new()
            {
                BasePrice = 120m,
                BaseDuration = 90m,
                Services =
                {
                    Service(BedroomsId, 25m, 30m, "bedrooms", 2),
                    Service(BathroomsId, 35m, 45m, "bathrooms", 1)
                },
                ExtraServices = extras.ToList()
            };

        [Fact]
        public void Regular_WithExtras_AddsTheExtrasAtTheirOwnPriceAndTime()
        {
            var quote = OrderPricingCalculator.CalculateQuote(Home(Windows(3)));

            Assert.Equal(250.00m, quote.SubTotal);      // 205 + 3 x 15
            Assert.Equal(255m, quote.TotalDuration);    // 195 + 3 x 20
            Assert.Equal(1m, quote.TimeMultiplier);
        }

        [Fact]
        public void Deep_WithoutExtras_IsExactlyTheOldPrice()
        {
            var quote = OrderPricingCalculator.CalculateQuote(Home(Deep()));

            Assert.Equal(357.50m, quote.SubTotal);      // 205 x 1.5 + 50
            Assert.Equal(255m, quote.TotalDuration);    // 195 + 60, time multiplier 1.0
        }

        [Fact]
        public void Deep_DoesNotMultiplyExtras()
        {
            var quote = OrderPricingCalculator.CalculateQuote(Home(Deep(), Windows(3)));

            // 205 x 1.5 = 307.50 home, + 45.00 windows (NOT 67.50), + 50.00 deep fee.
            Assert.Equal(402.50m, quote.SubTotal);
            Assert.Equal(45.00m, quote.ExtraServiceLines.Single(l => l.ExtraServiceId == 4).Cost);
            Assert.Equal(315m, quote.TotalDuration);    // 195 + 60 windows + 60 deep
        }

        [Fact]
        public void Deep_HourlyAndSameDayExtras_KeepTheirOwnPrice()
        {
            var quote = OrderPricingCalculator.CalculateQuote(Home(Deep(), Organizing(2), SameDay()));

            Assert.Equal(60.00m, quote.ExtraServiceLines.Single(l => l.ExtraServiceId == 6).Cost);
            Assert.Equal(75.00m, quote.ExtraServiceLines.Single(l => l.ExtraServiceId == 3).Cost);
            Assert.Equal(307.50m + 60m + 75m + 50m, quote.SubTotal);
            Assert.Equal(195m + 120m + 0m + 60m, quote.TotalDuration);
        }

        [Fact]
        public void TimeMultiplier_ScalesTheHomeMinutesOnly_RoundedPerLine()
        {
            var quote = OrderPricingCalculator.CalculateQuote(Home(Deep(timeMultiplier: 1.25m), Windows(3)));

            // base 90 x 1.25 = 112.5 -> 113, bedrooms 60 x 1.25 = 75, bathrooms 45 x 1.25 = 56.25 -> 56.
            Assert.Equal(75m, quote.ServiceLines.Single(l => l.ServiceId == BedroomsId).Duration);
            Assert.Equal(56m, quote.ServiceLines.Single(l => l.ServiceId == BathroomsId).Duration);
            // Windows keep 60 min, the deep extra keeps its own 60 min.
            Assert.Equal(60m, quote.ExtraServiceLines.Single(l => l.ExtraServiceId == 4).Duration);
            Assert.Equal(113m + 75m + 56m + 60m + 60m, quote.TotalDuration);
            Assert.Equal(1.25m, quote.TimeMultiplier);
            // The price is untouched by the time multiplier.
            Assert.Equal(402.50m, quote.SubTotal);
        }

        [Fact]
        public void SuperDeep_UsesItsOwnMultipliers_AndNeverMultipliesExtras()
        {
            var quote = OrderPricingCalculator.CalculateQuote(Home(SuperDeep(), Windows(3)));

            // 205 x 2.0 = 410 home, + 45 windows, + 100 super deep fee.
            Assert.Equal(555.00m, quote.SubTotal);
            // (90 x 1.5 = 135) + (60 x 1.5 = 90) + (45 x 1.5 = 67.5 -> 68) + 60 windows + 120 super deep.
            Assert.Equal(135m + 90m + 68m + 60m + 120m, quote.TotalDuration);
        }

        [Fact]
        public void SuperDeep_WinsOverDeep_ForBothMultipliers()
        {
            var extras = new[] { Deep(timeMultiplier: 1.25m), SuperDeep() };
            Assert.Equal(2.0m, OrderPricingCalculator.ResolvePriceMultiplier(extras).multiplier);
            Assert.Equal(1.5m, OrderPricingCalculator.ResolveTimeMultiplier(extras));
        }

        [Fact]
        public void Studio_ZeroQuantityMinutes_AreAHomeLine()
        {
            var input = Home(Deep(timeMultiplier: 1.5m));
            input.Services[0] = Service(BedroomsId, 25m, 30m, "bedrooms", 0, zeroQuantityCost: 20m, zeroQuantityDuration: 20m);
            var quote = OrderPricingCalculator.CalculateQuote(input);

            var studio = quote.ServiceLines.Single(l => l.ServiceId == BedroomsId);
            Assert.Equal(30.00m, studio.Cost);          // 20 x 1.5
            Assert.Equal(30m, studio.Duration);         // 20 x 1.5
        }

        [Fact]
        public void CleanerHours_AreTheCustomersChoice_NeverTimeMultiplied()
        {
            var input = new OrderPricingCalculator.QuoteInput
            {
                BasePrice = 0m,
                BaseDuration = 0m,
                Services =
                {
                    new() { ServiceId = 7, Cost = 40m, ServiceRelationType = "cleaner", ServiceKey = "cleaners", Quantity = 2 },
                    new() { ServiceId = 8, ServiceRelationType = "hours", ServiceKey = "hours", Quantity = 3 }
                },
                ExtraServices = { Deep(timeMultiplier: 1.5m) }
            };
            var quote = OrderPricingCalculator.CalculateQuote(input);

            Assert.Equal(40m * 1.5m * 2 * 3 + 50m, quote.SubTotal);
            Assert.Equal(180m, quote.DisplayDuration);
            Assert.Equal(180m, quote.TotalDuration);
        }

        [Theory]
        [InlineData(112.5, 1.0, 112.5)]   // 1.0 never rounds: regular quotes are untouched
        [InlineData(90, 1.25, 113)]       // 112.5 -> 113, half up
        [InlineData(45, 1.25, 56)]        // 56.25 -> 56
        [InlineData(45, 1.3, 59)]         // 58.5 -> 59
        public void ScaleHomeMinutes_RoundsHalfUpToWholeMinutes(double minutes, double multiplier, double expected)
        {
            Assert.Equal((decimal)expected, OrderPricingCalculator.ScaleHomeMinutes((decimal)minutes, (decimal)multiplier));
        }
    }
}
