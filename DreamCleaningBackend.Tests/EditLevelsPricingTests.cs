using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Repositories;
using DreamCleaningBackend.Services;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// 2026-10: an order booked as an APARTMENT has no levels line (booking only adds one when a
    /// house's level chip is clicked). Switching it to a house in the admin editor used to leave
    /// the level count "informational", because SuperAdminFullUpdateOrder could only update rows
    /// that already existed. The editor now adds the priced levels row and this endpoint persists
    /// it. Every price here is asserted against BOOKING's own server path for the same inputs
    /// (OrderPricingInputBuilder.FromBookingDtoAsync -> CalculateQuote), not against a constant.
    ///
    /// The customer order edit re-prices server-side (FromUpdateDtoAsync) and is asserted the same
    /// way, so all three surfaces agree on what a three-level house costs.
    /// </summary>
    public class EditLevelsPricingTests : IDisposable
    {
        private const int ResidentialId = 1, HeavyId = 6;
        private const int BedroomsId = 10, BathroomsId = 20, SqftId = 30, LevelsId = 40;
        private const int HeavyBedroomsId = 61, HeavyBathroomsId = 62, HeavySqftId = 63;

        private readonly ApplicationDbContext _db;

        public EditLevelsPricingTests()
        {
            _db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase($"edit-levels-{Guid.NewGuid()}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
            Seed();
        }

        public void Dispose() => _db.Dispose();

        /// <summary>Residential with the seeded levels row (production prices), plus a type without one.</summary>
        private void Seed()
        {
            _db.ServiceTypes.AddRange(
                new ServiceType { Id = ResidentialId, Name = "Residential Cleaning", ServiceKey = "residential",
                    BasePrice = 90m, TimeDuration = 120m, MinimumPrice = 130m, IsActive = true },
                new ServiceType { Id = HeavyId, Name = "Heavy Conditional Cleaning", ServiceKey = "heavy",
                    BasePrice = 150m, TimeDuration = 180m, MinimumPrice = 200m, IsActive = true });

            Service Svc(int id, int typeId, string key, decimal cost, decimal minutes, int? min, int? max, bool above = false) => new()
            {
                Id = id, Name = key, ServiceKey = key, Cost = cost, TimeDuration = minutes, ServiceTypeId = typeId,
                InputType = "dropdown", IsActive = true, MinValue = min, MaxValue = max, ChargeAboveThreshold = above,
                ZeroQuantityCost = key == "bedrooms" ? 0m : null, ZeroQuantityDuration = key == "bedrooms" ? 0m : null
            };
            _db.Services.AddRange(
                Svc(BedroomsId, ResidentialId, "bedrooms", 22.5m, 30m, 0, 6),
                Svc(BathroomsId, ResidentialId, "bathrooms", 22.5m, 30m, 1, 5),
                Svc(SqftId, ResidentialId, "sqft", 0.18m, 0.24m, null, null, above: true),
                Svc(LevelsId, ResidentialId, PropertyDetailsHelper.LevelsServiceKey, 35m, 25m, 1, 4, above: true),
                Svc(HeavyBedroomsId, HeavyId, "bedrooms", 22.5m, 30m, 0, 6),
                Svc(HeavyBathroomsId, HeavyId, "bathrooms", 22.5m, 30m, 1, 5),
                Svc(HeavySqftId, HeavyId, "sqft", 0.18m, 0.24m, null, null, above: true));

            var id = 1;
            foreach (var (sqftId, bedId) in new[] { (SqftId, BedroomsId), (HeavySqftId, HeavyBedroomsId) })
                foreach (var (q, inc) in new[] { (0, 400m), (1, 650m), (2, 850m), (3, 1000m), (4, 1500m) })
                    _db.ServiceThresholds.Add(new ServiceThreshold { Id = id++, ServiceId = sqftId, SourceServiceId = bedId, SourceQuantity = q, IncludedQuantity = inc });
            _db.ServiceThresholds.Add(new ServiceThreshold { Id = id, ServiceId = LevelsId, SourceServiceId = LevelsId, SourceQuantity = 1, IncludedQuantity = 1 });

            _db.Users.Add(new User { Id = 5, FirstName = "Casey", LastName = "Client", Email = "casey@example.invalid", Role = UserRole.Customer });
            _db.SaveChanges();
        }

        private static CreateBookingDto Booking(int typeId, int bed, int bath, int sqft, string propertyType, int? levels, int bedId, int bathId, int sqftSvcId)
        {
            var services = new List<BookingServiceDto>
            {
                new() { ServiceId = bedId, Quantity = bed },
                new() { ServiceId = bathId, Quantity = bath },
                new() { ServiceId = sqftSvcId, Quantity = sqft }
            };
            if (levels.HasValue) services.Add(new BookingServiceDto { ServiceId = LevelsId, Quantity = levels.Value });
            return new CreateBookingDto { ServiceTypeId = typeId, PropertyType = propertyType, LevelsQuantity = levels,
                Services = services, ExtraServices = new List<BookingExtraServiceDto>() };
        }

        /// <summary>BOOKING's price for these inputs, through booking's own server path.</summary>
        private async Task<OrderPricingCalculator.QuoteResult> BookingQuote(CreateBookingDto dto)
        {
            var st = await _db.ServiceTypes.FirstAsync(s => s.Id == dto.ServiceTypeId);
            return OrderPricingCalculator.CalculateQuote(
                await OrderPricingInputBuilder.FromBookingDtoAsync(_db, st, dto, allowCustomPricing: false));
        }

        /// <summary>An apartment order exactly as booking writes it: its lines come from the quote.</summary>
        private async Task<Order> BookApartment(int typeId, int bedId, int bathId, int sqftSvcId)
        {
            var quote = await BookingQuote(Booking(typeId, 2, 1, 850, PropertyDetailsHelper.Apartment, null, bedId, bathId, sqftSvcId));
            var order = new Order
            {
                Id = 900 + typeId, UserId = 5, ServiceTypeId = typeId, Status = "Active", IsPaid = false,
                ServiceDate = DateTime.UtcNow.Date.AddDays(10), ServiceTime = TimeSpan.FromHours(10),
                SubTotal = quote.SubTotal, TotalDuration = quote.TotalDuration, MaidsCount = 1,
                PropertyType = PropertyDetailsHelper.Apartment, ContactEmail = "casey@example.invalid",
                ContactFirstName = "Casey", ContactLastName = "Client", ContactPhone = "7185550100",
                ServiceAddress = "1 Main St", City = "Brooklyn", State = "NY", ZipCode = "11201", EntryMethod = "Doorman",
                OrderDate = DateTime.UtcNow, CreatedAt = DateTime.UtcNow
            };
            OrderPricingCalculator.AddOrderLinesFromQuote(order, quote);
            var totals = OrderPricingCalculator.CalculateTotals(new OrderPricingCalculator.TotalsInput { SubTotal = quote.SubTotal });
            order.Tax = totals.Tax; order.Total = totals.Total;
            _db.Orders.Add(order);
            await _db.SaveChangesAsync();
            Assert.DoesNotContain(order.OrderServices, os => os.ServiceId == LevelsId);
            return order;
        }

        private DreamCleaningBackend.Services.OrderService Orders() => new(new OrderRepository(_db), _db,
            RecurringDiscountRegressionTests.Stub<IStripeService>(), RecurringDiscountRegressionTests.Stub<IEmailService>(),
            RecurringDiscountRegressionTests.Stub<ISmsService>(), new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            NullLogger<DreamCleaningBackend.Services.OrderService>.Instance, RecurringDiscountRegressionTests.Stub<ILoyaltyDiscountService>());

        /// <summary>The admin editor's save for "now a house with N levels": existing rows + the added levels row.</summary>
        private static SuperAdminUpdateOrderDto AdminHouseEdit(Order order, int levels, OrderPricingCalculator.QuoteResult houseQuote, int levelsServiceId = LevelsId)
        {
            var levelsLine = houseQuote.ServiceLines.FirstOrDefault(l => l.ServiceId == levelsServiceId);
            var rows = order.OrderServices.Select(os => new SuperAdminOrderServiceUpdateDto
            {
                OrderServiceId = os.Id, Quantity = os.Quantity,
                Cost = houseQuote.ServiceLines.First(l => l.ServiceId == os.ServiceId).Cost
            }).ToList();
            rows.Add(new SuperAdminOrderServiceUpdateDto
            {
                OrderServiceId = 0, ServiceId = levelsServiceId, Quantity = levels,
                Cost = levelsLine?.Cost ?? 0m, Duration = levelsLine?.Duration ?? 0m
            });
            return new SuperAdminUpdateOrderDto { PropertyType = PropertyDetailsHelper.House, SubTotal = houseQuote.SubTotal, Services = rows };
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        public async Task AdminSave_AddsTheLevelsLine_AndTheSavedPriceIsBookingsPrice(int levels)
        {
            var order = await BookApartment(ResidentialId, BedroomsId, BathroomsId, SqftId);
            var houseQuote = await BookingQuote(Booking(ResidentialId, 2, 1, 850, PropertyDetailsHelper.House, levels, BedroomsId, BathroomsId, SqftId));

            await Orders().SuperAdminFullUpdateOrder(order.Id, 1, AdminHouseEdit(order, levels, houseQuote));

            var saved = await _db.Orders.Include(o => o.OrderServices).AsNoTracking().FirstAsync(o => o.Id == order.Id);
            var line = Assert.Single(saved.OrderServices, os => os.ServiceId == LevelsId);
            Assert.Equal(levels, line.Quantity);
            Assert.Equal(35m * (levels - 1), line.Cost);
            Assert.Equal(25m * (levels - 1), line.Duration);
            Assert.Equal(PropertyDetailsHelper.House, saved.PropertyType);
            Assert.Equal(levels, saved.LevelsQuantity);
            Assert.Equal(houseQuote.SubTotal, saved.SubTotal);
            Assert.Equal(OrderPricingCalculator.CalculateTotals(new OrderPricingCalculator.TotalsInput { SubTotal = houseQuote.SubTotal }).Total, saved.Total);
        }

        [Fact]
        public async Task CustomerEdit_PricesTheSameHouseExactlyLikeBooking()
        {
            var order = await BookApartment(ResidentialId, BedroomsId, BathroomsId, SqftId);
            var booking = Booking(ResidentialId, 2, 1, 850, PropertyDetailsHelper.House, 3, BedroomsId, BathroomsId, SqftId);
            var houseQuote = await BookingQuote(booking);

            // The customer edit page seeds every catalogue service (levels included) and sends them all.
            var update = new UpdateOrderDto
            {
                PropertyType = PropertyDetailsHelper.House, LevelsQuantity = 3,
                Services = booking.Services, ExtraServices = new List<BookingExtraServiceDto>()
            };
            var loaded = await _db.Orders.Include(o => o.OrderServices).ThenInclude(os => os.Service).FirstAsync(o => o.Id == order.Id);
            var editQuote = OrderPricingCalculator.CalculateQuote(await OrderPricingInputBuilder.FromUpdateDtoAsync(_db, loaded, update));

            Assert.Equal(houseQuote.SubTotal, editQuote.SubTotal);
            Assert.Equal(houseQuote.SubTotal - 70m, (await BookingQuote(Booking(ResidentialId, 2, 1, 850, PropertyDetailsHelper.Apartment, null, BedroomsId, BathroomsId, SqftId))).SubTotal);
        }

        [Fact]
        public async Task AdminSave_IgnoresALevelsRowForAnApartment()
        {
            var order = await BookApartment(ResidentialId, BedroomsId, BathroomsId, SqftId);
            var houseQuote = await BookingQuote(Booking(ResidentialId, 2, 1, 850, PropertyDetailsHelper.House, 3, BedroomsId, BathroomsId, SqftId));
            var dto = AdminHouseEdit(order, 3, houseQuote);
            dto.PropertyType = PropertyDetailsHelper.Apartment;

            await Orders().SuperAdminFullUpdateOrder(order.Id, 1, dto);

            Assert.False(await _db.OrderServices.AnyAsync(os => os.OrderId == order.Id && os.ServiceId == LevelsId));
        }

        [Fact]
        public async Task AdminSave_RefusesAnyOtherNewRow()
        {
            // A levels row belonging to another type, and a non-levels service: neither may be attached.
            var order = await BookApartment(HeavyId, HeavyBedroomsId, HeavyBathroomsId, HeavySqftId);
            var before = await _db.OrderServices.CountAsync(os => os.OrderId == order.Id);
            var quote = await BookingQuote(Booking(HeavyId, 2, 1, 850, PropertyDetailsHelper.House, null, HeavyBedroomsId, HeavyBathroomsId, HeavySqftId));

            await Orders().SuperAdminFullUpdateOrder(order.Id, 1, AdminHouseEdit(order, 3, quote, levelsServiceId: LevelsId));
            await Orders().SuperAdminFullUpdateOrder(order.Id, 1, AdminHouseEdit(order, 3, quote, levelsServiceId: HeavyBathroomsId));

            Assert.Equal(before, await _db.OrderServices.CountAsync(os => os.OrderId == order.Id));
        }

        [Fact]
        public async Task AdminSave_ClampsTheAddedLevelsToTheConfiguredRange()
        {
            var order = await BookApartment(ResidentialId, BedroomsId, BathroomsId, SqftId);
            var houseQuote = await BookingQuote(Booking(ResidentialId, 2, 1, 850, PropertyDetailsHelper.House, 4, BedroomsId, BathroomsId, SqftId));
            var dto = AdminHouseEdit(order, 9, houseQuote);

            await Orders().SuperAdminFullUpdateOrder(order.Id, 1, dto);

            Assert.Equal(4, (await _db.OrderServices.SingleAsync(os => os.OrderId == order.Id && os.ServiceId == LevelsId)).Quantity);
        }
    }
}
