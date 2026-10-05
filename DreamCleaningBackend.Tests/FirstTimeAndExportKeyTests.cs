using System.Text.Json;
using ClosedXML.Excel;
using DreamCleaningBackend.Controllers;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// 2026-10 cleanup of the last name-based rules:
    ///  - the "firstUse" promo marker is gone; the first-time discount travels as the customer's
    ///    granted first-time special offer and prices exactly as before;
    ///  - the admin special-offer editor can no longer wipe MinimumOrderAmount;
    ///  - the Excel exports decide Residential (Deep / Regular) by ServiceKey, not by name.
    /// </summary>
    public class FirstTimeAndExportKeyTests : IDisposable
    {
        private readonly ApplicationDbContext _db;

        public FirstTimeAndExportKeyTests()
        {
            _db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase($"first-time-export-{Guid.NewGuid()}", b => b.EnableNullChecks(false))
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
        }

        public void Dispose() => _db.Dispose();

        private BookingCreationService Booking() => new(_db, null!, null!, NullLogger<BookingCreationService>.Instance);

        /// <summary>Production's first-time offer (Custom type, 10%), granted to a first-time customer.</summary>
        private async Task SeedFirstTimeCustomer(string offerName = "First Time Customer")
        {
            _db.Users.Add(new User { Id = 1, FirstName = "Casey", LastName = "Client", Email = "casey@example.invalid", FirstTimeOrder = true, Role = UserRole.Customer });
            _db.SpecialOffers.Add(new SpecialOffer
            {
                Id = 1, Name = offerName, Description = "Get 10% off on your first order!", IsPercentage = true,
                DiscountValue = 10m, Type = OfferType.Custom, RequiresFirstTimeCustomer = true, IsActive = true,
                OfferKey = "first-time", CreatedAt = DateTime.UtcNow
            });
            _db.UserSpecialOffers.Add(new UserSpecialOffer { Id = 11, UserId = 1, SpecialOfferId = 1, GrantedAt = DateTime.UtcNow });
            await _db.SaveChangesAsync();
        }

        [Fact]
        public async Task TheFirstTimeDiscountIsTheGrantedOffer_TenPercent_RenamedOrNot()
        {
            await SeedFirstTimeCustomer();
            var (discount, _) = await Booking().ResolveDiscountsAsync(
                new CreateBookingDto { UserSpecialOfferId = 11, SpecialOfferId = 1 }, orderUserId: 1, subTotal: 275.55m);
            Assert.Equal(27.56m, discount);

            (await _db.SpecialOffers.SingleAsync()).Name = "New Client Deal";
            await _db.SaveChangesAsync();
            var (renamed, _) = await Booking().ResolveDiscountsAsync(
                new CreateBookingDto { UserSpecialOfferId = 11, SpecialOfferId = 1 }, orderUserId: 1, subTotal: 275.55m);
            Assert.Equal(discount, renamed);
        }

        [Fact]
        public async Task FirstUseIsNoLongerAFirstTimeMarker()
        {
            // Before: refused with "The first-time discount is no longer available" (production's offer
            // is not a FirstTime-TYPE row). Now it is simply not a code - the page never sends it.
            await SeedFirstTimeCustomer();
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Booking().ResolveDiscountsAsync(
                new CreateBookingDto { PromoCode = "firstUse" }, orderUserId: 1, subTotal: 275.55m));
            Assert.Equal("Invalid promo code.", ex.Message);
        }

        [Fact]
        public async Task NoOfferAndNoCode_IsFullPrice()
        {
            await SeedFirstTimeCustomer();
            var (discount, subscription) = await Booking().ResolveDiscountsAsync(
                new CreateBookingDto(), orderUserId: 1, subTotal: 275.55m);
            Assert.Equal(0m, discount);
            Assert.Equal(0m, subscription);
        }

        // ── MinimumOrderAmount on the admin special-offer editor ───────────────────────────────

        private static UpdateSpecialOfferDto Body(string json) =>
            JsonSerializer.Deserialize<UpdateSpecialOfferDto>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        [Fact]
        public async Task AnUpdateWithoutMinimumOrderAmountKeepsIt_NullClearsIt()
        {
            _db.SpecialOffers.Add(new SpecialOffer
            {
                Id = 5, Name = "Spring", Description = "d", IsPercentage = true, DiscountValue = 15m,
                Type = OfferType.Seasonal, IsActive = true, MinimumOrderAmount = 150m, CreatedAt = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();
            var svc = new SpecialOfferService(_db, new AuditService(_db, new HttpContextAccessor(), NullLogger<AuditService>.Instance));

            // The old inline editor's body: no minimumOrderAmount at all.
            await svc.UpdateSpecialOffer(5, Body("""{"name":"Spring","description":"d","isPercentage":true,"discountValue":15,"isActive":true}"""));
            Assert.Equal(150m, (await _db.SpecialOffers.AsNoTracking().SingleAsync()).MinimumOrderAmount);

            // The fixed editor sends the value it loaded.
            await svc.UpdateSpecialOffer(5, Body("""{"name":"Spring","description":"d","isPercentage":true,"discountValue":15,"isActive":true,"minimumOrderAmount":150}"""));
            Assert.Equal(150m, (await _db.SpecialOffers.AsNoTracking().SingleAsync()).MinimumOrderAmount);

            await svc.UpdateSpecialOffer(5, Body("""{"name":"Spring","description":"d","isPercentage":true,"discountValue":15,"isActive":true,"minimumOrderAmount":null}"""));
            Assert.Null((await _db.SpecialOffers.AsNoTracking().SingleAsync()).MinimumOrderAmount);
        }

        // ── Excel exports: Residential by ServiceKey ───────────────────────────────────────────

        private async Task SeedOrders()
        {
            _db.ServiceTypes.AddRange(
                new ServiceType { Id = 1, Name = "Home Care", ServiceKey = "residential", IsActive = true },
                new ServiceType { Id = 2, Name = "Residential Office Cleaning", ServiceKey = "office", IsActive = true },
                new ServiceType { Id = 3, Name = "Residential Cleaning", ServiceKey = null, IsActive = true });
            _db.ExtraServices.Add(new ExtraService { Id = 1, Name = "Thorough Clean", ExtraServiceKey = "deep-cleaning", IsDeepCleaning = true, IsActive = true });
            for (var i = 1; i <= 4; i++)
                _db.Users.Add(new User { Id = 100 + i, FirstName = "C" + i, LastName = "L", Email = $"c{i}@example.invalid", Role = UserRole.Customer });
            Order Make(int id, int userId, int typeId) => new()
            {
                Id = id, UserId = userId, ServiceTypeId = typeId, ServiceDate = DateTime.Today.AddDays(id),
                ServiceTime = TimeSpan.FromHours(9), Status = "Active", ContactEmail = "x@example.invalid",
                ContactFirstName = "C", ContactLastName = "L", OrderDate = DateTime.UtcNow, CreatedAt = DateTime.UtcNow
            };
            _db.Orders.AddRange(Make(1, 101, 1), Make(2, 102, 1), Make(3, 103, 2), Make(4, 104, 3));
            _db.OrderExtraServices.Add(new OrderExtraService { Id = 1, OrderId = 1, ExtraServiceId = 1, Quantity = 1 });
            await _db.SaveChangesAsync();
        }

        private static List<string> Column(FileContentResult file, string header)
        {
            using var wb = new XLWorkbook(new MemoryStream(file.FileContents));
            var ws = wb.Worksheets.First();
            var col = ws.Row(1).CellsUsed().First(c => c.GetString() == header).Address.ColumnNumber;
            return ws.RowsUsed().Skip(1).Select(r => r.Cell(col).GetString()).ToList();
        }

        [Fact]
        public async Task OrdersExportLabelsResidentialByKey()
        {
            await SeedOrders();
            var controller = new AdminOrdersController(_db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
                NullLogger<AdminOrdersController>.Instance)
            { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

            var file = Assert.IsType<FileContentResult>(await controller.ExportOrders(
                new OrdersExportRequestDto { Columns = new() { "orderId", "serviceType" } }));
            var labels = Column(file, "Service Type");

            // Renamed residential (keyed) -> Deep/Regular; an OFFICE type whose name says
            // "Residential" stays Office; an unkeyed type still goes by its name.
            Assert.Equal(new[] { "Deep", "Regular", "Residential Office", "Regular" }, labels);
        }

        [Fact]
        public async Task UsersExportLabelsResidentialByKey()
        {
            await SeedOrders();
            var controller = new AdminUsersController(_db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
                NullLogger<AdminUsersController>.Instance)
            { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

            var file = Assert.IsType<FileContentResult>(await controller.ExportUsers(
                new UsersExportRequestDto { Columns = new() { "userId", "lastServiceType" } }));
            Assert.Equal(new[] { "Deep", "Regular", "Residential Office", "Regular" }, Column(file, "Service Type"));
        }
    }
}
