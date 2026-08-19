using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// The server-side guards around property type and levels.
    ///
    /// The booking endpoint accepts direct API calls, so every rule the booking page enforces in
    /// the UI has to exist here too. These tests run the REAL OrderPricingInputBuilder against a
    /// real ApplicationDbContext, because a hand-built QuoteInput would sail past a clamp that
    /// was never wired up.
    ///
    /// Also covers PropertyDetailsHelper, which is the single writer for Order.PropertyType and
    /// Order.LevelsQuantity. The invariant it exists to protect is that the display column and
    /// the OrderServices row can never disagree - if they could, the cleaner email would announce
    /// a different number of levels than the invoice charged for.
    /// </summary>
    public class PropertyTypeGuardTests : IDisposable
    {
        private const int ServiceTypeId = 1;
        private const int BedroomsId = 10;
        private const int BathroomsId = 20;
        private const int SqftId = 30;
        private const int LevelsId = 40;

        private readonly ApplicationDbContext _context;

        public PropertyTypeGuardTests()
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase($"property-type-guards-{Guid.NewGuid()}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;

            _context = new ApplicationDbContext(options);
            SeedCatalog();
        }

        public void Dispose() => _context.Dispose();

        /// <summary>
        /// Residential plus the levels row exactly as the migration seeds it: MinValue 1,
        /// MaxValue 4, ChargeAboveThreshold, both zero-quantity columns NULL, and the
        /// self-referencing threshold that makes the first level free.
        /// </summary>
        private void SeedCatalog()
        {
            _context.ServiceTypes.Add(new ServiceType
            {
                Id = ServiceTypeId, Name = "Residential Cleaning",
                BasePrice = 90m, TimeDuration = 120m, MinimumPrice = 130m, IsActive = true
            });

            _context.Services.AddRange(
                new Service
                {
                    Id = BedroomsId, Name = "Bedrooms", ServiceKey = "bedrooms",
                    Cost = 22.50m, TimeDuration = 30m, ServiceTypeId = ServiceTypeId,
                    InputType = "dropdown", IsActive = true, MinValue = 0, MaxValue = 6,
                    ZeroQuantityCost = 0m, ZeroQuantityDuration = 0m
                },
                new Service
                {
                    Id = BathroomsId, Name = "Bathrooms", ServiceKey = "bathrooms",
                    Cost = 22.50m, TimeDuration = 30m, ServiceTypeId = ServiceTypeId,
                    InputType = "dropdown", IsActive = true, MinValue = 1, MaxValue = 5
                },
                new Service
                {
                    Id = SqftId, Name = "Sq.ft", ServiceKey = "sqft",
                    Cost = 0.18m, TimeDuration = 0.24m, ServiceTypeId = ServiceTypeId,
                    InputType = "slider", IsActive = true, ChargeAboveThreshold = true
                },
                new Service
                {
                    Id = LevelsId, Name = "Levels",
                    ServiceKey = PropertyDetailsHelper.LevelsServiceKey,
                    Cost = 35m, TimeDuration = 25m, ServiceTypeId = ServiceTypeId,
                    InputType = "dropdown", IsActive = true,
                    MinValue = 1, MaxValue = 4, StepValue = 1, DisplayOrder = 4,
                    ChargeAboveThreshold = true
                    // ZeroQuantityCost / ZeroQuantityDuration deliberately left null.
                });

            var included = new[] { (0, 400m), (1, 650m), (2, 850m), (3, 1000m), (4, 1500m) };
            var thresholdId = 1;
            foreach (var (quantity, amount) in included)
            {
                _context.ServiceThresholds.Add(new ServiceThreshold
                {
                    Id = thresholdId++, ServiceId = SqftId, SourceServiceId = BedroomsId,
                    SourceQuantity = quantity, IncludedQuantity = amount
                });
            }

            // The self-referencing levels allowance.
            _context.ServiceThresholds.Add(new ServiceThreshold
            {
                Id = thresholdId, ServiceId = LevelsId, SourceServiceId = LevelsId,
                SourceQuantity = 1, IncludedQuantity = 1
            });

            var tiers = new[] { (0m, 0.18m, 0.24m), (400m, 0.135m, 0.18m), (1200m, 0.11m, 0.145m) };
            var tierId = 1;
            foreach (var (from, cost, minutes) in tiers)
            {
                _context.ServiceRateTiers.Add(new ServiceRateTier
                {
                    Id = tierId++, ServiceId = SqftId,
                    FromQuantity = from, Cost = cost, TimeDuration = minutes, DisplayOrder = tierId
                });
            }

            _context.SaveChanges();
        }

        private CreateBookingDto BookingDto(
            int bedrooms, int bathrooms, int sqft, int levels, string? propertyType) => new()
        {
            ServiceTypeId = ServiceTypeId,
            PropertyType = propertyType,
            Services = new List<BookingServiceDto>
            {
                new() { ServiceId = BedroomsId,  Quantity = bedrooms },
                new() { ServiceId = BathroomsId, Quantity = bathrooms },
                new() { ServiceId = SqftId,      Quantity = sqft },
                new() { ServiceId = LevelsId,    Quantity = levels }
            },
            ExtraServices = new List<BookingExtraServiceDto>()
        };

        private async Task<OrderPricingCalculator.QuoteInput> BuildAsync(
            int bedrooms, int bathrooms, int sqft, int levels, string? propertyType)
        {
            var serviceType = await _context.ServiceTypes.FirstAsync(st => st.Id == ServiceTypeId);
            return await OrderPricingInputBuilder.FromBookingDtoAsync(
                _context, serviceType,
                BookingDto(bedrooms, bathrooms, sqft, levels, propertyType),
                allowCustomPricing: false);
        }

        private static int LevelsQuantityOf(OrderPricingCalculator.QuoteInput input)
            => input.Services.Single(s => s.ServiceKey == PropertyDetailsHelper.LevelsServiceKey).Quantity;

        private static int BedroomsQuantityOf(OrderPricingCalculator.QuoteInput input)
            => input.Services.Single(s => s.ServiceKey == "bedrooms").Quantity;

        private static int SqftQuantityOf(OrderPricingCalculator.QuoteInput input)
            => input.Services.Single(s => s.ServiceKey == "sqft").Quantity;

        // ── Levels are only for houses ───────────────────────────────────────────────────

        /// <summary>
        /// An apartment can never carry a stair charge, no matter what the request says. The
        /// line is forced to the included count rather than deleted, so it still prices to
        /// exactly zero and the quote's lines stay a faithful image of the submission.
        /// </summary>
        [Theory]
        [InlineData(PropertyDetailsHelper.Apartment)]
        [InlineData(null)]
        [InlineData("Mansion")]
        [InlineData("")]
        public async Task NonHouse_CannotBuyLevels(string? propertyType)
        {
            var input = await BuildAsync(3, 2, 1600, levels: 4, propertyType);

            Assert.Equal(1, LevelsQuantityOf(input));

            var quote = OrderPricingCalculator.CalculateQuote(input);
            var line = quote.ServiceLines.Single(l => l.ServiceId == LevelsId);
            Assert.Equal(0m, line.Cost);
            Assert.Equal(0m, line.Duration);
        }

        /// <summary>A house keeps the level count it asked for and is charged for it.</summary>
        [Fact]
        public async Task House_KeepsItsLevels()
        {
            var input = await BuildAsync(3, 2, 1600, levels: 3, PropertyDetailsHelper.House);

            Assert.Equal(3, LevelsQuantityOf(input));

            var quote = OrderPricingCalculator.CalculateQuote(input);
            Assert.Equal(70m, quote.ServiceLines.Single(l => l.ServiceId == LevelsId).Cost);
        }

        /// <summary>Property type matching is case-insensitive; the stored value is canonical.</summary>
        [Theory]
        [InlineData("house")]
        [InlineData("HOUSE")]
        [InlineData("  House  ")]
        public async Task House_IsRecognisedRegardlessOfCasingOrPadding(string propertyType)
        {
            var input = await BuildAsync(3, 2, 1600, levels: 3, propertyType);
            Assert.Equal(3, LevelsQuantityOf(input));
            Assert.Equal(PropertyDetailsHelper.House, PropertyDetailsHelper.NormalizePropertyType(propertyType));
        }

        // ── The configured range ─────────────────────────────────────────────────────────

        /// <summary>
        /// A direct API call could otherwise submit 400 levels and be quoted a five-figure stair
        /// charge no UI could ever produce. The clamp reads the admin-configured MinValue and
        /// MaxValue off the service row, so raising the cap later is a data change.
        /// </summary>
        [Theory]
        [InlineData(400, 4)]
        [InlineData(5, 4)]
        [InlineData(0, 1)]
        [InlineData(-3, 1)]
        [InlineData(2, 2)]
        public async Task Levels_AreClampedToTheConfiguredRange(int submitted, int expected)
        {
            var input = await BuildAsync(3, 2, 1600, submitted, PropertyDetailsHelper.House);
            Assert.Equal(expected, LevelsQuantityOf(input));
        }

        /// <summary>
        /// The clamp is deliberately levels-only. Clamping every service to its configured range
        /// would change how bedrooms, sq.ft and cleaner-hours behave on paths that have always
        /// taken the raw value, which is a far bigger behavioural change than this feature earns.
        /// </summary>
        [Fact]
        public async Task OtherServices_AreNotRangeClamped()
        {
            var input = await BuildAsync(bedrooms: 9, bathrooms: 2, sqft: 1600,
                levels: 1, PropertyDetailsHelper.House);

            // Bedrooms MaxValue is 6, and the submitted 9 survives untouched.
            Assert.Equal(9, BedroomsQuantityOf(input));
        }

        // ── A house has no studio ────────────────────────────────────────────────────────

        /// <summary>
        /// Studio is neither selectable nor displayed once House is picked, so a studio house
        /// submitted directly to the API is raised to one bedroom. Without this, a caller could
        /// pay the studio's zero-quantity price for a property we have already decided has at
        /// least one bedroom.
        /// </summary>
        [Fact]
        public async Task House_CannotBeAStudio()
        {
            var input = await BuildAsync(bedrooms: 0, bathrooms: 1, sqft: 400,
                levels: 2, PropertyDetailsHelper.House);

            Assert.Equal(1, BedroomsQuantityOf(input));
        }

        /// <summary>
        /// Raising bedrooms 0 -> 1 must drag the included sq.ft up with it (400 -> 650), exactly
        /// as the booking page does. This is why the bedroom clamp runs BEFORE the sq.ft clamp:
        /// reversed, the customer would sit below their included allowance and be billed for
        /// square footage that should have been free.
        /// </summary>
        [Fact]
        public async Task House_StudioBump_AlsoRaisesTheIncludedSquareFeet()
        {
            var input = await BuildAsync(bedrooms: 0, bathrooms: 1, sqft: 400,
                levels: 2, PropertyDetailsHelper.House);

            Assert.Equal(1, BedroomsQuantityOf(input));
            Assert.Equal(650, SqftQuantityOf(input));
        }

        /// <summary>An apartment studio is still a studio. The bump is house-only.</summary>
        [Fact]
        public async Task Apartment_MayStillBeAStudio()
        {
            var input = await BuildAsync(bedrooms: 0, bathrooms: 1, sqft: 400,
                levels: 1, PropertyDetailsHelper.Apartment);

            Assert.Equal(0, BedroomsQuantityOf(input));
            Assert.Equal(400, SqftQuantityOf(input));
        }

        // ── PropertyDetailsHelper: the column can never disagree with the row ────────────

        /// <summary>
        /// THE invariant. The display column is derived from the same input the calculator priced
        /// and AddOrderLinesFromQuote persisted, so the two are the same number by construction.
        /// </summary>
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        public async Task StoredLevelsColumn_AlwaysMatchesTheStoredRow(int levels)
        {
            var input = await BuildAsync(3, 2, 1600, levels, PropertyDetailsHelper.House);
            var quote = OrderPricingCalculator.CalculateQuote(input);

            var order = new Order();
            OrderPricingCalculator.AddOrderLinesFromQuote(order, quote);
            PropertyDetailsHelper.Apply(order, PropertyDetailsHelper.House, input);

            var row = order.OrderServices.Single(os => os.ServiceId == LevelsId);
            Assert.Equal(row.Quantity, order.LevelsQuantity);
            Assert.Equal(PropertyDetailsHelper.House, order.PropertyType);
        }

        /// <summary>
        /// An apartment stores no level count even though the clamped line still exists in the
        /// quote. Null is what every read surface treats as "render no levels field".
        /// </summary>
        [Fact]
        public async Task Apartment_StoresNoLevelCount()
        {
            var input = await BuildAsync(3, 2, 1600, levels: 3, PropertyDetailsHelper.Apartment);

            var order = new Order();
            PropertyDetailsHelper.Apply(order, PropertyDetailsHelper.Apartment, input);

            Assert.Equal(PropertyDetailsHelper.Apartment, order.PropertyType);
            Assert.Null(order.LevelsQuantity);
        }

        /// <summary>
        /// Switching a house back to an apartment during an edit must clear the count, or the
        /// cleaner email keeps announcing stairs for a flat.
        /// </summary>
        [Fact]
        public void SwitchingHouseToApartment_ClearsTheLevelCount()
        {
            var order = new Order { PropertyType = PropertyDetailsHelper.House, LevelsQuantity = 3 };

            PropertyDetailsHelper.Apply(order, PropertyDetailsHelper.Apartment, null);

            Assert.Equal(PropertyDetailsHelper.Apartment, order.PropertyType);
            Assert.Null(order.LevelsQuantity);
        }

        /// <summary>
        /// Legacy orders stay legacy. An unknown or absent value normalizes to null rather than
        /// being stored, so the column can only ever hold one of the two known values.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("Duplex")]
        public void UnknownPropertyTypes_NormalizeToNull(string? raw)
        {
            Assert.Null(PropertyDetailsHelper.NormalizePropertyType(raw));
            Assert.False(PropertyDetailsHelper.IsHouse(raw));
        }

        // ── The admin path reads the row back ────────────────────────────────────────────

        /// <summary>
        /// SuperAdminFullUpdateOrder mutates OrderServices in place instead of re-pricing, so the
        /// helper reads the level count back off the row the admin just edited. Running it before
        /// that edit would store the pre-edit count and hand the crew a stale number.
        /// </summary>
        [Fact]
        public void AdminPath_ReadsTheLevelCountBackFromTheEditedRow()
        {
            var order = new Order
            {
                OrderServices = new List<Models.OrderService>
                {
                    new()
                    {
                        ServiceId = LevelsId, Quantity = 4,
                        Service = new Service
                        {
                            Id = LevelsId,
                            ServiceKey = PropertyDetailsHelper.LevelsServiceKey,
                            Name = "Levels", InputType = "dropdown"
                        }
                    }
                }
            };

            PropertyDetailsHelper.ApplyFromOrderLines(order, PropertyDetailsHelper.House);

            Assert.Equal(4, order.LevelsQuantity);
        }

        /// <summary>
        /// Null means NO CHANGE on the admin path, matching SuperAdminUpdateOrderDto's patch
        /// semantics. An admin editing only the service date must not strip the property type.
        /// </summary>
        [Fact]
        public void AdminPath_NullPropertyType_LeavesTheOrderAlone()
        {
            var order = new Order { PropertyType = PropertyDetailsHelper.House, LevelsQuantity = 3 };

            PropertyDetailsHelper.ApplyFromOrderLines(order, null);

            Assert.Equal(PropertyDetailsHelper.House, order.PropertyType);
            Assert.Equal(3, order.LevelsQuantity);
        }

        /// <summary>
        /// A partially loaded order must never erase a level count as a side effect. With the
        /// Service navigation missing, the levels line cannot be identified, so the count is left
        /// alone and only the property type is recorded.
        /// </summary>
        [Fact]
        public void AdminPath_UnloadedServiceNavigation_DoesNotEraseTheLevelCount()
        {
            var order = new Order
            {
                PropertyType = PropertyDetailsHelper.House,
                LevelsQuantity = 3,
                OrderServices = new List<Models.OrderService>
                {
                    new() { ServiceId = LevelsId, Quantity = 3, Service = null! }
                }
            };

            PropertyDetailsHelper.ApplyFromOrderLines(order, PropertyDetailsHelper.House);

            Assert.Equal(3, order.LevelsQuantity);
        }

        // ── Informational levels: no priced levels service ───────────────────────────────

        /// <summary>
        /// A service type with NO levels service still records the level count, on the column
        /// alone. Same precedent as Order.BedroomsQuantity / BathroomsQuantity, which are captured
        /// this way for cleaner+hours and custom modes and affect neither price nor duration.
        ///
        /// This is the case the first implementation got wrong: ResolveLevelsQuantity read only
        /// the priced line, so with no line the column was written null and every read surface
        /// silently showed nothing.
        /// </summary>
        [Fact]
        public void NoPricedLevelsService_StillRecordsTheCountOnTheColumn()
        {
            var order = new Order();
            var input = new OrderPricingCalculator.QuoteInput
            {
                BasePrice = 200m,
                BaseDuration = 120m,
                Services =
                {
                    new OrderPricingCalculator.ServiceLineInput
                    {
                        ServiceId = 500, Cost = 40m, TimeDuration = 0m,
                        ServiceKey = "cleaners", ServiceRelationType = "cleaner", Quantity = 2
                    },
                    new OrderPricingCalculator.ServiceLineInput
                    {
                        ServiceId = 501, Cost = 0m, TimeDuration = 60m,
                        ServiceKey = "hours", ServiceRelationType = "hours", Quantity = 3
                    }
                }
            };

            var quote = OrderPricingCalculator.CalculateQuote(input);
            OrderPricingCalculator.AddOrderLinesFromQuote(order, quote);
            PropertyDetailsHelper.Apply(order, PropertyDetailsHelper.House, input, requestedLevels: 3);

            Assert.Equal(PropertyDetailsHelper.House, order.PropertyType);
            Assert.Equal(3, order.LevelsQuantity);
            // ...and NOTHING priced: no levels line was created at all.
            Assert.DoesNotContain(order.OrderServices, os => os.ServiceId == LevelsId);
        }

        /// <summary>
        /// A priced line ALWAYS wins over the informational fallback, so a hand-rolled request
        /// cannot use it to overstate the level count on an order that was charged for fewer.
        /// </summary>
        [Fact]
        public async Task PricedLevelsLine_BeatsTheInformationalFallback()
        {
            var input = await BuildAsync(3, 2, 1600, levels: 2, PropertyDetailsHelper.House);

            var order = new Order();
            PropertyDetailsHelper.Apply(order, PropertyDetailsHelper.House, input, requestedLevels: 4);

            Assert.Equal(2, order.LevelsQuantity);
        }

        /// <summary>The informational count is clamped, since it never passes the calculator.</summary>
        [Theory]
        [InlineData(400, 4)]
        [InlineData(0, 1)]
        [InlineData(-2, 1)]
        [InlineData(3, 3)]
        public void InformationalLevels_AreClamped(int requested, int expected)
        {
            Assert.Equal(expected, PropertyDetailsHelper.ClampInformationalLevels(requested));
        }

        /// <summary>An apartment records no count even on an unpriced service type.</summary>
        [Fact]
        public void NoPricedLevelsService_ApartmentStillRecordsNothing()
        {
            var order = new Order();

            PropertyDetailsHelper.Apply(
                order, PropertyDetailsHelper.Apartment, null, requestedLevels: 3);

            Assert.Equal(PropertyDetailsHelper.Apartment, order.PropertyType);
            Assert.Null(order.LevelsQuantity);
        }

        /// <summary>
        /// CONFIRMS point (g): the custom ("Pre-Arranged") flow needs no branch of its own. Its
        /// quote input carries no services at all, so it IS the informational case - property type
        /// is recorded, the level count lands on the column, and the admin-entered amount is
        /// untouched.
        /// </summary>
        [Fact]
        public void CustomPricing_RecordsPropertyTypeAndInformationalLevels_WithoutChangingTheAmount()
        {
            var input = new OrderPricingCalculator.QuoteInput
            {
                BasePrice = 0m,
                BaseDuration = 0m,
                IsCustomPricing = true,
                CustomAmount = 300m,
                CustomCleaners = 2,
                CustomDuration = 240m
            };

            var quote = OrderPricingCalculator.CalculateQuote(input);
            var order = new Order();
            OrderPricingCalculator.AddOrderLinesFromQuote(order, quote);
            PropertyDetailsHelper.Apply(order, PropertyDetailsHelper.House, input, requestedLevels: 4);

            Assert.Equal(PropertyDetailsHelper.House, order.PropertyType);
            Assert.Equal(4, order.LevelsQuantity);
            Assert.Empty(order.OrderServices);
            // The tax-inclusive split of the admin-entered amount is unchanged by any of this.
            Assert.Equal(300m, quote.SubTotal + (quote.TaxOverride ?? 0m));
        }
    }
}
