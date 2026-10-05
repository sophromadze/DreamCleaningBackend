using DreamCleaningBackend.Controllers;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// 2026-10: the Users panel's History tab is a RECORD, not a statistic. <c>GET
    /// admin/users/{id}/orders</c> used to drop cancelled orders, so a customer who had cancelled
    /// twice showed a clean history. It now returns every order, with the refunded amount the
    /// RefundH pill needs. The profile statistics (<c>users/{id}/profile</c>) — the panel's Total
    /// Jobs and Total Spent — exclude cancelled AND refunded orders, and net part refunds out of
    /// what was spent (owner's rule, 2026-10).
    /// </summary>
    public class AdminUserHistoryTests : IDisposable
    {
        private readonly ApplicationDbContext _db;

        public AdminUserHistoryTests()
        {
            _db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase($"admin-user-history-{Guid.NewGuid()}", b => b.EnableNullChecks(false))
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
        }

        public void Dispose() => _db.Dispose();

        private AdminUsersController Controller() =>
            new(_db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
                NullLogger<AdminUsersController>.Instance)
            { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

        private async Task Seed()
        {
            _db.ServiceTypes.Add(new ServiceType { Id = 1, Name = "Residential Cleaning", ServiceKey = "residential", IsActive = true });
            _db.Users.Add(new User { Id = 7, FirstName = "Casey", LastName = "Client", Email = "casey@example.invalid", Role = UserRole.Customer });
            Order Make(int id, string status, decimal refunded = 0m, bool paid = true) => new()
            {
                Id = id, UserId = 7, ServiceTypeId = 1, ServiceDate = DateTime.Today.AddDays(id),
                ServiceTime = TimeSpan.FromHours(9), Status = status, Total = 100m + id, IsPaid = paid,
                PaymentMethod = PaymentMethod.Normal, TotalRefundedAmount = refunded,
                ContactEmail = "casey@example.invalid", ContactFirstName = "Casey", ContactLastName = "Client",
                OrderDate = DateTime.UtcNow.AddMinutes(id), CreatedAt = DateTime.UtcNow
            };
            _db.Orders.AddRange(
                Make(1, "Done"),
                Make(2, "Cancelled", paid: false),          // cancelled by the customer
                Make(3, "Refunded", refunded: 103m),        // fully refunded
                Make(4, "Cancelled", refunded: 34m));       // cancelled, fee kept (RefundH)
            await _db.SaveChangesAsync();
        }

        [Fact]
        public async Task HistoryReturnsEveryOrder_CancelledAndRefundedIncluded()
        {
            await Seed();

            var result = await Controller().GetUserOrders(7);
            var orders = Assert.IsType<List<OrderListDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);

            Assert.Equal(new[] { 4, 3, 2, 1 }, orders.Select(o => o.Id));
            Assert.Equal(new[] { "Cancelled", "Refunded", "Cancelled", "Done" }, orders.Select(o => o.Status));
            // The partial refund reaches the list, so the History pill can say RefundH.
            Assert.Equal(34m, orders.Single(o => o.Id == 4).TotalRefundedAmount);
            Assert.Equal(103m, orders.Single(o => o.Id == 3).TotalRefundedAmount);
        }

        [Fact]
        public async Task UsersList_OrderCount_IsRealOrdersOnly_SoTheNewReturningFilterAgreesWithThePanel()
        {
            await Seed();

            var result = await Controller().GetUsers();
            var value = (result.Result as ObjectResult)?.Value ?? result.Value;
            var json = System.Text.Json.JsonSerializer.Serialize(value);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            // Property names as serialized (PascalCase by default); either casing is accepted.
            static System.Text.Json.JsonElement Prop(System.Text.Json.JsonElement e, string name) =>
                e.EnumerateObject().First(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Value;
            var users = root.ValueKind == System.Text.Json.JsonValueKind.Array ? root : Prop(root, "users");
            var casey = users.EnumerateArray().Single(u => Prop(u, "id").GetInt32() == 7);

            // Done counts; the two Cancelled rows and the fully Refunded one do not (2026-10) - one
            // real order makes Casey "new", not "returning".
            Assert.Equal(1, Prop(casey, "totalOrdersCount").GetInt32());
        }

        [Fact]
        public async Task ProfileStatistics_ExcludeCancelledAndRefundedOrders()
        {
            await Seed();

            var result = await Controller().GetUserProfile(7);
            var detail = Assert.IsType<UserDetailDto>(Assert.IsType<OkObjectResult>(result.Result).Value);

            // Only the Done order is a real job: both Cancelled rows and the fully Refunded one stay out.
            Assert.Equal(1, detail.TotalOrders);
            Assert.Equal(101m, detail.TotalSpent);
        }

        [Fact]
        public async Task ProfileStatistics_CountAPartlyRefundedOrder_ButOnlyWhatWasNotRefunded()
        {
            await Seed();
            // Done, $105 paid, $20 refunded: still a job; $85 of it was spent.
            _db.Orders.Add(new Order
            {
                Id = 5, UserId = 7, ServiceTypeId = 1, ServiceDate = DateTime.Today.AddDays(5),
                ServiceTime = TimeSpan.FromHours(9), Status = "Done", Total = 105m, IsPaid = true,
                PaymentMethod = PaymentMethod.Normal, TotalRefundedAmount = 20m,
                ContactEmail = "casey@example.invalid", ContactFirstName = "Casey", ContactLastName = "Client",
                OrderDate = DateTime.UtcNow.AddMinutes(5), CreatedAt = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();

            var result = await Controller().GetUserProfile(7);
            var detail = Assert.IsType<UserDetailDto>(Assert.IsType<OkObjectResult>(result.Result).Value);

            Assert.Equal(2, detail.TotalOrders);
            Assert.Equal(101m + 85m, detail.TotalSpent);
        }

        [Fact]
        public async Task ProfileStatistics_TheRandaTaherCase_OneRefundedOfTwoEqualOrders()
        {
            // Production report (2026-10): two $659.70 orders, one refunded in full, showed
            // "Total Jobs 2 / Total Spent $1,319.40". It must read 1 / $659.70.
            _db.ServiceTypes.Add(new ServiceType { Id = 1, Name = "Residential Cleaning", ServiceKey = "residential", IsActive = true });
            _db.Users.Add(new User { Id = 8, FirstName = "Randa", LastName = "Example", Email = "r@example.invalid", Role = UserRole.Customer });
            Order Make(int id, string status, decimal refunded) => new()
            {
                Id = id, UserId = 8, ServiceTypeId = 1, ServiceDate = DateTime.Today.AddDays(id),
                ServiceTime = TimeSpan.FromHours(9), Status = status, Total = 659.70m, IsPaid = true,
                PaymentMethod = PaymentMethod.Normal, TotalRefundedAmount = refunded, StatusBeforeRefund = refunded > 0 ? "Active" : null,
                ContactEmail = "r@example.invalid", ContactFirstName = "Randa", ContactLastName = "Example",
                OrderDate = DateTime.UtcNow.AddMinutes(id), CreatedAt = DateTime.UtcNow
            };
            _db.Orders.AddRange(Make(11, "Done", 0m), Make(12, "Refunded", 659.70m));
            await _db.SaveChangesAsync();

            var result = await Controller().GetUserProfile(8);
            var detail = Assert.IsType<UserDetailDto>(Assert.IsType<OkObjectResult>(result.Result).Value);

            Assert.Equal(1, detail.TotalOrders);
            Assert.Equal(659.70m, detail.TotalSpent);
        }
    }
}
