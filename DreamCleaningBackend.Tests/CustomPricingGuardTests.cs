using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// THE CUSTOM-PRICING GATE.
    ///
    /// Custom ("Pre-Arranged") pricing makes the caller's own number the price: the calculator's
    /// custom branch returns before the service catalogue, the rate tiers and the MinimumPrice
    /// floor. It used to be switched on by the request body alone — dto.IsCustomPricing — on every
    /// booking path, including the anonymous one. Any caller could POST customAmount = 1.00 against
    /// an ordinary Residential Cleaning and be charged a dollar.
    ///
    /// The gate now requires BOTH:
    ///   a) the selected service type really IS the custom one, AND
    ///   b) the caller is an Admin/SuperAdmin (passed in as allowCustomPricing — the builder never
    ///      reads HttpContext).
    ///
    /// These tests exercise the REAL builder and the REAL order-creation service against a real
    /// ApplicationDbContext, never a hand-built QuoteInput: a hand-built input would happily assert
    /// a guard that production bypasses.
    ///
    /// The endpoint decisions being modelled (see BookingController):
    ///   POST api/booking/calculate        -> allow = caller is Admin/SuperAdmin (DB role)
    ///   POST api/booking/create           -> allow = caller is Admin/SuperAdmin (DB role)
    ///   POST api/booking/create-for-user  -> allow = ADMIN is Admin/SuperAdmin (Moderator: false)
    ///   POST api/booking/prepare-payment  -> allow = false, ALWAYS (anonymous endpoint)
    ///   confirm-payment order creation    -> allow = false, ALWAYS (consumes prepare-payment's DTO)
    /// </summary>
    public class CustomPricingGuardTests : IDisposable
    {
        // Non-custom type: base 90, floor 130. A rejected custom request must land on the floor.
        private const int ResidentialTypeId = 1;
        private const decimal ResidentialBasePrice = 90m;
        private const decimal ResidentialMinimumPrice = 130m;

        // The real custom ("Pre-Arranged") type: no catalogue, no floor.
        private const int CustomTypeId = 2;

        // The attack amount from the original report: a dollar for a full cleaning.
        private const decimal AttemptedCustomAmount = 1.00m;

        // Priced extras used to prove they contribute nothing on a custom-priced order.
        private const int WindowsExtraId = 10;
        private const int OrganizingExtraId = 11;

        // The two supply extras. Informational like every other custom-mode extra, but the ONLY
        // extras whose presence changes what the cleaner is told to put in the car.
        private const int CleaningSuppliesExtraId = 12;
        private const int CleaningEssentialsExtraId = 13;

        private const int CustomerUserId = 100;
        private const int AdminUserId = 200;
        private const int ModeratorUserId = 300;

        private readonly ApplicationDbContext _context;

        public CustomPricingGuardTests()
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase($"custom-pricing-guard-{Guid.NewGuid()}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;

            _context = new ApplicationDbContext(options);
            Seed();
        }

        public void Dispose() => _context.Dispose();

        private void Seed()
        {
            _context.ServiceTypes.AddRange(
                new ServiceType
                {
                    Id = ResidentialTypeId,
                    Name = "Residential Cleaning",
                    BasePrice = ResidentialBasePrice,
                    TimeDuration = 120m,
                    MinimumPrice = ResidentialMinimumPrice,
                    IsCustom = false,
                    IsActive = true
                },
                new ServiceType
                {
                    Id = CustomTypeId,
                    Name = "Pre-Arranged Cleaning",
                    BasePrice = 0m,
                    TimeDuration = 60m,
                    MinimumPrice = 0m,
                    IsCustom = true,
                    IsActive = true
                });

            // No Service rows are seeded on purpose: every DTO here selects no services, which is
            // exactly what a custom-pricing request looks like. That makes the Residential floor
            // (MinimumPrice) the sole thing standing between a refused request and a $1 order,
            // which is precisely what these tests need to prove.

            // Two priced extras, used only by the informational-extras test below.
            _context.ExtraServices.AddRange(
                new ExtraService
                {
                    Id = WindowsExtraId,
                    Name = "Windows",
                    Price = 12m,
                    Duration = 20m,
                    PriceMultiplier = 1m,
                    HasQuantity = true,
                    IsActive = true,
                    IsAvailableForAll = true
                },
                new ExtraService
                {
                    Id = OrganizingExtraId,
                    Name = "Folding / Organizing",
                    Price = 30m,
                    Duration = 60m,
                    PriceMultiplier = 1m,
                    HasHours = true,
                    IsActive = true,
                    IsAvailableForAll = true
                },
                new ExtraService
                {
                    Id = CleaningSuppliesExtraId,
                    Name = "Cleaning Supplies",
                    Price = 30m,
                    Duration = 0m,
                    PriceMultiplier = 1m,
                    IsActive = true,
                    IsAvailableForAll = true
                },
                new ExtraService
                {
                    Id = CleaningEssentialsExtraId,
                    Name = "Cleaning Essentials",
                    Price = 15m,
                    Duration = 0m,
                    PriceMultiplier = 1m,
                    IsActive = true,
                    IsAvailableForAll = true
                });

            _context.Users.AddRange(
                new User
                {
                    Id = CustomerUserId,
                    Email = "customer@example.com",
                    FirstName = "Cus", LastName = "Tomer",
                    PasswordHash = "x",
                    Role = UserRole.Customer,
                    FirstTimeOrder = false
                },
                new User
                {
                    Id = AdminUserId,
                    Email = "admin@example.com",
                    FirstName = "Ad", LastName = "Min",
                    PasswordHash = "x",
                    Role = UserRole.Admin,
                    FirstTimeOrder = false
                },
                new User
                {
                    Id = ModeratorUserId,
                    Email = "moderator@example.com",
                    FirstName = "Mod", LastName = "Erator",
                    PasswordHash = "x",
                    Role = UserRole.Moderator,
                    FirstTimeOrder = false
                });

            _context.SaveChanges();
        }

        private Task<ServiceType> LoadTypeAsync(int id) =>
            _context.ServiceTypes.FirstAsync(st => st.Id == id);

        /// <summary>A booking asking for custom pricing. No services selected — exactly what the
        /// Pre-arranged form sends, and what an attacker sends against Residential.</summary>
        private static CreateBookingDto CustomPricingDto(int serviceTypeId, decimal customAmount) => new()
        {
            ServiceTypeId = serviceTypeId,
            IsCustomPricing = true,
            CustomAmount = customAmount,
            CustomCleaners = 2,
            CustomDuration = 240m,
            ServiceDate = DateTime.UtcNow.Date.AddDays(7),
            ServiceTime = "10:00",
            ContactFirstName = "Test", ContactLastName = "Booking",
            ContactEmail = "test@example.com", ContactPhone = "5551234567",
            ServiceAddress = "1 Test St", City = "Brooklyn", State = "New York", ZipCode = "11201",
            EntryMethod = "Someone will be home",
            Services = new List<BookingServiceDto>(),
            ExtraServices = new List<BookingExtraServiceDto>()
        };

        private Task<OrderPricingCalculator.QuoteInput> BuildAsync(
            ServiceType serviceType, CreateBookingDto dto, bool allowCustomPricing)
            => OrderPricingInputBuilder.FromBookingDtoAsync(_context, serviceType, dto, allowCustomPricing);

        // ── 1. Honoured: custom service type + admin caller ──────────────────────────────────

        [Fact]
        public async Task CustomServiceType_AdminCaller_HonoursCustomPricing()
        {
            var serviceType = await LoadTypeAsync(CustomTypeId);
            var dto = CustomPricingDto(CustomTypeId, 300m);

            Assert.True(OrderPricingInputBuilder.ShouldHonourCustomPricing(
                serviceType, dto, allowCustomPricing: true));

            var quote = OrderPricingCalculator.CalculateQuote(
                await BuildAsync(serviceType, dto, allowCustomPricing: true));

            // The typed amount is TAX-INCLUSIVE: subtotal + tax add back to it exactly.
            Assert.Equal(300m, quote.SubTotal + quote.TaxOverride!.Value);
            Assert.Equal(2, quote.MaidsCount);
            Assert.False(quote.MinimumPriceApplied);
        }

        // ── 2. Ignored: non-custom service type, even for an admin ───────────────────────────

        [Fact]
        public async Task NonCustomServiceType_AdminCaller_IgnoresCustomPricing_AndPricesNormally()
        {
            var serviceType = await LoadTypeAsync(ResidentialTypeId);
            var dto = CustomPricingDto(ResidentialTypeId, AttemptedCustomAmount);

            // Being an admin is not enough — half (a) fails.
            Assert.False(OrderPricingInputBuilder.ShouldHonourCustomPricing(
                serviceType, dto, allowCustomPricing: true));

            var quote = OrderPricingCalculator.CalculateQuote(
                await BuildAsync(serviceType, dto, allowCustomPricing: true));

            Assert.Null(quote.TaxOverride);
            Assert.Equal(ResidentialMinimumPrice, quote.SubTotal);
        }

        // ── 3. Ignored: custom service type, non-admin caller ────────────────────────────────

        [Fact]
        public async Task CustomServiceType_CustomerCaller_IgnoresCustomPricing()
        {
            var serviceType = await LoadTypeAsync(CustomTypeId);
            var dto = CustomPricingDto(CustomTypeId, AttemptedCustomAmount);

            // Right service type, wrong caller — half (b) fails.
            Assert.False(OrderPricingInputBuilder.ShouldHonourCustomPricing(
                serviceType, dto, allowCustomPricing: false));

            var quote = OrderPricingCalculator.CalculateQuote(
                await BuildAsync(serviceType, dto, allowCustomPricing: false));

            // Priced through the catalogue instead: the custom type has no services and no floor,
            // so the honest answer is 0 — emphatically not the subtotal split out of $1.00.
            Assert.Null(quote.TaxOverride);
            Assert.Equal(0m, quote.SubTotal);
        }

        // ── 4. Ignored: the anonymous prepare-payment decision ───────────────────────────────

        [Fact]
        public async Task PreparePaymentDecision_IsAlwaysDeny_EvenOnTheCustomServiceType()
        {
            // PreparePayment is [AllowAnonymous] and hands its DTO to confirm-payment to build the
            // order from, so it passes allowCustomPricing: false unconditionally — it never
            // consults a role at all for the pricing decision.
            const bool preparePaymentAllowsCustomPricing = false;

            foreach (var typeId in new[] { ResidentialTypeId, CustomTypeId })
            {
                var serviceType = await LoadTypeAsync(typeId);
                var dto = CustomPricingDto(typeId, AttemptedCustomAmount);

                Assert.False(OrderPricingInputBuilder.ShouldHonourCustomPricing(
                    serviceType, dto, preparePaymentAllowsCustomPricing));

                var quote = OrderPricingCalculator.CalculateQuote(
                    await BuildAsync(serviceType, dto, preparePaymentAllowsCustomPricing));

                Assert.Null(quote.TaxOverride);
            }
        }

        // ── 5. The MinimumPrice floor survives every refusal ─────────────────────────────────

        [Fact]
        public async Task RefusedCustomPricing_StillAppliesTheMinimumPriceFloor()
        {
            var serviceType = await LoadTypeAsync(ResidentialTypeId);

            // Both refusal reasons, against the type that actually has a floor.
            foreach (var allow in new[] { true, false })
            {
                var dto = CustomPricingDto(ResidentialTypeId, AttemptedCustomAmount);
                var quote = OrderPricingCalculator.CalculateQuote(
                    await BuildAsync(serviceType, dto, allow));

                Assert.True(quote.MinimumPriceApplied);
                Assert.Equal(ResidentialMinimumPrice, quote.SubTotal);
                Assert.True(quote.SubTotal > AttemptedCustomAmount);
            }
        }

        // ── 6. Quote and charge agree on a refused request ───────────────────────────────────

        [Fact]
        public async Task CalculateAndCreate_ProduceTheSameTotal_ForTheSameRefusedInput()
        {
            // Same authenticated non-admin caller on both endpoints, so both resolve
            // allowCustomPricing: false. (Anonymous `create` is a 401, so it can't be paired.)
            const bool customerAllowsCustomPricing = false;

            var serviceType = await LoadTypeAsync(ResidentialTypeId);

            // POST api/booking/calculate
            var calculateDto = CustomPricingDto(ResidentialTypeId, AttemptedCustomAmount);
            OrderPricingInputBuilder.NormalizeCustomPricing(
                serviceType, calculateDto, customerAllowsCustomPricing, out _);
            var calculateQuote = OrderPricingCalculator.CalculateQuote(
                await BuildAsync(serviceType, calculateDto, customerAllowsCustomPricing));
            var calculateTotal = OrderPricingCalculator.CalculateTotals(
                new OrderPricingCalculator.TotalsInput
                {
                    SubTotal = calculateQuote.SubTotal,
                    TaxOverride = calculateQuote.TaxOverride
                }).Total;

            // POST api/booking/create
            var createDto = CustomPricingDto(ResidentialTypeId, AttemptedCustomAmount);
            OrderPricingInputBuilder.NormalizeCustomPricing(
                serviceType, createDto, customerAllowsCustomPricing, out _);
            var createQuote = OrderPricingCalculator.CalculateQuote(
                await BuildAsync(serviceType, createDto, customerAllowsCustomPricing));
            var createTotal = OrderPricingCalculator.CalculateTotals(
                new OrderPricingCalculator.TotalsInput
                {
                    SubTotal = createQuote.SubTotal,
                    TaxOverride = createQuote.TaxOverride
                }).Total;

            Assert.Equal(calculateTotal, createTotal);
            Assert.True(createTotal > AttemptedCustomAmount);
        }

        // ── 7. The charge-vs-record mismatch, pinned end to end ──────────────────────────────

        [Fact]
        public async Task PreparePaymentThenConfirm_PersistsTheNormallyPricedOrder_NotTheAttackerAmount()
        {
            // This is the failure mode that made a quote-only fix worse than no fix: prepare-payment
            // computes the Stripe charge, parks the DTO in BookingDataService, and confirm-payment
            // later rebuilds the order from that same DTO. If only the quote were guarded, the
            // customer would be charged the correct price while the PERSISTED order recorded $1.00.
            var serviceType = await LoadTypeAsync(ResidentialTypeId);
            var dto = CustomPricingDto(ResidentialTypeId, AttemptedCustomAmount);

            // --- PreparePayment: hard deny, and the stored DTO is normalised in place ---
            var refused = OrderPricingInputBuilder.NormalizeCustomPricing(
                serviceType, dto, allowCustomPricing: false, out var attemptedAmount);

            Assert.True(refused);
            Assert.Equal(AttemptedCustomAmount, attemptedAmount);   // captured for the warning log
            Assert.False(dto.IsCustomPricing);                      // the parked DTO IS the decision
            Assert.Null(dto.CustomAmount);

            var chargedQuote = OrderPricingCalculator.CalculateQuote(
                await BuildAsync(serviceType, dto, allowCustomPricing: false));
            var chargedTotal = OrderPricingCalculator.CalculateTotals(
                new OrderPricingCalculator.TotalsInput
                {
                    SubTotal = chargedQuote.SubTotal,
                    TaxOverride = chargedQuote.TaxOverride
                }).Total;

            // --- ConfirmPayment: rebuilds the real order from the parked DTO, also deny ---
            var order = await CreateOrderServiceUnderTest()
                .CreateOrderAsync(dto, CustomerUserId, allowCustomPricing: false);

            Assert.Equal(ResidentialMinimumPrice, order.SubTotal);
            Assert.NotEqual(AttemptedCustomAmount, order.Total);

            // The invariant: charged and persisted come from one decision.
            Assert.Equal(chargedTotal, order.Total);

            var persisted = await _context.Orders.AsNoTracking().FirstAsync(o => o.Id == order.Id);
            Assert.Equal(order.Total, persisted.Total);
        }

        // ── 8. The legitimate admin Pre-arranged flow still works end to end ─────────────────

        [Fact]
        public async Task CreateForUser_AdminOnCustomServiceType_StillPricesAndPersistsTheTypedAmount()
        {
            // The regression that matters most: this is the path the admin panel actually uses
            // (booking.component -> createBookingForUser -> POST api/booking/create-for-user).
            const decimal typedAmount = 450m;

            var serviceType = await LoadTypeAsync(CustomTypeId);
            var dto = CustomPricingDto(CustomTypeId, typedAmount);
            dto.CustomServiceDisplayName = "Deep";

            // create-for-user resolves allow from the ADMIN's DB role.
            var adminRole = await _context.Users.AsNoTracking()
                .Where(u => u.Id == AdminUserId).Select(u => u.Role).FirstAsync();
            var allowCustomPricing = adminRole is UserRole.Admin or UserRole.SuperAdmin;
            Assert.True(allowCustomPricing);

            // Nothing is stripped from the DTO for a legitimate request.
            Assert.False(OrderPricingInputBuilder.NormalizeCustomPricing(
                serviceType, dto, allowCustomPricing, out _));
            Assert.True(dto.IsCustomPricing);
            Assert.Equal(typedAmount, dto.CustomAmount);

            var order = await CreateOrderServiceUnderTest().CreateOrderAsync(
                dto, CustomerUserId, allowCustomPricing,
                new BookingCreationOptions { BookedByAdminUserId = AdminUserId });

            // The customer pays exactly what the admin typed — tax split out of it, not added on.
            Assert.Equal(typedAmount, order.Total);
            Assert.Equal(typedAmount, order.SubTotal + order.Tax);
            Assert.Equal(2, order.MaidsCount);
            Assert.Equal("Deep", order.CustomServiceDisplayName);

            var persisted = await _context.Orders.AsNoTracking().FirstAsync(o => o.Id == order.Id);
            Assert.Equal(typedAmount, persisted.Total);
        }

        // ── Moderator: create-for-user is open to them, custom pricing is not ────────────────

        [Fact]
        public async Task CreateForUser_ModeratorOnCustomServiceType_IsRefused()
        {
            // Moderator is View-only in PermissionService, and the booking UI only offers the
            // custom service type to Admin/SuperAdmin — so this closes the API path without
            // removing anything a Moderator can do through the product.
            var serviceType = await LoadTypeAsync(CustomTypeId);
            var dto = CustomPricingDto(CustomTypeId, AttemptedCustomAmount);

            var moderatorRole = await _context.Users.AsNoTracking()
                .Where(u => u.Id == ModeratorUserId).Select(u => u.Role).FirstAsync();
            var allowCustomPricing = moderatorRole is UserRole.Admin or UserRole.SuperAdmin;
            Assert.False(allowCustomPricing);

            Assert.True(OrderPricingInputBuilder.NormalizeCustomPricing(
                serviceType, dto, allowCustomPricing, out var attempted));
            Assert.Equal(AttemptedCustomAmount, attempted);

            var quote = OrderPricingCalculator.CalculateQuote(
                await BuildAsync(serviceType, dto, allowCustomPricing));
            Assert.Null(quote.TaxOverride);
        }

        // ── Normalisation leaves a legitimate request completely untouched ───────────────────

        [Fact]
        public async Task Normalize_DoesNothing_WhenNoCustomPricingWasRequested()
        {
            var serviceType = await LoadTypeAsync(ResidentialTypeId);
            var dto = CustomPricingDto(ResidentialTypeId, AttemptedCustomAmount);
            dto.IsCustomPricing = false;
            dto.CustomAmount = null;

            Assert.False(OrderPricingInputBuilder.NormalizeCustomPricing(
                serviceType, dto, allowCustomPricing: false, out var attempted));
            Assert.Null(attempted);

            await Task.CompletedTask;
        }

        // ── Informational extras on a custom-priced order ────────────────────────────────────

        /// <summary>
        /// Extras ARE selectable on a Custom ("Pre-Arranged") service type, but purely as
        /// information for the admin panel and the cleaner's job email. They must be recorded on
        /// the order — otherwise the cleaner never learns the fridge is part of the job — while
        /// contributing exactly $0 and 0 minutes, because the admin-entered amount and duration
        /// ARE the quote. Both a quantity extra and an hours extra are exercised: the ordinary
        /// branch prices those as price × quantity and price × hours, so either one leaking
        /// through would move the total.
        /// </summary>
        [Fact]
        public async Task CustomPricing_Extras_ArePersistedButCostNothingAndAddNoTime()
        {
            var serviceType = await LoadTypeAsync(CustomTypeId);
            var dto = CustomPricingDto(CustomTypeId, 300m);
            dto.ExtraServices = new List<BookingExtraServiceDto>
            {
                new() { ExtraServiceId = WindowsExtraId, Quantity = 5, Hours = 0 },
                new() { ExtraServiceId = OrganizingExtraId, Quantity = 1, Hours = 2 }
            };

            var quote = OrderPricingCalculator.CalculateQuote(
                await BuildAsync(serviceType, dto, allowCustomPricing: true));

            // The typed amount is untouched — no extra leaked into the subtotal or the tax.
            Assert.Equal(300m, quote.SubTotal + quote.TaxOverride!.Value);

            // Duration is still cleaners × the entered per-cleaner minutes (2 × 240), not
            // 480 + 5×20 + 2×60.
            Assert.Equal(480m, quote.TotalDuration);
            Assert.Equal(240m, quote.DisplayDuration);

            // Both extras are on the quote, at zero, with the quantity/hours that describe the job.
            Assert.Equal(2, quote.ExtraServiceLines.Count);
            Assert.All(quote.ExtraServiceLines, line =>
            {
                Assert.Equal(0m, line.Cost);
                Assert.Equal(0m, line.Duration);
            });

            var windows = quote.ExtraServiceLines.Single(l => l.ExtraServiceId == WindowsExtraId);
            Assert.Equal(5, windows.Quantity);

            var organizing = quote.ExtraServiceLines.Single(l => l.ExtraServiceId == OrganizingExtraId);
            Assert.Equal(2m, organizing.Hours);

            // And they reach the persisted order, which is the whole point of keeping them.
            var order = new Order();
            OrderPricingCalculator.AddOrderLinesFromQuote(order, quote);
            Assert.Equal(2, order.OrderExtraServices.Count);
            Assert.All(order.OrderExtraServices, oes =>
            {
                Assert.Equal(0m, oes.Cost);
                Assert.Equal(0m, oes.Duration);
            });
        }

        /// <summary>
        /// THE SUPPLY EXTRAS STILL REACH THE CLEANER ON A PRE-ARRANGED ORDER.
        ///
        /// Cleaning Supplies and Cleaning Essentials are informational on a custom-priced order
        /// like every other extra — $0 and 0 minutes, because the admin-entered amount IS the
        /// quote. What is NOT informational is their meaning: the extra being on the order is the
        /// customer paying US to bring the products, so the row is what puts the Supplies and
        /// Essentials lines in the assignment email and SMS and the banners in the cleaner portal.
        /// Those three surfaces resolve it through CleanerJobView, which matches on the extra's
        /// NAME and knows nothing about service types or cost — this test pins that a $0 line is
        /// as loud as a $30 one, so a future "skip the free lines" shortcut fails here instead of
        /// silently sending a crew out empty-handed.
        ///
        /// The same rows are also why the CUSTOMER's "please provide" checklist comes back empty:
        /// both halves of the arrangement are read from one source and cannot contradict.
        /// </summary>
        [Fact]
        public async Task CustomPricing_SupplyExtras_StillTellTheCleanerWhatToBring()
        {
            var serviceType = await LoadTypeAsync(CustomTypeId);
            var dto = CustomPricingDto(CustomTypeId, 300m);
            dto.ExtraServices = new List<BookingExtraServiceDto>
            {
                new() { ExtraServiceId = CleaningSuppliesExtraId, Quantity = 1, Hours = 0 },
                new() { ExtraServiceId = CleaningEssentialsExtraId, Quantity = 1, Hours = 0 }
            };

            var quote = OrderPricingCalculator.CalculateQuote(
                await BuildAsync(serviceType, dto, allowCustomPricing: true));

            // Free, as on every other custom-mode extra — the typed amount is the whole quote.
            Assert.Equal(300m, quote.SubTotal + quote.TaxOverride!.Value);
            Assert.All(quote.ExtraServiceLines, line =>
            {
                Assert.Equal(0m, line.Cost);
                Assert.Equal(0m, line.Duration);
            });

            var order = new Order { ServiceType = serviceType };
            OrderPricingCalculator.AddOrderLinesFromQuote(order, quote);
            // The calculator writes ids; the cleaner surfaces read the order with the extras
            // Included, so attach the navigations the same way EF would.
            foreach (var line in order.OrderExtraServices)
            {
                line.ExtraService = await _context.ExtraServices
                    .FirstAsync(e => e.Id == line.ExtraServiceId);
            }

            // What the cleaner is told: load the car with both.
            Assert.True(CleanerJobView.RequiresCleanerToBringSupplies(order));
            Assert.True(CleanerJobView.RequiresCleanerToBringEssentials(order));

            // ...and neither is repeated in the task list, since each has its own row.
            Assert.All(order.OrderExtraServices, line =>
                Assert.True(CleanerJobView.IsExtraHiddenFromCleaners(line.ExtraService!.Name)));

            // The customer's own checklist is the other half of the same fact: nothing to prepare.
            Assert.Empty(CustomerSupplyChecklist.BuildItems(CustomerSupplyChecklist.Resolve(order)));

            // ...whereas the same pre-arranged order WITHOUT the Essentials extra still asks for
            // that group by hand. That is the whole reason the card had to become reachable here:
            // the booking page's supplies modal never opens on a custom-priced order, so before
            // this there was no way to tell a pre-arranged customer we were bringing them.
            order.OrderExtraServices.Remove(order.OrderExtraServices
                .Single(l => l.ExtraServiceId == CleaningEssentialsExtraId));
            var withoutEssentials = CustomerSupplyChecklist.BuildItems(
                CustomerSupplyChecklist.Resolve(order));
            Assert.Contains("Paper towels", withoutEssentials);
            Assert.Contains("Toilet brush", withoutEssentials);
        }

        // ── Test doubles ─────────────────────────────────────────────────────────────────────

        private BookingCreationService CreateOrderServiceUnderTest() =>
            new(_context,
                new NoLoyaltyDiscountService(),
                new UnusedGiftCardService(),
                NullLogger<BookingCreationService>.Instance);

        /// <summary>No loyalty on any account: CalculateForOrderAsync returns (0,0) and stacking
        /// passes the other slots straight through, so these tests measure pricing alone.</summary>
        private sealed class NoLoyaltyDiscountService : ILoyaltyDiscountService
        {
            public Task<(decimal amount, decimal percentage)> CalculateForOrderAsync(int userId, decimal subTotal)
                => Task.FromResult((0m, 0m));

            public (decimal loyaltyAmount, decimal loyaltyPercentage, decimal subscriptionAmount, decimal promoAmount)
                ResolveStacking(decimal loyaltyCandidateAmount, decimal loyaltyCandidatePercentage,
                                decimal subscriptionAmount, decimal promoAmount)
                => (loyaltyCandidateAmount, loyaltyCandidatePercentage, subscriptionAmount, promoAmount);

            public Task ApplyToOrderAsync(int orderId) => Task.CompletedTask;
            public Task ReverseFromOrderAsync(int orderId) => Task.CompletedTask;

            public Task<LoyaltyDiscountDto> GetForUserAsync(int userId) => throw new NotSupportedException();
            public Task<LoyaltyDiscountDto> SetManualAsync(int userId, decimal percentage, int adminUserId) => throw new NotSupportedException();
            public Task<LoyaltyDiscountDto> ClearAsync(int userId, int adminUserId) => throw new NotSupportedException();
        }

        /// <summary>These bookings carry no gift card, so nothing here should ever be called.</summary>
        private sealed class UnusedGiftCardService : IGiftCardService
        {
            public Task<GiftCard> CreateGiftCard(int? userId, CreateGiftCardDto createDto) => throw new NotSupportedException();
            public Task<GiftCardValidationDto> ValidateGiftCard(string code) => throw new NotSupportedException();
            public Task<decimal> ApplyGiftCardToOrder(string code, decimal orderAmount, int orderId, int userId) => throw new NotSupportedException();
            public Task<List<GiftCardDto>> GetUserGiftCards(int userId) => throw new NotSupportedException();
            public Task<List<GiftCardUsageDto>> GetGiftCardUsageHistory(string code, int userId) => throw new NotSupportedException();
            public Task<GiftCard> GetGiftCardByCode(string code) => throw new NotSupportedException();
            public Task<bool> MarkGiftCardAsPaid(int giftCardId, string paymentIntentId) => throw new NotSupportedException();
            public string GenerateUniqueGiftCardCode() => throw new NotSupportedException();
            public Task<List<GiftCardAdminDto>> GetAllGiftCardsForAdmin() => throw new NotSupportedException();
            public Task<bool> SimulateGiftCardPayment(int giftCardId) => throw new NotSupportedException();
        }
    }
}
