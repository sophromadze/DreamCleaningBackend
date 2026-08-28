using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Services;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// NO-CONFIGURATION REGRESSION GUARD.
    ///
    /// The threshold/tier refactor ships with every new knob switched off:
    ///   ChargeAboveThreshold = false, no ServiceThreshold rows, no ServiceRateTier rows,
    ///   ZeroQuantityCost/Duration = null, ServiceType.MinimumPrice = 0.
    /// In that state the calculator MUST produce the same numbers the pre-refactor code did,
    /// so applying the schema migration cannot move a single price.
    ///
    /// Every expected value below was hand-derived from the PRE-refactor algorithm, not
    /// captured from the new code. Deriving them from the new code would make this file
    /// assert that the code equals itself.
    ///
    /// The catalog values used are the repository seed values (ServiceType 1 = 120/90,
    /// bedrooms 25/30, bathrooms 35/45, sqft 0.10/1) so the test is deterministic and
    /// independent of whatever any particular database currently holds.
    ///
    /// ONE INTENDED DIVERGENCE EXISTS. See StudioWithDeepCleaning_DurationDropsBy10Minutes_ThisIsBugFixB1.
    /// </summary>
    public class PricingNoConfigurationRegressionTests
    {
        private const int BedroomsId = 1;
        private const int BathroomsId = 2;
        private const int SqftId = 3;
        private const int CleanersId = 4;
        private const int HoursId = 5;

        /// <summary>A service exactly as an unconfigured row deserialises: all new fields inert.</summary>
        private static OrderPricingCalculator.ServiceLineInput Service(
            int id, decimal cost, decimal timeDuration, string key, int quantity,
            string? relationType = null)
            => new()
            {
                ServiceId = id,
                Cost = cost,
                TimeDuration = timeDuration,
                ServiceKey = key,
                Quantity = quantity,
                ServiceRelationType = relationType,
                // The four new knobs, in their shipped-off state.
                ChargeAboveThreshold = false,
                ZeroQuantityCost = null,
                ZeroQuantityDuration = null,
                RateTiers = new(),
                Thresholds = new()
            };

        /// <summary>
        /// Deep Cleaning exactly as the repository SEEDS it (ApplicationDbContext.cs,
        /// ExtraService Id 1): Price 50, Duration 60, PriceMultiplier 1.5. All three are seed
        /// values. Production currently runs $90.00 / 120 min instead — deliberately not tracked
        /// here, because this suite must be reproducible from the repository alone.
        /// </summary>
        private static OrderPricingCalculator.ExtraServiceLineInput DeepCleaning()
            => new()
            {
                ExtraServiceId = 1,
                Name = "Deep Cleaning",
                Price = 50m,
                Duration = 60m,
                PriceMultiplier = 1.5m,
                IsDeepCleaning = true,
                Quantity = 1
            };

        /// <summary>Residential selections with MinimumPrice deliberately left at 0.</summary>
        private static OrderPricingCalculator.QuoteInput Residential(
            int bedrooms, int bathrooms, int sqft,
            params OrderPricingCalculator.ExtraServiceLineInput[] extras)
            => new()
            {
                BasePrice = 120m,
                BaseDuration = 90m,
                MinimumPrice = 0m,
                Services =
                {
                    Service(BedroomsId,  25m,   30m, "bedrooms",  bedrooms),
                    Service(BathroomsId, 35m,   45m, "bathrooms", bathrooms),
                    Service(SqftId,      0.10m,  1m, "sqft",      sqft)
                },
                ExtraServices = extras.ToList()
            };

        // ── Ordinary services ────────────────────────────────────────────────────────────

        [Fact]
        public void TwoBedOneBath850Sqft_MatchesPreRefactorExactly()
        {
            // Pre-refactor derivation:
            //   base      120.00   /  90 min
            //   bedrooms   25 x 2 =  50.00  /  30 x 2 =  60 min
            //   bathrooms  35 x 1 =  35.00  /  45 x 1 =  45 min
            //   sqft     0.10 x 850 = 85.00 /   1 x 850 = 850 min   (billed from zero)
            //   subtotal  290.00              duration 1045 min
            var quote = OrderPricingCalculator.CalculateQuote(Residential(2, 1, 850));

            Assert.Equal(290.00m, quote.SubTotal);
            Assert.Equal(1045m, quote.TotalDuration);
            Assert.Equal(1045m, quote.DisplayDuration);
            Assert.Equal(1, quote.MaidsCount);

            // The new outputs must stay quiet when nothing is configured.
            Assert.False(quote.MinimumPriceApplied);
            Assert.Empty(quote.Warnings);
        }

        [Fact]
        public void PerLineCostsAndDurations_MatchPreRefactorExactly()
        {
            var quote = OrderPricingCalculator.CalculateQuote(Residential(2, 1, 850));

            var bedrooms = quote.ServiceLines.Single(l => l.ServiceId == BedroomsId);
            Assert.Equal(50.00m, bedrooms.Cost);
            Assert.Equal(60m, bedrooms.Duration);

            var sqft = quote.ServiceLines.Single(l => l.ServiceId == SqftId);
            Assert.Equal(85.00m, sqft.Cost);   // 0.10 x 850, NOT tiered
            Assert.Equal(850m, sqft.Duration);

            Assert.All(quote.ServiceLines, l => Assert.True(l.ShouldAddToOrder));
        }

        [Fact]
        public void DeepCleaningMultiplier_StillScalesCost_OnOrdinaryServices()
        {
            //   base      120 x 1.5 = 180.00  /  90 min
            //   bedrooms   25 x 2 x 1.5 = 75.00  /  60 min
            //   bathrooms  35 x 1 x 1.5 = 52.50  /  45 min
            //   sqft     0.10 x 850 x 1.5 = 127.50 / 850 min
            //   deep fee  +50.00 at the end       /  60 min
            //   subtotal  485.00                   duration 1105 min
            var quote = OrderPricingCalculator.CalculateQuote(Residential(2, 1, 850, DeepCleaning()));

            Assert.Equal(485.00m, quote.SubTotal);
            Assert.Equal(1105m, quote.TotalDuration);
            Assert.Equal(1.5m, quote.PriceMultiplier);
            Assert.Equal(50m, quote.DeepCleaningFee);
        }

        // ── Studio: the legacy fallback path ─────────────────────────────────────────────

        [Fact]
        public void Studio_WithNoZeroQuantityColumns_UsesLegacyFallback_MatchesPreRefactorExactly()
        {
            // With ZeroQuantityCost/Duration both null the generic zero-quantity branch is
            // skipped and the legacy StudioPrice/StudioDuration path runs, exactly as before.
            //   base      120.00 / 90 min
            //   studio     10.00 / 20 min     (StudioPrice 10, StudioDuration 20)
            //   bathrooms  35.00 / 45 min
            //   sqft       40.00 / 400 min    (0.10 x 400)
            //   subtotal  205.00   duration 555 min
            var quote = OrderPricingCalculator.CalculateQuote(Residential(0, 1, 400));

            Assert.Equal(205.00m, quote.SubTotal);
            Assert.Equal(555m, quote.TotalDuration);

            var studioLine = quote.ServiceLines.Single(l => l.ServiceId == BedroomsId);
            Assert.Equal(10.00m, studioLine.Cost);
            Assert.Equal(20m, studioLine.Duration);
        }

        [Fact]
        public void StudioWithDeepCleaning_DurationDropsBy10Minutes_ThisIsBugFixB1()
        {
            // THE ONE INTENDED DIVERGENCE FROM PRE-REFACTOR BEHAVIOUR.
            //
            // The old studio branch was the only place in the entire quote where a DURATION
            // was scaled by the cleaning-type multiplier:
            //     line.Duration = Math.Round(StudioDuration * priceMultiplier)  ->  Round(20 x 1.5) = 30
            // Every other service contributed unscaled minutes, so the per-service chips and the
            // summary disagreed. Bug B1 removes the multiplier from duration everywhere; Deep
            // Cleaning still contributes its own 60 minutes through its own ExtraService row.
            //
            //   PRE-REFACTOR : studio 30 min -> total 625 min
            //   NOW          : studio 20 min -> total 615 min
            //
            // COST is unaffected: the multiplier still applies to money.
            var quote = OrderPricingCalculator.CalculateQuote(Residential(0, 1, 400, DeepCleaning()));

            var studioLine = quote.ServiceLines.Single(l => l.ServiceId == BedroomsId);
            Assert.Equal(15.00m, studioLine.Cost);   // 10 x 1.5 — unchanged from pre-refactor
            Assert.Equal(20m, studioLine.Duration);  // was 30 — this is B1

            Assert.Equal(357.50m, quote.SubTotal);   // identical to pre-refactor
            Assert.Equal(615m, quote.TotalDuration); // pre-refactor produced 625
        }

        // ── Cleaner + hours service types ────────────────────────────────────────────────

        [Fact]
        public void CleanerHoursServiceType_Unchanged()
        {
            // Office Cleaning: base 200, 2 cleaners x 3 hours at 40/cleaner/hour.
            //   subtotal 200 + (40 x 2 x 3) = 440.00
            //   duration is the explicit hours: 3 x 60 = 180 min, per cleaner
            var input = new OrderPricingCalculator.QuoteInput
            {
                BasePrice = 200m,
                BaseDuration = 120m,
                MinimumPrice = 0m,
                Services =
                {
                    Service(CleanersId, 40m, 0m,  "cleaners", 2, relationType: "cleaner"),
                    Service(HoursId,     0m, 60m, "hours",    3, relationType: "hours")
                }
            };

            var quote = OrderPricingCalculator.CalculateQuote(input);

            Assert.Equal(440.00m, quote.SubTotal);
            Assert.Equal(180m, quote.TotalDuration);
            Assert.Equal(180m, quote.DisplayDuration);
            Assert.Equal(2, quote.MaidsCount);
            Assert.True(quote.HasCleanerService);

            // The hours line is folded into the cleaner line and must not be persisted.
            Assert.False(quote.ServiceLines.Single(l => l.ServiceId == HoursId).ShouldAddToOrder);
            Assert.True(quote.ServiceLines.Single(l => l.ServiceId == CleanersId).ShouldAddToOrder);
        }

        [Fact]
        public void CleanerSalaryRounding_Unchanged()
        {
            // Routed through the new shared DurationUtils helper in Nearest mode, which must be
            // identical to the old inline Math.Round(x, MidpointRounding.AwayFromZero).
            //   180 min / 30 = 6 exactly -> 180 min -> 3h x 2 maids x $20 = $120.00
            Assert.Equal(120.00m, OrderPricingCalculator.CalculateCleanerTotalSalary(180m, 2, true, 20m));

            // Half-way case: 75 min / 30 = 2.5 -> AwayFromZero -> 3 -> 90 min -> 1.5h x $20 = $30.00
            // (Banker's rounding would give 2 -> 60 min -> $20.00. That was the EmailService bug.)
            Assert.Equal(30.00m, OrderPricingCalculator.CalculateCleanerTotalSalary(75m, 1, false, 20m));
        }

        [Fact]
        public void RaisingMaidsCount_NeverRaisesCleanerSalary()
        {
            // The reported bug: a 456-minute order at $21/h paid $157.50 with 1 cleaner but
            // $168.00 with 2, because the per-cleaner share (228 min) was rounded to the NEAREST
            // increment (240) and then multiplied back by the cleaner count. Identical work,
            // +$10.50 for typing a 2 into the Maids field.
            Assert.Equal(157.50m, OrderPricingCalculator.CalculateCleanerTotalSalary(456m, 1, false, 21m));
            Assert.Equal(147.00m, OrderPricingCalculator.CalculateCleanerTotalSalary(456m, 2, false, 21m));

            // The label admins see is driven by the same function, so "3h 30m per cleaner"
            // (210 min) is exactly what the $147.00 is 2 x 3.5h of — no floored/nearest drift
            // between the number shown and the number paid.
            Assert.Equal(210m, OrderPricingCalculator.CalculatePerCleanerBillableMinutes(456m, 2, false));

            // The general guarantee, over the whole grid: for any total duration, raising the
            // cleaner count can only ever hold the payout flat or lower it. Skips the single
            // documented exception — a share below one increment is paid one increment rather
            // than $0, which is the only way the payout can go up.
            for (var total = 60m; total <= 1200m; total += 1m)
            {
                var atOne = OrderPricingCalculator.CalculateCleanerTotalSalary(total, 1, false, 21m);
                for (var maids = 2; maids <= 6; maids++)
                {
                    if (total / maids < OrderPricingCalculator.DurationRoundingMinutes) continue;

                    Assert.True(
                        OrderPricingCalculator.CalculateCleanerTotalSalary(total, maids, false, 21m) <= atOne,
                        $"{total} min across {maids} cleaners paid more than across 1");
                }
            }

            // The zero-pay guard itself: 6 cleaners on a 1h job is 10 min each, which floors to
            // nothing. One increment each is paid instead.
            Assert.Equal(30m, OrderPricingCalculator.CalculatePerCleanerBillableMinutes(60m, 6, false));
        }

        /// <summary>
        /// The per-cleaner share is cut from the total the admin can SEE, not from the raw
        /// stored minutes (2026-08).
        ///
        /// Reported against a real order: 710 stored minutes with 2 cleaners rendered
        /// "12h total · 5h 30m per cleaner". Neither half was wrong on its own — the 12h
        /// rounded 710 to the NEAREST increment (720), the share floored 710 / 2 = 355 down
        /// to 330 — but side by side they read as arithmetic that does not work, because
        /// halving the only total on screen gives 6h. The split now starts from the same
        /// rounded figure every surface prints, so the label survives being checked by hand.
        /// </summary>
        [Fact]
        public void PerCleanerSplit_DividesTheDisplayedTotal_NotTheRawMinutes()
        {
            // The total every surface shows for this order.
            Assert.Equal(720m, DurationUtils.RoundToIncrement(
                710m, OrderPricingCalculator.DurationRoundingMinutes, DurationRounding.Nearest));

            // ...and therefore the share, and the money it explains. Was 330m / $231.00.
            Assert.Equal(360m, OrderPricingCalculator.CalculatePerCleanerBillableMinutes(710m, 2, false));
            Assert.Equal(252.00m, OrderPricingCalculator.CalculateCleanerTotalSalary(710m, 2, false, 21m));

            // A single cleaner is unaffected: there is nothing to divide, and rounding the
            // total to the nearest increment is what that case always did.
            Assert.Equal(720m, OrderPricingCalculator.CalculatePerCleanerBillableMinutes(710m, 1, false));

            // An UNEVEN split still rounds DOWN — the owner chose clean half-hour labels over
            // exactness (2026-08). 11h30 across 2 cleaners is 5h30 each, not 5h45, so the
            // paid total sits half an hour under the displayed one. Deliberate, not drift.
            Assert.Equal(330m, OrderPricingCalculator.CalculatePerCleanerBillableMinutes(690m, 2, false));
            Assert.Equal(180m, OrderPricingCalculator.CalculatePerCleanerBillableMinutes(600m, 3, false));

            // The general guarantee that replaces exactness: over the whole grid the shares
            // never add up to MORE than the total on screen. Only the documented zero-pay
            // guard may exceed it, so shares that floor to nothing are skipped.
            for (var total = 60m; total <= 1200m; total += 1m)
            {
                var shown = DurationUtils.RoundToIncrement(
                    total, OrderPricingCalculator.DurationRoundingMinutes, DurationRounding.Nearest);

                for (var maids = 2; maids <= 6; maids++)
                {
                    if (shown / maids < OrderPricingCalculator.DurationRoundingMinutes) continue;

                    var paidFor = maids * OrderPricingCalculator.CalculatePerCleanerBillableMinutes(
                        total, maids, false);

                    Assert.True(paidFor <= shown,
                        $"{total} min shown as {shown} paid {maids} cleaners for {paidFor} min");
                }
            }
        }

        [Fact]
        public void ChatQuotedDuration_MatchesTheBookingPage()
        {
            // The chat agent and the booking page round the SAME quote.DisplayDuration. The chat
            // used Ceiling until 2026-08-11, so any raw value in the upper half of an increment
            // was quoted one increment higher than the page the customer then books on: a real
            // 2bd/1ba/1000sqft Deep Clean was quoted "about 6 hours 30 minutes" in chat and
            // rendered "6h" on /booking.
            //
            // Nearest is the booking page's mode (DurationUtils.formatDurationRounded ->
            // Math.round), so asserting Nearest here is asserting the two surfaces agree.
            Assert.Equal(360m, DurationUtils.RoundToIncrement(
                370m, OrderPricingCalculator.DurationRoundingMinutes, DurationRounding.Nearest));

            // Across the whole reported window (raw 361-374 all displayed as 6h on the page),
            // the chat must never quote 6h30m.
            for (var raw = 361m; raw <= 374m; raw += 1m)
            {
                Assert.Equal(360m, DurationUtils.RoundToIncrement(
                    raw, OrderPricingCalculator.DurationRoundingMinutes, DurationRounding.Nearest));
            }

            // Halves still go away from zero, matching JS Math.round on the page (375 -> 390).
            Assert.Equal(390m, DurationUtils.RoundToIncrement(
                375m, OrderPricingCalculator.DurationRoundingMinutes, DurationRounding.Nearest));

            // Cleaner-hours service types are untouched: TotalDuration is already per-cleaner
            // there (the customer picked cleaners x hours), so it still rounds to nearest and
            // scales with the count.
            Assert.Equal(126.00m, OrderPricingCalculator.CalculateCleanerTotalSalary(180m, 2, true, 21m));
        }

        // ── Proving each new knob is inert on its own ────────────────────────────────────

        [Fact]
        public void ChargeAboveThresholdTrue_WithNoThresholdRows_ChangesNothing()
        {
            // The flag alone must not alter pricing: with no threshold rows the allowance is 0,
            // so the whole quantity stays billable and no warning is raised.
            var input = Residential(2, 1, 850);
            input.Services.Single(s => s.ServiceId == SqftId).ChargeAboveThreshold = true;

            var quote = OrderPricingCalculator.CalculateQuote(input);

            Assert.Equal(290.00m, quote.SubTotal);
            Assert.Equal(1045m, quote.TotalDuration);
            Assert.Empty(quote.Warnings);
        }

        [Fact]
        public void MinimumPriceZero_AppliesNoFloor()
        {
            // Subtotal 205.00 sits well under any realistic floor; with MinimumPrice 0 it must
            // pass through untouched.
            var quote = OrderPricingCalculator.CalculateQuote(Residential(0, 1, 400));

            Assert.Equal(205.00m, quote.SubTotal);
            Assert.False(quote.MinimumPriceApplied);
        }

        [Fact]
        public void EmptyRateTiers_FallBackToFlatCostAndDuration()
        {
            // Explicitly asserts the fallback the whole no-config guarantee rests on:
            // sqft with no tiers prices at Cost x quantity, exactly as it always did.
            var quote = OrderPricingCalculator.CalculateQuote(Residential(3, 2, 1200));

            //   base 120 / 90 | bedrooms 75 / 90 | bathrooms 70 / 90 | sqft 120 / 1200
            //   subtotal 385.00, duration 1470
            Assert.Equal(385.00m, quote.SubTotal);
            Assert.Equal(1470m, quote.TotalDuration);
        }

        [Fact]
        public void TaxAndTotals_Unchanged()
        {
            var quote = OrderPricingCalculator.CalculateQuote(Residential(2, 1, 850));
            var totals = OrderPricingCalculator.CalculateTotals(new OrderPricingCalculator.TotalsInput
            {
                SubTotal = quote.SubTotal
            });

            // 290.00 x 0.08875 = 25.7375 -> 25.74 half-away-from-zero
            Assert.Equal(25.74m, totals.Tax);
            Assert.Equal(315.74m, totals.Total);
        }
    }
}
