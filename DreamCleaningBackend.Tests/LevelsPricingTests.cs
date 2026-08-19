using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// The "Levels" service: stair pricing for a house.
    ///
    /// The model is DATA, not a calculator special case. Levels is an ordinary service row with
    /// ChargeAboveThreshold = true and ONE self-referencing ServiceThreshold row
    /// (ServiceId = SourceServiceId = the levels service, SourceQuantity 1, IncludedQuantity 1).
    /// The existing threshold machinery then produces billable = max(0, levels - 1) with no new
    /// branch anywhere in CalculateQuote.
    ///
    /// That is the whole point of these tests: they pin the CONTRACT the data relies on, so a
    /// future refactor of the threshold resolver cannot quietly change what a three-level house
    /// costs. The mirrored TypeScript assertions live in
    /// DreamCleaningNG/src/app/shared/pricing/levels-pricing.spec.ts.
    /// </summary>
    public class LevelsPricingTests
    {
        private const int BedroomsId = 1;
        private const int BathroomsId = 2;
        private const int SqftId = 3;

        // Deliberately NOT a seeded id. The levels service is created by raw SQL in
        // AddOrderPropertyTypeAndLevelsService with a database-assigned id, so no test may
        // assume a particular one.
        private const int LevelsId = 900;

        private const decimal LevelsCost = 35.00m;
        private const decimal LevelsMinutes = 25m;

        /// <summary>
        /// The levels line exactly as the migration seeds it, including the self-reference.
        /// </summary>
        private static OrderPricingCalculator.ServiceLineInput LevelsLine(int levels)
            => new()
            {
                ServiceId = LevelsId,
                Cost = LevelsCost,
                TimeDuration = LevelsMinutes,
                ServiceKey = PropertyDetailsHelper.LevelsServiceKey,
                Quantity = levels,
                ChargeAboveThreshold = true,
                // ZeroQuantityCost / ZeroQuantityDuration stay NULL. See
                // ZeroQuantityConfiguration_WouldHijackTheLevelsLine for why that matters.
                Thresholds =
                {
                    new OrderPricingCalculator.ThresholdInput
                    {
                        SourceServiceId = LevelsId,   // SELF-REFERENCE
                        SourceQuantity = 1,
                        IncludedQuantity = 1
                    }
                }
            };

        private static OrderPricingCalculator.QuoteInput WithLevels(
            int bedrooms, int bathrooms, int sqft, int? levels,
            params OrderPricingCalculator.ExtraServiceLineInput[] extras)
        {
            var input = new OrderPricingCalculator.QuoteInput
            {
                BasePrice = 90.00m,
                BaseDuration = 120m,
                MinimumPrice = 130.00m,
                Services =
                {
                    new OrderPricingCalculator.ServiceLineInput
                    {
                        ServiceId = BedroomsId, Cost = 22.50m, TimeDuration = 30m,
                        ServiceKey = "bedrooms", Quantity = bedrooms,
                        ZeroQuantityCost = 0.00m, ZeroQuantityDuration = 0.00m
                    },
                    new OrderPricingCalculator.ServiceLineInput
                    {
                        ServiceId = BathroomsId, Cost = 22.50m, TimeDuration = 30m,
                        ServiceKey = "bathrooms", Quantity = bathrooms
                    },
                    new OrderPricingCalculator.ServiceLineInput
                    {
                        ServiceId = SqftId, Cost = 0.18m, TimeDuration = 0.24m,
                        ServiceKey = "sqft", Quantity = sqft,
                        ChargeAboveThreshold = true,
                        Thresholds =
                        {
                            new OrderPricingCalculator.ThresholdInput
                                { SourceServiceId = BedroomsId, SourceQuantity = 0, IncludedQuantity = 400m },
                            new OrderPricingCalculator.ThresholdInput
                                { SourceServiceId = BedroomsId, SourceQuantity = 1, IncludedQuantity = 650m },
                            new OrderPricingCalculator.ThresholdInput
                                { SourceServiceId = BedroomsId, SourceQuantity = 2, IncludedQuantity = 850m },
                            new OrderPricingCalculator.ThresholdInput
                                { SourceServiceId = BedroomsId, SourceQuantity = 3, IncludedQuantity = 1000m },
                            new OrderPricingCalculator.ThresholdInput
                                { SourceServiceId = BedroomsId, SourceQuantity = 4, IncludedQuantity = 1500m }
                        },
                        RateTiers =
                        {
                            new OrderPricingCalculator.RateTierInput
                                { FromQuantity = 0m, Cost = 0.18m, TimeDuration = 0.24m },
                            new OrderPricingCalculator.RateTierInput
                                { FromQuantity = 400m, Cost = 0.135m, TimeDuration = 0.18m },
                            new OrderPricingCalculator.RateTierInput
                                { FromQuantity = 1200m, Cost = 0.11m, TimeDuration = 0.145m }
                        }
                    }
                },
                ExtraServices = extras.ToList()
            };

            if (levels.HasValue) input.Services.Add(LevelsLine(levels.Value));
            return input;
        }

        private static OrderPricingCalculator.ExtraServiceLineInput DeepCleaning()
            => new()
            {
                ExtraServiceId = 1, Name = "Deep Cleaning", Price = 90m, Duration = 120m,
                PriceMultiplier = 1.5m, IsDeepCleaning = true, Quantity = 1
            };

        private static OrderPricingCalculator.ServiceLineResult LevelsResult(
            OrderPricingCalculator.QuoteResult quote)
            => quote.ServiceLines.Single(l => l.ServiceId == LevelsId);

        // ── The pricing table ────────────────────────────────────────────────────────────

        /// <summary>
        /// The whole feature in one table. The first level is free, so a one-level house is
        /// priced identically to the equivalent apartment; each level after that adds one
        /// increment of cost and time.
        /// </summary>
        [Theory]
        [InlineData(1, 0.00, 0)]
        [InlineData(2, 35.00, 25)]
        [InlineData(3, 70.00, 50)]
        [InlineData(4, 105.00, 75)]
        public void Levels_ChargeOnlyAboveTheFirst(int levels, decimal expectedCost, decimal expectedMinutes)
        {
            var quote = OrderPricingCalculator.CalculateQuote(WithLevels(3, 2, 1600, levels));
            var line = LevelsResult(quote);

            Assert.Equal(expectedCost, line.Cost);
            Assert.Equal(expectedMinutes, line.Duration);
        }

        /// <summary>
        /// The stored quantity is the ACTUAL level count, never the decremented one. Reporting,
        /// the admin panel and the cleaner email all read the real number of levels; the "minus
        /// one" exists only inside the allowance arithmetic.
        /// </summary>
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        public void Levels_StoresTheActualCount_NotTheBillableCount(int levels)
        {
            var order = new Order();
            var quote = OrderPricingCalculator.CalculateQuote(WithLevels(3, 2, 1600, levels));

            OrderPricingCalculator.AddOrderLinesFromQuote(order, quote);

            var stored = order.OrderServices.Single(os => os.ServiceId == LevelsId);
            Assert.Equal(levels, stored.Quantity);
        }

        /// <summary>
        /// A one-level house must cost EXACTLY what the same home costs as an apartment. If this
        /// ever fails, we have started charging people for owning stairs they do not have.
        /// </summary>
        [Fact]
        public void OneLevelHouse_CostsTheSameAsAnApartment()
        {
            var apartment = OrderPricingCalculator.CalculateQuote(WithLevels(3, 2, 1600, null));
            var house = OrderPricingCalculator.CalculateQuote(WithLevels(3, 2, 1600, 1));

            Assert.Equal(apartment.SubTotal, house.SubTotal);
            Assert.Equal(apartment.TotalDuration, house.TotalDuration);
        }

        // ── The deep-cleaning multiplier ─────────────────────────────────────────────────

        /// <summary>
        /// Stair deep cleaning genuinely takes longer, so the cleaning-type multiplier DOES apply
        /// to the levels cost. It falls out of treating levels as an ordinary service line.
        ///
        /// Duration is NOT multiplied. No duration anywhere in this system is multiplier-scaled
        /// (see the note on getServiceDisplayDuration about bug B1): Deep Cleaning contributes
        /// its own minutes through its ExtraService row, and scaling service durations on top of
        /// that double-counted it. 3 levels deep is therefore $105.00 and 50 minutes, not 75.
        /// </summary>
        [Fact]
        public void DeepCleaning_ScalesLevelsCost_ButNeverLevelsDuration()
        {
            var quote = OrderPricingCalculator.CalculateQuote(
                WithLevels(3, 2, 1600, 3, DeepCleaning()));
            var line = LevelsResult(quote);

            Assert.Equal(105.00m, line.Cost);   // 35.00 x 2 x 1.5
            Assert.Equal(50m, line.Duration);   // 25 x 2, unscaled
        }

        /// <summary>
        /// The signed-off worked example, end to end: Residential, 3 bedrooms, 2 bathrooms,
        /// 1600 sq.ft, 3 levels, Deep Cleaning, against the production pricing configuration
        /// (admin export 2026-08-02). This is the number the feature was approved on.
        /// </summary>
        [Fact]
        public void WorkedExample_ThreeBedTwoBath1600SqftThreeLevelsDeep()
        {
            var quote = OrderPricingCalculator.CalculateQuote(
                WithLevels(3, 2, 1600, 3, DeepCleaning()));

            Assert.Equal(647.25m, quote.SubTotal);
            Assert.Equal(572m, quote.TotalDuration);
            Assert.Equal(572m, quote.DisplayDuration);
            Assert.Equal(1, quote.MaidsCount);
        }

        // ── The self-reference contract ──────────────────────────────────────────────────

        /// <summary>
        /// A self-referencing threshold is a SUPPORTED configuration, not an incidental one.
        ///
        /// The resolver reads the source service's quantity straight out of the same selection
        /// array and never resolves the source's OWN allowance, so pointing a service at itself
        /// terminates in one step. This test exists so that if anyone ever makes the resolver
        /// recursive - for example to let allowances chain - the suite fails here instead of the
        /// server hanging on the first house booking.
        ///
        /// It also asserts NO WARNING is produced: the "threshold source service was not present
        /// in the selection" warning fires when a source cannot be found, and a self-reference
        /// must never look like a missing source.
        /// </summary>
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        public void SelfReferencingThreshold_ResolvesTerminatesAndWarnsNothing(int levels)
        {
            var quote = OrderPricingCalculator.CalculateQuote(WithLevels(3, 2, 1600, levels));

            Assert.Empty(quote.Warnings);

            // billable = max(0, levels - 1), including the clamp at zero.
            var expectedBillable = System.Math.Max(0, levels - 1);
            Assert.Equal(LevelsCost * expectedBillable, LevelsResult(quote).Cost);
            Assert.Equal(LevelsMinutes * expectedBillable, LevelsResult(quote).Duration);
        }

        /// <summary>
        /// The floor match is what lets ONE threshold row cover every level count. Removing the
        /// row entirely must fall back to billing from zero, which is the behaviour that makes
        /// the single seeded row load-bearing rather than decorative.
        /// </summary>
        [Fact]
        public void WithoutTheThresholdRow_EveryLevelIsBilled()
        {
            var input = WithLevels(3, 2, 1600, 3);
            var levels = input.Services.Single(s => s.ServiceId == LevelsId);
            levels.Thresholds.Clear();

            var quote = OrderPricingCalculator.CalculateQuote(input);

            Assert.Equal(LevelsCost * 3, LevelsResult(quote).Cost);
        }

        // ── The studio guard ─────────────────────────────────────────────────────────────

        /// <summary>
        /// The studio rule must never touch the levels line.
        ///
        /// The plan assumed the zero-quantity check keys on serviceKey == "bedrooms". It does
        /// not. The FIRST zero-quantity branch is GENERIC - it fires for any service with a
        /// non-null ZeroQuantityCost or ZeroQuantityDuration - and the bedrooms-keyed branch
        /// below it is only the legacy fallback for when both are null.
        ///
        /// So the real invariant is not "levels is not called bedrooms", it is "the levels row
        /// leaves both zero-quantity columns NULL". This test pins the actual exposure by
        /// configuring one of them and asserting the line is then hijacked, which is what a
        /// future admin or migration would accidentally cause.
        /// </summary>
        [Fact]
        public void ZeroQuantityConfiguration_WouldHijackTheLevelsLine()
        {
            var input = WithLevels(3, 2, 1600, 0);
            var levels = input.Services.Single(s => s.ServiceId == LevelsId);
            levels.ZeroQuantityCost = 99.00m;

            var quote = OrderPricingCalculator.CalculateQuote(input);

            // Demonstrates the hazard: with a zero-quantity cost configured the threshold path is
            // skipped entirely. The migration therefore seeds both columns NULL.
            Assert.Equal(99.00m, LevelsResult(quote).Cost);
        }

        /// <summary>
        /// With the columns left NULL as seeded, a zero-quantity levels line prices at nothing
        /// through the threshold path and never reaches the legacy studio constants.
        /// </summary>
        [Fact]
        public void LevelsAtZero_IsFree_AndNeverPricedAsAStudio()
        {
            var quote = OrderPricingCalculator.CalculateQuote(WithLevels(3, 2, 1600, 0));
            var line = LevelsResult(quote);

            Assert.Equal(0m, line.Cost);
            Assert.Equal(0m, line.Duration);
            Assert.NotEqual(OrderPricingCalculator.StudioPrice, line.Cost);
        }

        /// <summary>
        /// The bedrooms studio rule still works with a levels line present. Adding a second
        /// threshold-driven service to the selection must not disturb it.
        /// </summary>
        [Fact]
        public void StudioBedrooms_StillPriceAsAStudio_WithLevelsPresent()
        {
            var input = WithLevels(0, 1, 400, 2);
            var quote = OrderPricingCalculator.CalculateQuote(input);

            var bedrooms = quote.ServiceLines.Single(l => l.ServiceId == BedroomsId);
            Assert.Equal(0m, bedrooms.Cost);       // configured ZeroQuantityCost 0.00
            Assert.Equal(35.00m, LevelsResult(quote).Cost);
        }
    }
}
