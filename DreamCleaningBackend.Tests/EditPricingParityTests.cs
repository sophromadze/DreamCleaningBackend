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
    /// 2026-10 (owner's rules): an order edit prices EXACTLY like booking. Booking the edited job
    /// from scratch, the customer editing an order into it, and an admin editing an order into it
    /// must store the same subtotal, discounts, tax and total - with a percentage promo, a fixed
    /// promo, a fixed special offer, a recurring-plan discount and a gift card.
    ///
    /// Every expectation is the BOOKING path's own result for the same inputs
    /// (BookingCreationService.CreateOrderAsync), never a constant, so the test fails the moment
    /// any of the three surfaces drifts from the others. A few anchors then state the rules in
    /// plain numbers (a fixed promo stays fixed; a fixed promo larger than the job is capped).
    /// </summary>
    public class EditPricingParityTests : IDisposable
    {
        private const int TypeId = 1, BedroomsId = 10, BathroomsId = 20, SqftId = 30, UserId = 5;
        private const int OfferId = 1, PlanId = 1;

        private readonly ApplicationDbContext _db;
        private int _nextGrant = 100, _nextCard = 1;

        public EditPricingParityTests()
        {
            _db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase($"edit-parity-{Guid.NewGuid()}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
            Seed();
        }

        public void Dispose() => _db.Dispose();

        public enum Kind { PercentPromo, FixedPromo, FixedOffer, Subscription, PercentPromoAndSubscription, GiftCard }

        private void Seed()
        {
            _db.ServiceTypes.Add(new ServiceType { Id = TypeId, Name = "Residential Cleaning", ServiceKey = "residential",
                BasePrice = 90m, TimeDuration = 120m, MinimumPrice = 130m, IsActive = true });

            Service Svc(int id, string key, decimal cost, decimal minutes, int? min, int? max, bool above = false) => new()
            {
                Id = id, Name = key, ServiceKey = key, Cost = cost, TimeDuration = minutes, ServiceTypeId = TypeId,
                InputType = "dropdown", IsActive = true, MinValue = min, MaxValue = max, ChargeAboveThreshold = above,
                ZeroQuantityCost = key == "bedrooms" ? 0m : null, ZeroQuantityDuration = key == "bedrooms" ? 0m : null
            };
            _db.Services.AddRange(
                Svc(BedroomsId, "bedrooms", 22.5m, 30m, 0, 6),
                Svc(BathroomsId, "bathrooms", 22.5m, 30m, 1, 5),
                Svc(SqftId, "sqft", 0.18m, 0.24m, null, null, above: true));
            var id = 1;
            foreach (var (q, inc) in new[] { (0, 400m), (1, 650m), (2, 850m), (3, 1000m), (4, 1500m) })
                _db.ServiceThresholds.Add(new ServiceThreshold { Id = id++, ServiceId = SqftId, SourceServiceId = BedroomsId, SourceQuantity = q, IncludedQuantity = inc });

            _db.Subscriptions.Add(new Subscription { Id = PlanId, Name = "Weekly", DiscountPercentage = 15m, SubscriptionDays = 7,
                IsActive = true, CreatedAt = DateTime.UtcNow });
            _db.Users.Add(new User { Id = UserId, FirstName = "Casey", LastName = "Client", Email = "casey@example.invalid",
                Role = UserRole.Customer, FirstTimeOrder = false, SubscriptionId = PlanId, SubscriptionExpiryDate = DateTime.UtcNow.AddDays(60) });

            _db.PromoCodes.AddRange(
                new PromoCode { Id = 1, Code = "PCT20", IsPercentage = true, DiscountValue = 20m, IsActive = true, CreatedAt = DateTime.UtcNow },
                new PromoCode { Id = 2, Code = "FIX30", IsPercentage = false, DiscountValue = 30m, IsActive = true, CreatedAt = DateTime.UtcNow },
                new PromoCode { Id = 3, Code = "FIX999", IsPercentage = false, DiscountValue = 999m, IsActive = true, CreatedAt = DateTime.UtcNow });
            _db.SpecialOffers.Add(new SpecialOffer { Id = OfferId, Name = "Spring", Description = "$25 off", IsPercentage = false,
                DiscountValue = 25m, Type = OfferType.Custom, IsActive = true, CreatedAt = DateTime.UtcNow });
            _db.SaveChanges();
        }

        // ===== Plumbing =====

        private BookingCreationService Booking() => new(_db, new NoLoyalty(),
            new GiftCardService(_db, new RecordingAuditService(), RecurringDiscountRegressionTests.Stub<IEmailService>(),
                NullLogger<GiftCardService>.Instance),
            NullLogger<BookingCreationService>.Instance);

        private DreamCleaningBackend.Services.OrderService Orders() => new(new OrderRepository(_db), _db,
            RecurringDiscountRegressionTests.Stub<IStripeService>(), RecurringDiscountRegressionTests.Stub<IEmailService>(),
            RecurringDiscountRegressionTests.Stub<ISmsService>(), new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            NullLogger<DreamCleaningBackend.Services.OrderService>.Instance, RecurringDiscountRegressionTests.Stub<ILoyaltyDiscountService>());

        private static List<BookingServiceDto> Lines(int bed, int bath, int sqft) => new()
        {
            new() { ServiceId = BedroomsId, Quantity = bed },
            new() { ServiceId = BathroomsId, Quantity = bath },
            new() { ServiceId = SqftId, Quantity = sqft }
        };

        /// <summary>A booking request carrying the discount under test (fresh offer grant / card per order).</summary>
        private CreateBookingDto Request(Kind kind, int bed, int bath, int sqft, string? promo = null)
        {
            var dto = new CreateBookingDto
            {
                ServiceTypeId = TypeId, PropertyType = PropertyDetailsHelper.Apartment,
                Services = Lines(bed, bath, sqft), ExtraServices = new List<BookingExtraServiceDto>(),
                ServiceDate = DateTime.UtcNow.Date.AddDays(10), ServiceTime = "10:00", EntryMethod = "Doorman",
                ContactFirstName = "Casey", ContactLastName = "Client", ContactEmail = "casey@example.invalid",
                ContactPhone = "7185550100", ServiceAddress = "1 Main St", City = "Brooklyn", State = "NY", ZipCode = "11201"
            };
            switch (kind)
            {
                case Kind.PercentPromo: dto.PromoCode = promo ?? "PCT20"; break;
                case Kind.FixedPromo: dto.PromoCode = promo ?? "FIX30"; break;
                case Kind.Subscription: dto.SubscriptionId = PlanId; break;
                case Kind.PercentPromoAndSubscription: dto.PromoCode = "PCT20"; dto.SubscriptionId = PlanId; break;
                case Kind.FixedOffer:
                    var grant = new UserSpecialOffer { Id = _nextGrant++, UserId = UserId, SpecialOfferId = OfferId, GrantedAt = DateTime.UtcNow };
                    _db.UserSpecialOffers.Add(grant);
                    _db.SaveChanges();
                    dto.UserSpecialOfferId = grant.Id;
                    break;
                case Kind.GiftCard:
                    var code = $"GC{_nextCard:00}-AAAA-BBBB";
                    _db.GiftCards.Add(new GiftCard { Id = _nextCard++, Code = code, OriginalAmount = 100m, CurrentBalance = 100m,
                        SenderName = "Pat", SenderEmail = "pat@example.invalid", IsActive = true, IsPaid = true, PaidAt = DateTime.UtcNow });
                    _db.SaveChanges();
                    dto.GiftCardCode = code;
                    dto.GiftCardAmountToUse = 100m;
                    break;
            }
            return dto;
        }

        private async Task<Order> Book(CreateBookingDto dto)
        {
            var order = await Booking().CreateOrderAsync(dto, UserId, allowCustomPricing: false);
            _db.ChangeTracker.Clear();
            return await _db.Orders.Include(o => o.OrderServices).Include(o => o.OrderExtraServices).FirstAsync(o => o.Id == order.Id);
        }

        private async Task<Order> Reload(int orderId)
        {
            _db.ChangeTracker.Clear();
            return await _db.Orders.Include(o => o.OrderServices).Include(o => o.OrderExtraServices).FirstAsync(o => o.Id == orderId);
        }

        private static UpdateOrderDto CustomerEdit(Order order, int bed, int bath, int sqft, decimal totalDuration) => new()
        {
            ServiceDate = order.ServiceDate, ServiceTime = "10:00", MaidsCount = 1, TotalDuration = totalDuration,
            PropertyType = PropertyDetailsHelper.Apartment, EntryMethod = "Doorman",
            ContactFirstName = "Casey", ContactLastName = "Client", ContactEmail = "casey@example.invalid", ContactPhone = "7185550100",
            ServiceAddress = "1 Main St", City = "Brooklyn", State = "NY", ZipCode = "11201",
            Services = Lines(bed, bath, sqft), ExtraServices = new List<BookingExtraServiceDto>(), Tips = order.Tips
        };

        /// <summary>
        /// What the admin editor posts after stepping the lines to the new quantities: the same
        /// rows with new quantities, the subtotal its preview showed and the discounts its preview
        /// derived (resolveEditedDiscounts, the TS mirror of the rule asserted here).
        /// </summary>
        private static SuperAdminUpdateOrderDto AdminEdit(Order order, int bed, int bath, int sqft, decimal previewSubTotal, decimal totalDuration)
        {
            var qty = new Dictionary<int, int> { [BedroomsId] = bed, [BathroomsId] = bath, [SqftId] = sqft };
            var (promo, plan, _) = OrderPricingCalculator.ResolveEditedDiscounts(order, previewSubTotal);
            return new SuperAdminUpdateOrderDto
            {
                Services = order.OrderServices.Select(os => new SuperAdminOrderServiceUpdateDto
                    { OrderServiceId = os.Id, Quantity = qty[os.ServiceId], Cost = os.Cost }).ToList(),
                SubTotal = previewSubTotal,
                DiscountAmount = promo,
                SubscriptionDiscountAmount = plan,
                TotalDuration = totalDuration,
                PropertyType = PropertyDetailsHelper.Apartment
            };
        }

        private static void AssertSamePrice(Order expected, Order actual, string surface)
        {
            Assert.True(expected.SubTotal == actual.SubTotal, $"{surface}: subtotal {actual.SubTotal} != booking {expected.SubTotal}");
            Assert.True(expected.DiscountAmount == actual.DiscountAmount, $"{surface}: promo {actual.DiscountAmount} != booking {expected.DiscountAmount}");
            Assert.True(expected.SubscriptionDiscountAmount == actual.SubscriptionDiscountAmount, $"{surface}: plan {actual.SubscriptionDiscountAmount} != booking {expected.SubscriptionDiscountAmount}");
            Assert.True(expected.LoyaltyDiscountAmount == actual.LoyaltyDiscountAmount, $"{surface}: loyalty {actual.LoyaltyDiscountAmount} != booking {expected.LoyaltyDiscountAmount}");
            Assert.True(expected.Tax == actual.Tax, $"{surface}: tax {actual.Tax} != booking {expected.Tax}");
            Assert.True(expected.Total == actual.Total, $"{surface}: total {actual.Total} != booking {expected.Total}");
            Assert.True(expected.GiftCardAmountUsed == actual.GiftCardAmountUsed, $"{surface}: gift card {actual.GiftCardAmountUsed} != booking {expected.GiftCardAmountUsed}");
            // Per-line costs and minutes too: "consistent with booking" includes the stored lines.
            foreach (var line in expected.OrderServices)
            {
                var other = actual.OrderServices.Single(os => os.ServiceId == line.ServiceId);
                Assert.True(line.Cost == other.Cost && line.Duration == other.Duration && line.Quantity == other.Quantity,
                    $"{surface}: line {line.ServiceId} {other.Quantity}/{other.Cost}/{other.Duration} != booking {line.Quantity}/{line.Cost}/{line.Duration}");
            }
        }

        // ===== Parity: booking == customer edit == admin edit =====

        [Theory]
        [InlineData(Kind.PercentPromo)]
        [InlineData(Kind.FixedPromo)]
        [InlineData(Kind.FixedOffer)]
        [InlineData(Kind.Subscription)]
        [InlineData(Kind.PercentPromoAndSubscription)]
        [InlineData(Kind.GiftCard)]
        public async Task BookingCustomerEditAndAdminEdit_StoreTheSamePrice(Kind kind)
        {
            // The edited job, booked from scratch: the reference every surface must match.
            var booked = await Book(Request(kind, 3, 2, 1000));

            // Customer: book the smaller job, then edit it up to the reference job.
            var customerOrder = await Book(Request(kind, 2, 1, 850));
            await Orders().UpdateOrder(customerOrder.Id, UserId, CustomerEdit(customerOrder, 3, 2, 1000, booked.TotalDuration));
            AssertSamePrice(booked, await Reload(customerOrder.Id), $"{kind} customer edit");

            // Admin: same, through the admin editor's save.
            var adminOrder = await Book(Request(kind, 2, 1, 850));
            await Orders().SuperAdminFullUpdateOrder(adminOrder.Id, 1,
                AdminEdit(adminOrder, 3, 2, 1000, booked.SubTotal, booked.TotalDuration));
            AssertSamePrice(booked, await Reload(adminOrder.Id), $"{kind} admin edit");
        }

        // ===== The rules, stated in plain numbers =====

        [Fact]
        public async Task Booking_RecordsTheRuleBehindEachSurvivingDiscount()
        {
            var pct = await Book(Request(Kind.PercentPromoAndSubscription, 2, 1, 850));
            Assert.Equal(20m, pct.DiscountPercent);
            Assert.Null(pct.DiscountFixedAmount);
            Assert.Equal(15m, pct.SubscriptionDiscountPercent);

            var fixedPromo = await Book(Request(Kind.FixedPromo, 2, 1, 850));
            Assert.Null(fixedPromo.DiscountPercent);
            Assert.Equal(30m, fixedPromo.DiscountFixedAmount);
            Assert.Null(fixedPromo.SubscriptionDiscountPercent);
        }

        [Fact]
        public async Task FixedPromo_StaysFixedWhenTheJobGrows()
        {
            var order = await Book(Request(Kind.FixedPromo, 2, 1, 850));
            Assert.Equal(30m, order.DiscountAmount);
            var bookedSubTotal = order.SubTotal; // the edit mutates the tracked instance

            await Orders().UpdateOrder(order.Id, UserId, CustomerEdit(order, 4, 3, 1500, order.TotalDuration));
            var edited = await Reload(order.Id);
            Assert.True(edited.SubTotal > bookedSubTotal);
            Assert.Equal(30m, edited.DiscountAmount); // was scaled up with the subtotal before 2026-10
        }

        [Fact]
        public async Task FixedPromoLargerThanTheJob_IsCappedSoTheTotalNeverGoesNegative()
        {
            var order = await Book(Request(Kind.FixedPromo, 2, 1, 850, promo: "FIX999"));
            Assert.Equal(order.SubTotal, order.DiscountAmount); // capped at booking, like a fixed special offer
            Assert.Equal(999m, order.DiscountFixedAmount);       // the rule keeps the face value
            Assert.Equal(0m, order.Total);

            await Orders().SuperAdminFullUpdateOrder(order.Id, 1, AdminEdit(order, 3, 2, 1000,
                // Subtotals do not depend on the discount, so any booked 3/2/1000 job is the reference.
                (await Book(Request(Kind.PercentPromo, 3, 2, 1000))).SubTotal, order.TotalDuration));
            var edited = await Reload(order.Id);
            Assert.Equal(edited.SubTotal, edited.DiscountAmount);
            Assert.Equal(0m, edited.Total);
        }

        [Fact]
        public async Task PercentPromo_IsRecomputedLikeBooking_NotScaledByTheStoredRatio()
        {
            // 20% of 347.53 is 69.506 -> 69.51. A ratio off a previously rounded amount can land a
            // cent away; the recorded percentage cannot.
            var order = await Book(Request(Kind.PercentPromo, 2, 1, 850));
            var (promo, _, _) = OrderPricingCalculator.ResolveEditedDiscounts(order, 347.53m);
            Assert.Equal(69.51m, promo);
        }

        /// <summary>
        /// Shared vectors with order-pricing.calculator.spec.ts ("percentOf"): the TS mirror computes
        /// in whole cents so it lands on these exact figures too. 1.15 x 50% is the float trap
        /// (0.575 -> 57.49999 cents in binary floating point).
        /// </summary>
        [Theory]
        [InlineData(1.15, 50, 0.58)]
        [InlineData(347.53, 20, 69.51)]
        [InlineData(10.25, 10, 1.03)]
        [InlineData(202.50, 15, 30.38)]
        [InlineData(157.50, 11.11, 17.50)]
        public void PercentOf_RoundsHalfAwayFromZeroInCents(double subTotal, double percent, double expected)
        {
            Assert.Equal((decimal)expected, OrderPricingCalculator.PercentOf((decimal)subTotal, (decimal)percent));
        }

        [Fact]
        public async Task LegacyOrderWithoutARecordedRule_KeepsTheProportionalRescale()
        {
            var order = await Book(Request(Kind.FixedPromo, 2, 1, 850));
            // An order booked before rules were recorded (and not backfilled).
            order.DiscountFixedAmount = null;
            await _db.SaveChangesAsync();

            var (promo, _, _) = OrderPricingCalculator.ResolveEditedDiscounts(order, order.SubTotal * 2m);
            Assert.Equal(60m, promo);
        }

        // ===== Admin save: the server prices the lines =====

        [Fact]
        public async Task AdminSave_IgnoresAStaleEditorSubtotal_AndUsesTheServerPrice()
        {
            var reference = await Book(Request(Kind.PercentPromo, 3, 2, 1000));
            var order = await Book(Request(Kind.PercentPromo, 2, 1, 850));

            var dto = AdminEdit(order, 3, 2, 1000, reference.SubTotal + 37m, reference.TotalDuration);
            await Orders().SuperAdminFullUpdateOrder(order.Id, 1, dto);

            AssertSamePrice(reference, await Reload(order.Id), "stale editor subtotal");
        }

        [Fact]
        public async Task AdminSave_KeepsAPriceTheAdminTyped()
        {
            var order = await Book(Request(Kind.PercentPromo, 2, 1, 850));
            var dto = AdminEdit(order, 2, 1, 850, 400m, order.TotalDuration);
            dto.PriceTypedByAdmin = true;

            await Orders().SuperAdminFullUpdateOrder(order.Id, 1, dto);

            var edited = await Reload(order.Id);
            Assert.Equal(400m, edited.SubTotal);
            Assert.Equal(80m, edited.DiscountAmount); // 20% of the typed price
        }

        [Fact]
        public async Task AdminSave_WithNoLineChange_NeverRepricesTheBookedOrder()
        {
            var order = await Book(Request(Kind.PercentPromo, 2, 1, 850));
            var booked = order.SubTotal;
            // Today's catalogue is more expensive than when the order was booked.
            (await _db.Services.FirstAsync(s => s.Id == BedroomsId)).Cost = 99m;
            await _db.SaveChangesAsync();

            var dto = AdminEdit(order, 2, 1, 850, booked + 5m, order.TotalDuration);
            dto.ContactPhone = "7185550199";
            await Orders().SuperAdminFullUpdateOrder(order.Id, 1, dto);

            var edited = await Reload(order.Id);
            Assert.Equal(booked, edited.SubTotal);
            Assert.Equal("7185550199", edited.ContactPhone);
        }

        [Fact]
        public async Task AdminSave_ASuperAdminTypedDiscountIsKept_AndItsRuleCleared()
        {
            var order = await Book(Request(Kind.PercentPromo, 2, 1, 850));
            var dto = AdminEdit(order, 2, 1, 850, order.SubTotal, order.TotalDuration);
            dto.DiscountAmount = 12.34m;

            await Orders().SuperAdminFullUpdateOrder(order.Id, 1, dto);

            var edited = await Reload(order.Id);
            Assert.Equal(12.34m, edited.DiscountAmount);
            Assert.Null(edited.DiscountPercent);
            Assert.Null(edited.DiscountFixedAmount);
        }

        // ===== The controller gate follows the same rule =====

        [Fact]
        public async Task DiscountPolicy_AcceptsTheBookingRuleForAFixedPromo()
        {
            var order = await Book(Request(Kind.FixedPromo, 2, 1, 850));
            var grown = order.SubTotal + 100m;

            Assert.True(OrderDiscountEditPolicy.KeepsDiscounts(order,
                new SuperAdminUpdateOrderDto { SubTotal = grown, DiscountAmount = 30m }));
            // The old proportional figure is a CHANGED discount for a fixed promo now.
            var proportional = OrderPricingCalculator.Round2(grown * (30m / order.SubTotal));
            Assert.False(OrderDiscountEditPolicy.KeepsDiscounts(order,
                new SuperAdminUpdateOrderDto { SubTotal = grown, DiscountAmount = proportional }));
        }

        /// <summary>No loyalty on the account: stacking passes the other slots through.</summary>
        private sealed class NoLoyalty : ILoyaltyDiscountService
        {
            public Task<(decimal amount, decimal percentage)> CalculateForOrderAsync(int userId, decimal subTotal) => Task.FromResult((0m, 0m));
            public (decimal loyaltyAmount, decimal loyaltyPercentage, decimal subscriptionAmount, decimal promoAmount)
                ResolveStacking(decimal loyaltyCandidateAmount, decimal loyaltyCandidatePercentage, decimal subscriptionAmount, decimal promoAmount)
                => OrderPricingCalculator.ResolveLoyaltyStacking(loyaltyCandidateAmount, loyaltyCandidatePercentage, subscriptionAmount, promoAmount);
            public Task ApplyToOrderAsync(int orderId) => Task.CompletedTask;
            public Task ReverseFromOrderAsync(int orderId) => Task.CompletedTask;
            public Task<LoyaltyDiscountDto> GetForUserAsync(int userId) => throw new NotSupportedException();
            public Task<LoyaltyDiscountDto> SetManualAsync(int userId, decimal percentage, int adminUserId, bool isLifetime = false) => throw new NotSupportedException();
            public Task<LoyaltyDiscountDto> ClearAsync(int userId, int adminUserId) => throw new NotSupportedException();
        }
    }
}
