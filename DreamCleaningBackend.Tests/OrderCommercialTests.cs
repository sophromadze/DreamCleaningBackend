using System.Security.Claims;
using ClosedXML.Excel;
using DreamCleaningBackend.Controllers;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// Regular vs Commercial orders (2026-10). <c>Order.IsCommercial</c> is decided ONCE, at
    /// creation, by <see cref="OrderCommercialPolicy"/>; afterwards only an admin's explicit move
    /// changes it. These tests pin both halves: the creation rule (business customer, commercial
    /// billing link, the Admin Mode choice) and that nothing re-files an existing order behind the
    /// admin's back.
    /// </summary>
    public class OrderCommercialTests : IDisposable
    {
        private const int ResidentialTypeId = 1;
        private const int HouseholdId = 10;
        private const int BusinessId = 11;
        private const int AdminId = 2;

        private readonly ApplicationDbContext _db;

        public OrderCommercialTests()
        {
            _db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase($"order-commercial-{Guid.NewGuid()}", b => b.EnableNullChecks(false))
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options);

            _db.ServiceTypes.Add(new ServiceType
            {
                Id = ResidentialTypeId, Name = "Residential Cleaning", ServiceKey = "residential",
                BasePrice = 90m, TimeDuration = 120m, MinimumPrice = 130m, IsActive = true
            });
            _db.Users.AddRange(
                new User { Id = AdminId, FirstName = "Adam", LastName = "Admin", Email = "a@example.invalid", Role = UserRole.Admin, IsActive = true },
                new User { Id = HouseholdId, FirstName = "Hal", LastName = "Home", Email = "h@example.invalid", Role = UserRole.Customer, IsActive = true },
                new User { Id = BusinessId, FirstName = "Bea", LastName = "Business", Email = "b@example.invalid", Role = UserRole.Customer, IsBusiness = true, IsActive = true });
            _db.SaveChanges();
        }

        public void Dispose() => _db.Dispose();

        // ── The creation rule ──────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(false, null, null, null, false)]
        [InlineData(true, null, null, null, true)]      // a business customer
        [InlineData(false, 5, null, null, true)]        // billed to a commercial client
        [InlineData(false, null, 7, null, true)]        // performed under a contract
        [InlineData(true, null, null, false, false)]    // the admin said Regular
        [InlineData(false, null, null, true, true)]     // the admin said Commercial
        public void TheCreationRule(bool isBusiness, int? clientId, int? contractId, bool? adminChoice, bool expected)
        {
            Assert.Equal(expected, OrderCommercialPolicy.ResolveForNewOrder(isBusiness, clientId, contractId, adminChoice));
        }

        [Fact]
        public async Task ABusinessCustomersBookingIsCommercial_AHouseholdsIsRegular()
        {
            var service = BookingService();

            var business = await service.CreateOrderAsync(Booking(), BusinessId, allowCustomPricing: false);
            var household = await service.CreateOrderAsync(Booking(), HouseholdId, allowCustomPricing: false);

            Assert.True(business.IsCommercial);
            Assert.False(household.IsCommercial);
        }

        [Fact]
        public async Task TheAdminModeChoiceWinsEitherWay()
        {
            var service = BookingService();

            var regularForBusiness = await service.CreateOrderAsync(Booking(), BusinessId, false,
                new BookingCreationOptions { BookedByAdminUserId = AdminId, IsCommercial = false });
            var commercialForHousehold = await service.CreateOrderAsync(Booking(), HouseholdId, false,
                new BookingCreationOptions { BookedByAdminUserId = AdminId, IsCommercial = true });

            Assert.False(regularForBusiness.IsCommercial);
            Assert.True(commercialForHousehold.IsCommercial);
        }

        [Fact]
        public async Task AnOrderBilledToACommercialClientIsCommercial()
        {
            var order = await BookingService().CreateOrderAsync(Booking(), HouseholdId, false,
                new BookingCreationOptions { BookedByAdminUserId = AdminId, ContractClientId = 42, PaymentMethod = PaymentMethod.Invoice });

            Assert.True(order.IsCommercial);
        }

        [Fact]
        public void TheSelfServiceBookingDtoHasNoWayToFileAnOrder()
        {
            // The flag travels on the ADMIN wrapper only. A customer's request body cannot carry it.
            Assert.Null(typeof(CreateBookingDto).GetProperty("IsCommercial"));
            Assert.NotNull(typeof(CreateBookingForUserDto).GetProperty("IsCommercial"));
        }

        // ── Moving an order ────────────────────────────────────────────────────────────────

        [Fact]
        public async Task AnAdminMoveIsStoredOnTheOrderAndAudited()
        {
            var order = await BookingService().CreateOrderAsync(Booking(), HouseholdId, false);

            var result = await Controller().UpdateOrderType(order.Id, new UpdateOrderTypeDto { IsCommercial = true });

            Assert.IsType<OkObjectResult>(result);
            Assert.True((await _db.Orders.AsNoTracking().FirstAsync(o => o.Id == order.Id, cancellationToken: TestContext.Current.CancellationToken)).IsCommercial);
            var audit = await _db.AuditLogs.AsNoTracking().SingleAsync(a => a.EntityType == AuditEntityTypes.OrderType, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal((long)order.Id, audit.EntityId);
            Assert.Contains("Commercial", audit.NewValues);
        }

        [Fact]
        public async Task ChangingTheCustomersBusinessFlagLater_NeverReFilesExistingOrders()
        {
            var service = BookingService();
            var moved = await service.CreateOrderAsync(Booking(), HouseholdId, false);
            await Controller().UpdateOrderType(moved.Id, new UpdateOrderTypeDto { IsCommercial = true });
            var untouched = await service.CreateOrderAsync(Booking(), HouseholdId, false);

            var user = await _db.Users.FirstAsync(u => u.Id == HouseholdId, cancellationToken: TestContext.Current.CancellationToken);
            user.IsBusiness = true;
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            Assert.True((await _db.Orders.AsNoTracking().FirstAsync(o => o.Id == moved.Id, cancellationToken: TestContext.Current.CancellationToken)).IsCommercial);
            Assert.False((await _db.Orders.AsNoTracking().FirstAsync(o => o.Id == untouched.Id, cancellationToken: TestContext.Current.CancellationToken)).IsCommercial);

            // ...while the customer's NEXT booking follows the new flag.
            Assert.True((await service.CreateOrderAsync(Booking(), HouseholdId, false)).IsCommercial);
        }

        [Fact]
        public async Task TheExportCarriesTheOrderType()
        {
            var service = BookingService();
            await service.CreateOrderAsync(Booking(), BusinessId, false);
            await service.CreateOrderAsync(Booking(), HouseholdId, false);

            var file = Assert.IsType<FileContentResult>(await Controller().ExportOrders(
                new OrdersExportRequestDto { Columns = new() { "orderId", "orderType" } }));

            using var wb = new XLWorkbook(new MemoryStream(file.FileContents));
            var ws = wb.Worksheets.First();
            var col = ws.Row(1).CellsUsed().First(c => c.GetString() == "Order Type").Address.ColumnNumber;
            Assert.Equal(new[] { "Commercial", "Regular" },
                ws.RowsUsed().Skip(1).Select(r => r.Cell(col).GetString()).ToArray());
        }

        // ── Helpers ────────────────────────────────────────────────────────────────────────

        private static CreateBookingDto Booking() => new()
        {
            ServiceTypeId = ResidentialTypeId,
            ServiceDate = DateTime.UtcNow.Date.AddDays(7),
            ServiceTime = "10:00",
            ContactFirstName = "Test", ContactLastName = "Booking",
            ContactEmail = "test@example.invalid", ContactPhone = "5551234567",
            ServiceAddress = "1 Test St", City = "Brooklyn", State = "New York", ZipCode = "11201",
            EntryMethod = "Someone will be home",
            Services = new List<BookingServiceDto>(),
            ExtraServices = new List<BookingExtraServiceDto>()
        };

        private BookingCreationService BookingService() =>
            new(_db, new NoLoyalty(), null!, NullLogger<BookingCreationService>.Instance);

        private AdminOrdersController Controller()
        {
            var audit = new AuditService(_db, new HttpContextAccessor(), NullLogger<AuditService>.Instance);
            var controller = new AdminOrdersController(_db, null!, audit, null!, null!, null!, null!, null!,
                null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
                NullLogger<AdminOrdersController>.Instance);
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.NameIdentifier, AdminId.ToString()),
                        new Claim("UserId", AdminId.ToString()),
                        new Claim("Role", "Admin")
                    }, "test"))
                }
            };
            return controller;
        }

        private sealed class NoLoyalty : ILoyaltyDiscountService
        {
            public Task<(decimal amount, decimal percentage)> CalculateForOrderAsync(int userId, decimal subTotal) => Task.FromResult((0m, 0m));
            public (decimal loyaltyAmount, decimal loyaltyPercentage, decimal subscriptionAmount, decimal promoAmount)
                ResolveStacking(decimal loyaltyCandidateAmount, decimal loyaltyCandidatePercentage, decimal subscriptionAmount, decimal promoAmount)
                => (loyaltyCandidateAmount, loyaltyCandidatePercentage, subscriptionAmount, promoAmount);
            public Task ApplyToOrderAsync(int orderId) => Task.CompletedTask;
            public Task ReverseFromOrderAsync(int orderId) => Task.CompletedTask;
            public Task<LoyaltyDiscountDto> GetForUserAsync(int userId) => throw new NotSupportedException();
            public Task<LoyaltyDiscountDto> SetManualAsync(int userId, decimal percentage, int adminUserId, bool isLifetime = false) => throw new NotSupportedException();
            public Task<LoyaltyDiscountDto> ClearAsync(int userId, int adminUserId) => throw new NotSupportedException();
        }
    }
}
