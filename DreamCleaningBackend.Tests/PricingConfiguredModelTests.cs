using DreamCleaningBackend.Services;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// Behaviour of the threshold + tier model once a service type has been CONFIGURED through
    /// the admin panel. Nothing here is seeded by a migration — these values are what an admin
    /// enters (or imports) — so this suite is the only executable record of the intended
    /// pricing model.
    ///
    /// Configuration under test (the plan's sections 6.1-6.4):
    ///   ServiceType : BasePrice 90.00, TimeDuration 120, MinimumPrice 125.00
    ///   Bedrooms    : 22.50 / 30 min, ZeroQuantityCost 0.00, ZeroQuantityDuration 0.00
    ///   Bathrooms   : 22.50 / 30 min
    ///   Sq.ft       : ChargeAboveThreshold, thresholds 400/650/850/1000/1500/1800/2000,
    ///                 marginal tiers 0 -> 0.18/0.24, 400 -> 0.135/0.18, 1200 -> 0.11/0.145
    ///
    /// Only seed-defined service Ids (1 Bedrooms, 2 Bathrooms, 3 Sq.ft on ServiceType 1) are
    /// used. No test here assumes an Id for any admin-created service.
    /// </summary>
    public class PricingConfiguredModelTests
    {
        private const int BedroomsId = 1;
        private const int BathroomsId = 2;
        private const int SqftId = 3;

        private static readonly (int SourceQuantity, decimal Included)[] SqftThresholds =
        {
            (0, 400m), (1, 650m), (2, 850m), (3, 1000m), (4, 1500m), (5, 1800m), (6, 2000m)
        };

        private static readonly (decimal From, decimal Cost, decimal Minutes)[] SqftTiers =
        {
            (0m, 0.18m, 0.24m), (400m, 0.135m, 0.18m), (1200m, 0.11m, 0.145m)
        };

        /// <param name="configureZeroQuantity">
        /// False reproduces the un-configured legacy fallback (ZeroQuantity* null).
        /// </param>
        private static OrderPricingCalculator.QuoteInput Configured(
            int bedrooms, int bathrooms, int sqft,
            bool configureZeroQuantity = true,
            decimal minimumPrice = 125.00m,
            params OrderPricingCalculator.ExtraServiceLineInput[] extras)
            => new()
            {
                BasePrice = 90.00m,
                BaseDuration = 120m,
                MinimumPrice = minimumPrice,
                Services =
                {
                    new OrderPricingCalculator.ServiceLineInput
                    {
                        ServiceId = BedroomsId, Cost = 22.50m, TimeDuration = 30m,
                        ServiceKey = "bedrooms", Quantity = bedrooms,
                        ZeroQuantityCost     = configureZeroQuantity ? 0.00m : null,
                        ZeroQuantityDuration = configureZeroQuantity ? 0.00m : null
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
                        Thresholds = SqftThresholds.Select(t => new OrderPricingCalculator.ThresholdInput
                        {
                            SourceServiceId = BedroomsId,
                            SourceQuantity = t.SourceQuantity,
                            IncludedQuantity = t.Included
                        }).ToList(),
                        RateTiers = SqftTiers.Select(t => new OrderPricingCalculator.RateTierInput
                        {
                            FromQuantity = t.From, Cost = t.Cost, TimeDuration = t.Minutes
                        }).ToList()
                    }
                },
                ExtraServices = extras.ToList()
            };

        /// <summary>
        /// Deep Cleaning exactly as the repository SEEDS it: Price 50, Duration 60,
        /// PriceMultiplier 1.5 (ApplicationDbContext.cs, ExtraService Id 1). Every one of those
        /// three numbers is a seed value, not a placeholder chosen for the test.
        ///
        /// PRODUCTION DIFFERS — it currently runs Deep Cleaning at $90.00 / 120 min. That is
        /// deliberate and this test does NOT track it: the suite must be reproducible from the
        /// repository alone, so it asserts against the seed. The multiplier is the part that
        /// matters for these assertions, and it is 1.5 in both.
        /// </summary>
        private static OrderPricingCalculator.ExtraServiceLineInput DeepCleaning()
            => new()
            {
                ExtraServiceId = 1, Name = "Deep Cleaning", Price = 50m, Duration = 60m,
                PriceMultiplier = 1.5m, IsDeepCleaning = true, Quantity = 1
            };

        // ── The authoritative fixture table (plan section 1.4) ───────────────────────────

        [Theory]
        // bedrooms, sqft, expected subtotal, expected duration (minutes)
        [InlineData(0,  400, 125.00, 150)]   // floor applied; raw 112.50
        [InlineData(0,  650, 157.50, 210)]
        [InlineData(0,  900, 198.00, 264)]
        [InlineData(1,  650, 135.00, 180)]
        [InlineData(1,  900, 180.00, 240)]
        [InlineData(1, 1200, 227.25, 303)]
        [InlineData(2,  850, 157.50, 210)]
        [InlineData(2, 1000, 184.50, 246)]
        [InlineData(3, 1000, 180.00, 240)]
        [InlineData(3, 2400, 382.00, 509)]
        [InlineData(4, 1500, 202.50, 270)]
        [InlineData(4, 2500, 355.50, 474)]
        [InlineData(5, 1800, 225.00, 300)]
        [InlineData(6, 2000, 247.50, 330)]
        [InlineData(6, 3000, 400.50, 534)]
        // Tier-3-heavy additions (overage > 2000), exercising the top of the curve.
        [InlineData(0, 3000, 446.50, 593)]
        [InlineData(3, 4000, 558.00, 741)]
        [InlineData(6, 5000, 625.50, 831)]
        public void FixtureTable_OneBathroom_RegularCleaning(
            int bedrooms, int sqft, decimal expectedSubTotal, int expectedDuration)
        {
            var quote = OrderPricingCalculator.CalculateQuote(Configured(bedrooms, 1, sqft));

            Assert.Equal(expectedSubTotal, quote.SubTotal);
            Assert.Equal(expectedDuration, quote.DisplayDuration);
            Assert.Empty(quote.Warnings);
        }

        // ── Studio: the divergence, recorded against all three baselines ─────────────────

        [Fact]
        public void Studio_Configured_ContributesZeroMinutes_AndZeroCost()
        {
            // THE STUDIO DURATION CHANGE, IN FULL. The bedrooms line for a Studio contributes:
            //
            //   pre-refactor, Regular  : Round(StudioDuration 20 x 1.0) = 20 min
            //   pre-refactor, Deep     : Round(StudioDuration 20 x 1.5) = 30 min
            //   new code, unconfigured : 20 min for BOTH  (legacy fallback; B1 dropped the
            //                            multiplier, so Deep no longer differs)
            //   new code, configured   :  0 min for BOTH  (ZeroQuantityDuration = 0.00)
            //
            // So relative to what production does TODAY the configured studio is
            //   -20 min on a Regular booking and -30 min on a Deep booking,
            // and relative to the post-migration/pre-configuration state it is -20 min on both.
            //
            // This is intended: the correct Studio total is reached by base 120 + bathroom 30
            // = 150 min without the bedrooms line contributing anything.
            var regular = OrderPricingCalculator.CalculateQuote(Configured(0, 1, 400));
            var regularStudioLine = regular.ServiceLines.Single(l => l.ServiceId == BedroomsId);
            Assert.Equal(0m, regularStudioLine.Duration);
            Assert.Equal(0m, regularStudioLine.Cost);
            Assert.Equal(150m, regular.DisplayDuration);   // 120 base + 30 bathroom, nothing else

            var deep = OrderPricingCalculator.CalculateQuote(
                Configured(0, 1, 400, extras: DeepCleaning()));
            var deepStudioLine = deep.ServiceLines.Single(l => l.ServiceId == BedroomsId);
            Assert.Equal(0m, deepStudioLine.Duration);     // NOT 30 — the multiplier never
            Assert.Equal(0m, deepStudioLine.Cost);         // touches a zero-quantity line
        }

        [Fact]
        public void Studio_Unconfigured_StillUsesLegacyFallback_TwentyMinutes()
        {
            // Companion to the test above: until an admin fills in ZeroQuantityCost/Duration the
            // legacy StudioPrice/StudioDuration constants still apply, so a partially configured
            // service type behaves predictably rather than pricing a Studio at zero.
            var quote = OrderPricingCalculator.CalculateQuote(
                Configured(0, 1, 400, configureZeroQuantity: false));

            var studioLine = quote.ServiceLines.Single(l => l.ServiceId == BedroomsId);
            Assert.Equal(10m, studioLine.Cost);       // legacy StudioPrice
            Assert.Equal(20m, studioLine.Duration);   // legacy StudioDuration, unscaled (B1)
        }

        // ── Studio and the minimum-price floor ───────────────────────────────────────────

        [Fact]
        public void Studio_WithZeroQuantityCost_StillHitsTheMinimumPriceFloor()
        {
            //   base       90.00
            //   bedrooms    0.00  (ZeroQuantityCost)
            //   bathrooms  22.50
            //   sqft        0.00  (400 sqft, all of it inside the 400 included allowance)
            //   raw       112.50  ->  floored to 125.00
            //
            // The floor is what stops a configured Studio from being under-priced once the
            // bedrooms line stops contributing.
            var quote = OrderPricingCalculator.CalculateQuote(Configured(0, 1, 400));

            Assert.Equal(125.00m, quote.SubTotal);
            Assert.True(quote.MinimumPriceApplied);

            // Same selection with the floor removed proves 112.50 is the real raw subtotal.
            var unfloored = OrderPricingCalculator.CalculateQuote(
                Configured(0, 1, 400, minimumPrice: 0m));
            Assert.Equal(112.50m, unfloored.SubTotal);
            Assert.False(unfloored.MinimumPriceApplied);
        }

        [Fact]
        public void MinimumPrice_NotAppliedWhenSubtotalAlreadyExceedsIt()
        {
            // Studio at 650 sqft is 157.50 raw, comfortably above the 125.00 floor.
            var quote = OrderPricingCalculator.CalculateQuote(Configured(0, 1, 650));

            Assert.Equal(157.50m, quote.SubTotal);
            Assert.False(quote.MinimumPriceApplied);
        }

        [Fact]
        public void MinimumPrice_FloorsServicesOnly_ExtrasStackOnTop()
        {
            // The floor covers base + services; the deep-cleaning fee is added afterwards, so it
            // must NOT be absorbed. Studio @ 400 with Deep:
            //   base 90 x 1.5 = 135.00, bedrooms 0, bathrooms 22.50 x 1.5 = 33.75, sqft 0
            //   services subtotal 168.75 -> already above the 125.00 floor, so no floor
            //   + deep fee 50.00 -> 218.75
            var quote = OrderPricingCalculator.CalculateQuote(
                Configured(0, 1, 400, extras: DeepCleaning()));

            Assert.False(quote.MinimumPriceApplied);
            Assert.Equal(218.75m, quote.SubTotal);
        }

        // ── Downstream: what the studio change does to cleaner pay ───────────────────────

        [Fact]
        public void StudioPlusDeep_CleanerSalaryDropsByExactlyOneRoundingIncrement()
        {
            // CalculateCleanerTotalSalary derives pay from duration, so the 30 minutes the studio
            // line stops contributing on a Deep booking are 30 minutes the cleaner is no longer
            // paid for. Quantified here so the trade-off is on the record and testable.
            //
            // The drop is EXACTLY 30 billed minutes regardless of catalog. Salary rounds the
            // per-cleaner duration to the nearest 30-minute increment, and subtracting exactly one
            // full increment from the raw duration always shifts the rounded value by exactly one
            // increment — round((X-30)/30) == round(X/30) - 1 for every X. So:
            //
            //   30 min at the deep-cleaning rate of $21/h = $10.50 less per Studio + Deep booking.
            //
            // Raising the Deep Cleaning extra service's own Duration by 30 minutes restores it
            // exactly, and is admin-editable.
            //
            // NOTE: the Regular (non-deep) studio case is NOT symmetrical. It loses 20 raw minutes,
            // which is not a whole increment, so the billed delta is either 0 or -30 depending on
            // where the raw total happens to fall on the grid. See the assertions at the end.
            const decimal deepRate = OrderPricingCalculator.DeepCleaningCleanerHourlyRate; // 21

            // Target catalog: base 120 + studio + bath 30 + sqft 0 (inside allowance) + deep 120.
            const decimal preRefactorTotal = 120m + 30m + 30m + 0m + 120m;  // 300 min
            const decimal configuredTotal = 120m + 0m + 30m + 0m + 120m;    // 270 min

            var preRefactorSalary = OrderPricingCalculator.CalculateCleanerTotalSalary(
                preRefactorTotal, maidsCount: 1, hasCleanerService: false, deepRate);
            var configuredSalary = OrderPricingCalculator.CalculateCleanerTotalSalary(
                configuredTotal, maidsCount: 1, hasCleanerService: false, deepRate);

            Assert.Equal(105.00m, preRefactorSalary);   // 5.0h x $21
            Assert.Equal(94.50m, configuredSalary);     // 4.5h x $21
            Assert.Equal(-10.50m, configuredSalary - preRefactorSalary);

            // Same $10.50 under the production catalog (base 100, bath 30, sqft 0.01/unit,
            // deep 120), proving the delta is catalog-independent.
            var prodPre = OrderPricingCalculator.CalculateCleanerTotalSalary(284m, 1, false, deepRate);
            var prodCfg = OrderPricingCalculator.CalculateCleanerTotalSalary(254m, 1, false, deepRate);
            Assert.Equal(-10.50m, prodCfg - prodPre);

            // Regular studio loses 20 raw minutes, so the billed delta is grid-dependent.
            Assert.Equal(0m,
                OrderPricingCalculator.CalculateCleanerTotalSalary(234m, 1, false, 20m)
                - OrderPricingCalculator.CalculateCleanerTotalSalary(254m, 1, false, 20m));
            Assert.Equal(-10.00m,
                OrderPricingCalculator.CalculateCleanerTotalSalary(245m, 1, false, 20m)
                - OrderPricingCalculator.CalculateCleanerTotalSalary(265m, 1, false, 20m));
        }

        // ── Threshold and tier mechanics ─────────────────────────────────────────────────

        [Fact]
        public void SqftBelowThreshold_ContributesExactlyZero()
        {
            // 2 bedrooms includes 850; at 800 the customer is under the allowance entirely.
            var quote = OrderPricingCalculator.CalculateQuote(Configured(2, 1, 800));
            var sqftLine = quote.ServiceLines.Single(l => l.ServiceId == SqftId);

            Assert.Equal(0m, sqftLine.Cost);
            Assert.Equal(0m, sqftLine.Duration);
        }

        [Fact]
        public void SqftExactlyAtThreshold_ContributesExactlyZero()
        {
            var quote = OrderPricingCalculator.CalculateQuote(Configured(2, 1, 850));
            var sqftLine = quote.ServiceLines.Single(l => l.ServiceId == SqftId);

            Assert.Equal(0m, sqftLine.Cost);
            Assert.Equal(0m, sqftLine.Duration);
        }

        [Fact]
        public void OverageSpanningAllThreeTiers_SplitsMarginally_NotAtTheTopRate()
        {
            // 3 bedrooms includes 1000; at 2400 the overage is 1400, split 400 / 800 / 200:
            //   400 x 0.18  =  72.00      400 x 0.24  =  96 min
            //   800 x 0.135 = 108.00      800 x 0.18  = 144 min
            //   200 x 0.11  =  22.00      200 x 0.145 =  29 min
            //                 202.00                    269 min
            var quote = OrderPricingCalculator.CalculateQuote(Configured(3, 1, 2400));
            var sqftLine = quote.ServiceLines.Single(l => l.ServiceId == SqftId);

            Assert.Equal(202.00m, sqftLine.Cost);
            Assert.Equal(269m, sqftLine.Duration);

            // The whole point of marginal tiers: the top rate applied to everything would be
            // 1400 x 0.11 = 154.00, and the bottom rate would be 1400 x 0.18 = 252.00.
            Assert.NotEqual(154.00m, sqftLine.Cost);
            Assert.NotEqual(252.00m, sqftLine.Cost);
        }

        [Fact]
        public void BedroomsAboveHighestConfiguredThreshold_UsesTheHighestRow()
        {
            // 8 bedrooms has no row; the floor lookup falls back to the 6-bedroom row (2000).
            // At 2000 sqft that leaves zero overage.
            var quote = OrderPricingCalculator.CalculateQuote(Configured(8, 1, 2000));
            var sqftLine = quote.ServiceLines.Single(l => l.ServiceId == SqftId);

            Assert.Equal(0m, sqftLine.Cost);
            Assert.Empty(quote.Warnings);
        }

        [Fact]
        public void MissingThresholdSource_FallsBackToSmallestAllowance_AndWarns()
        {
            // If the bedrooms line is absent the source quantity is treated as 0, resolving to
            // the 400 allowance rather than to "no allowance". Failing the other way would bill
            // a large home from zero — a severe silent overcharge.
            var input = Configured(2, 1, 2000);
            input.Services.RemoveAll(s => s.ServiceId == BedroomsId);

            var quote = OrderPricingCalculator.CalculateQuote(input);
            var sqftLine = quote.ServiceLines.Single(l => l.ServiceId == SqftId);

            // Overage 1600 = 400 @ .18 + 800 @ .135 + 400 @ .11 = 72 + 108 + 44 = 224.00
            Assert.Equal(224.00m, sqftLine.Cost);

            var warning = Assert.Single(quote.Warnings);
            Assert.Contains("was not present in the selection", warning);
        }

        [Fact]
        public void DeepCleaningMultiplier_ScalesTieredCost_ButNeverTieredDuration()
        {
            var regular = OrderPricingCalculator.CalculateQuote(Configured(2, 1, 1000));
            var deep = OrderPricingCalculator.CalculateQuote(
                Configured(2, 1, 1000, extras: DeepCleaning()));

            var regularSqft = regular.ServiceLines.Single(l => l.ServiceId == SqftId);
            var deepSqft = deep.ServiceLines.Single(l => l.ServiceId == SqftId);

            Assert.Equal(27.00m, regularSqft.Cost);          // 150 overage x 0.18
            Assert.Equal(40.50m, deepSqft.Cost);             // x 1.5
            Assert.Equal(regularSqft.Duration, deepSqft.Duration);  // 36 min either way
            Assert.Equal(36m, deepSqft.Duration);
        }
    }
}
