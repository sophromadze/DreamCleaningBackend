using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// GUARD AGAINST FIELD-DROPPING IN THE INPUT BUILDER.
    ///
    /// This is the test that should have existed from the start. OrderPricingInputBuilder is the
    /// only adapter between the database and the calculator on the server, and it is hand-written
    /// field-by-field — so a column added to Service but not mapped here silently reverts the
    /// ENTIRE backend to the previous pricing model while the frontend uses the new one. That is
    /// exactly what happened: ChargeAboveThreshold, ZeroQuantityCost, ZeroQuantityDuration,
    /// RateTiers, Thresholds and MinimumPrice were all absent, so every backend quote priced sqft
    /// from unit one at a flat rate, skipped the price floor, and used the legacy Studio constants.
    ///
    /// These tests exercise the REAL builder against a real ApplicationDbContext (in-memory
    /// provider) — never a hand-constructed QuoteInput. A hand-built input would have passed
    /// happily while production was mispriced.
    ///
    /// Note on the in-memory provider: it ignores Include() and returns navigations from its
    /// change tracker, so it cannot prove the eager-loading is correct. The assertions below are
    /// therefore about MAPPING completeness. The eager-load itself is covered by the fact that
    /// lazy loading is disabled — with no Include and a fresh context, the collections would be
    /// empty against MariaDB.
    /// </summary>
    public class OrderPricingInputBuilderTests : IDisposable
    {
        private const int ServiceTypeId = 1;
        private const int BedroomsId = 10;
        private const int BathroomsId = 20;
        private const int SqftId = 30;

        private readonly ApplicationDbContext _context;

        public OrderPricingInputBuilderTests()
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase($"pricing-input-builder-{Guid.NewGuid()}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;

            _context = new ApplicationDbContext(options);
            SeedConfiguredCatalog();
        }

        public void Dispose() => _context.Dispose();

        /// <summary>Mirrors the shipped Residential configuration: floor, allowances, three tiers.</summary>
        private void SeedConfiguredCatalog()
        {
            _context.ServiceTypes.Add(new ServiceType
            {
                Id = ServiceTypeId,
                Name = "Residential Cleaning",
                BasePrice = 90m,
                TimeDuration = 120m,
                MinimumPrice = 130m,
                IsActive = true
            });

            _context.Services.AddRange(
                new Service
                {
                    Id = BedroomsId, Name = "Bedrooms", ServiceKey = "bedrooms",
                    Cost = 22.50m, TimeDuration = 30m, ServiceTypeId = ServiceTypeId,
                    InputType = "dropdown", IsActive = true,
                    ZeroQuantityCost = 0m, ZeroQuantityDuration = 0m
                },
                new Service
                {
                    Id = BathroomsId, Name = "Bathrooms", ServiceKey = "bathrooms",
                    Cost = 22.50m, TimeDuration = 30m, ServiceTypeId = ServiceTypeId,
                    InputType = "dropdown", IsActive = true
                },
                new Service
                {
                    Id = SqftId, Name = "Sq.ft", ServiceKey = "sqft",
                    Cost = 0.18m, TimeDuration = 0.24m, ServiceTypeId = ServiceTypeId,
                    InputType = "slider", IsActive = true,
                    ChargeAboveThreshold = true
                });

            var included = new[] { (0, 400m), (1, 650m), (2, 850m), (3, 1000m), (4, 1500m), (5, 1800m), (6, 2000m) };
            var thresholdId = 1;
            foreach (var (quantity, amount) in included)
            {
                _context.ServiceThresholds.Add(new ServiceThreshold
                {
                    Id = thresholdId++,
                    ServiceId = SqftId,
                    SourceServiceId = BedroomsId,
                    SourceQuantity = quantity,
                    IncludedQuantity = amount
                });
            }

            var tiers = new[] { (0m, 0.18m, 0.24m), (400m, 0.135m, 0.18m), (1200m, 0.11m, 0.145m) };
            var tierId = 1;
            foreach (var (from, cost, minutes) in tiers)
            {
                _context.ServiceRateTiers.Add(new ServiceRateTier
                {
                    Id = tierId++,
                    ServiceId = SqftId,
                    FromQuantity = from,
                    Cost = cost,
                    TimeDuration = minutes,
                    DisplayOrder = tierId
                });
            }

            _context.SaveChanges();
        }

        private static CreateBookingDto BookingDto(int bedrooms, int bathrooms, int sqft) => new()
        {
            ServiceTypeId = ServiceTypeId,
            Services = new List<BookingServiceDto>
            {
                new() { ServiceId = BedroomsId,  Quantity = bedrooms },
                new() { ServiceId = BathroomsId, Quantity = bathrooms },
                new() { ServiceId = SqftId,      Quantity = sqft }
            },
            ExtraServices = new List<BookingExtraServiceDto>()
        };

        private async Task<OrderPricingCalculator.QuoteInput> BuildAsync(int bedrooms, int bathrooms, int sqft)
        {
            var serviceType = await _context.ServiceTypes.FirstAsync(st => st.Id == ServiceTypeId);
            return await OrderPricingInputBuilder.FromBookingDtoAsync(
                _context, serviceType, BookingDto(bedrooms, bathrooms, sqft), allowCustomPricing: false);
        }

        // ── The five fields + the floor ──────────────────────────────────────────────────

        [Fact]
        public async Task Builder_CarriesEveryNewFieldThrough_NoneDefaultOrEmpty()
        {
            var input = await BuildAsync(bedrooms: 2, bathrooms: 1, sqft: 1000);

            // 1. MinimumPrice — dropping this removes the price floor from every backend path.
            Assert.Equal(130m, input.MinimumPrice);

            var sqft = input.Services.Single(s => s.ServiceKey == "sqft");

            // 2. ChargeAboveThreshold — false here means sqft bills from unit one.
            Assert.True(sqft.ChargeAboveThreshold);

            // 3. Thresholds — empty here means no allowance, i.e. the whole area is billable.
            Assert.NotEmpty(sqft.Thresholds);
            Assert.Equal(7, sqft.Thresholds.Count);
            Assert.All(sqft.Thresholds, t => Assert.Equal(BedroomsId, t.SourceServiceId));
            Assert.Equal(850m, sqft.Thresholds.Single(t => t.SourceQuantity == 2).IncludedQuantity);

            // 4. RateTiers — empty here means a flat rate across the whole quantity.
            Assert.NotEmpty(sqft.RateTiers);
            Assert.Equal(3, sqft.RateTiers.Count);
            Assert.Equal(new[] { 0m, 400m, 1200m }, sqft.RateTiers.Select(t => t.FromQuantity).ToArray());
            // 0.135 and 0.145 are the values a decimal(18,2) column would have destroyed.
            Assert.Equal(0.135m, sqft.RateTiers.Single(t => t.FromQuantity == 400m).Cost);
            Assert.Equal(0.145m, sqft.RateTiers.Single(t => t.FromQuantity == 1200m).TimeDuration);

            // 5/6. ZeroQuantity* — null here sends Studio back to the legacy $10 / 20 min constants.
            var bedroomsLine = input.Services.Single(s => s.ServiceKey == "bedrooms");
            Assert.Equal(0m, bedroomsLine.ZeroQuantityCost);
            Assert.Equal(0m, bedroomsLine.ZeroQuantityDuration);

            // A service with nothing configured must still map as "not applicable", not as 0.
            var bathrooms = input.Services.Single(s => s.ServiceKey == "bathrooms");
            Assert.Null(bathrooms.ZeroQuantityCost);
            Assert.Null(bathrooms.ZeroQuantityDuration);
            Assert.False(bathrooms.ChargeAboveThreshold);
            Assert.Empty(bathrooms.RateTiers);
        }

        [Fact]
        public async Task Builder_ProducesTheConfiguredPrice_NotTheLegacyOne()
        {
            // End-to-end through the real builder: 2 bed / 1 bath / 1000 sqft.
            //   base 90 + bedrooms 45 + bathrooms 22.50 + sqft (150 overage x 0.18 = 27) = 184.50
            // Under the pre-fix builder this was 90 + 45 + 22.50 + (1000 x 0.18 = 180) = 337.50,
            // with no floor and Studio at the legacy constants.
            var quote = OrderPricingCalculator.CalculateQuote(await BuildAsync(2, 1, 1000));

            Assert.Equal(184.50m, quote.SubTotal);
            Assert.Equal(246m, quote.DisplayDuration);
        }

        [Fact]
        public async Task Builder_AppliesTheMinimumPriceFloor()
        {
            // Studio @ 400: 90 + 0 + 22.50 + 0 = 112.50 raw, floored to 130.00.
            var quote = OrderPricingCalculator.CalculateQuote(await BuildAsync(0, 1, 400));

            Assert.Equal(130.00m, quote.SubTotal);
            Assert.True(quote.MinimumPriceApplied);
        }

        [Fact]
        public async Task Builder_UsesConfiguredZeroQuantityValues_NotLegacyStudioConstants()
        {
            var quote = OrderPricingCalculator.CalculateQuote(await BuildAsync(0, 1, 400));
            var studioLine = quote.ServiceLines.Single(l => l.ServiceId == BedroomsId);

            Assert.Equal(0m, studioLine.Cost);      // legacy fallback would be 10
            Assert.Equal(0m, studioLine.Duration);  // legacy fallback would be 20
        }

        // ── The clamp must follow configured thresholds, not the hardcoded table ─────────

        [Fact]
        public async Task Clamp_RaisesSqftToTheConfiguredAllowance()
        {
            // 3 bedrooms includes 1000. A request for 600 must be clamped up to 1000, leaving
            // zero overage — never left at 600, which would bill below the included amount.
            var input = await BuildAsync(bedrooms: 3, bathrooms: 1, sqft: 600);
            var sqft = input.Services.Single(s => s.ServiceKey == "sqft");

            Assert.Equal(1000, sqft.Quantity);

            var quote = OrderPricingCalculator.CalculateQuote(input);
            Assert.Equal(0m, quote.ServiceLines.Single(l => l.ServiceId == SqftId).Cost);
        }

        [Fact]
        public async Task Clamp_LeavesQuantitiesAboveTheAllowanceAlone()
        {
            var input = await BuildAsync(bedrooms: 2, bathrooms: 1, sqft: 1700);
            Assert.Equal(1700, input.Services.Single(s => s.ServiceKey == "sqft").Quantity);
        }

        [Fact]
        public async Task Clamp_IgnoresThresholdsBelongingToAnotherSourceService()
        {
            // A stray allowance keyed to some other source must not drive the sqft minimum.
            _context.ServiceThresholds.Add(new ServiceThreshold
            {
                Id = 999, ServiceId = SqftId, SourceServiceId = BathroomsId,
                SourceQuantity = 1, IncludedQuantity = 99999m
            });
            await _context.SaveChangesAsync();

            var input = await BuildAsync(bedrooms: 2, bathrooms: 1, sqft: 900);
            Assert.Equal(900, input.Services.Single(s => s.ServiceKey == "sqft").Quantity);
        }

        // ── Order-edit path ─────────────────────────────────────────────────────────────

        [Fact]
        public async Task UpdatePath_LoadsMinimumPrice_EvenWhenServiceTypeNavigationIsNotLoaded()
        {
            // The edit path receives an Order whose ServiceType navigation may be null. Falling
            // back to 0 would silently drop the floor on every order edit.
            var order = new Order
            {
                Id = 500,
                ServiceTypeId = ServiceTypeId,
                ServiceType = null,
                OrderServices = new List<Models.OrderService>()
            };

            var dto = new UpdateOrderDto
            {
                Services = new List<BookingServiceDto>
                {
                    new() { ServiceId = BedroomsId,  Quantity = 0 },
                    new() { ServiceId = BathroomsId, Quantity = 1 },
                    new() { ServiceId = SqftId,      Quantity = 400 }
                },
                ExtraServices = new List<BookingExtraServiceDto>()
            };

            var input = await OrderPricingInputBuilder.FromUpdateDtoAsync(_context, order, dto);

            Assert.Equal(130m, input.MinimumPrice);
            Assert.Equal(90m, input.BasePrice);
            Assert.Equal(120m, input.BaseDuration);

            var quote = OrderPricingCalculator.CalculateQuote(input);
            Assert.Equal(130.00m, quote.SubTotal);
            Assert.True(quote.MinimumPriceApplied);
        }

        [Fact]
        public async Task UpdatePath_CarriesThresholdsAndTiersThrough()
        {
            var order = new Order
            {
                Id = 501,
                ServiceTypeId = ServiceTypeId,
                OrderServices = new List<Models.OrderService>()
            };

            var dto = new UpdateOrderDto
            {
                Services = new List<BookingServiceDto>
                {
                    new() { ServiceId = BedroomsId,  Quantity = 3 },
                    new() { ServiceId = BathroomsId, Quantity = 1 },
                    new() { ServiceId = SqftId,      Quantity = 2400 }
                },
                ExtraServices = new List<BookingExtraServiceDto>()
            };

            var input = await OrderPricingInputBuilder.FromUpdateDtoAsync(_context, order, dto);
            var sqft = input.Services.Single(s => s.ServiceKey == "sqft");

            Assert.True(sqft.ChargeAboveThreshold);
            Assert.Equal(7, sqft.Thresholds.Count);
            Assert.Equal(3, sqft.RateTiers.Count);

            // 1400 overage split 400/800/200 = 72.00 + 108.00 + 22.00 = 202.00
            var quote = OrderPricingCalculator.CalculateQuote(input);
            Assert.Equal(202.00m, quote.ServiceLines.Single(l => l.ServiceId == SqftId).Cost);
        }
    }
}
