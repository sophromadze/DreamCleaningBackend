using System.Security.Claims;
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
    /// Pending → Done needs approval for Admins without the direct-save grant (2026-10).
    ///
    /// The rule is <see cref="OrderDoneApprovalPolicy"/>; these tests drive it through the real
    /// endpoints, because the point of the feature is that the SERVER refuses the move - the panel
    /// relabelling its button is only a convenience. They also pin the other half of the promise:
    /// an approved request leaves the order exactly as a direct Done would (status, payment
    /// method, bubble points), and a rejected one leaves it untouched.
    /// </summary>
    public class OrderDoneApprovalTests : IDisposable
    {
        private const int SuperAdminId = 1;
        private const int GrantedAdminId = 2;
        private const int AdminId = 3;
        private const int CustomerId = 10;

        private readonly ApplicationDbContext _db;
        private readonly RecordingBubblePoints _points = new();

        public OrderDoneApprovalTests()
        {
            _db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase($"done-approval-{Guid.NewGuid()}", b => b.EnableNullChecks(false))
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options);

            _db.Users.AddRange(
                new User { Id = SuperAdminId, FirstName = "Sue", LastName = "Super", Email = "sa@example.invalid", Role = UserRole.SuperAdmin, IsActive = true },
                new User { Id = GrantedAdminId, FirstName = "Gina", LastName = "Granted", Email = "ga@example.invalid", Role = UserRole.Admin, CanEditOrdersWithoutApproval = true, IsActive = true },
                new User { Id = AdminId, FirstName = "Adam", LastName = "Admin", Email = "a@example.invalid", Role = UserRole.Admin, IsActive = true },
                new User { Id = CustomerId, FirstName = "Cleo", LastName = "Customer", Email = "c@example.invalid", Role = UserRole.Customer, IsActive = true });
            _db.SaveChanges();
        }

        public void Dispose() => _db.Dispose();

        // ── The policy ─────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("Pending", true)]
        [InlineData("Cancelled", true)]
        [InlineData("Refunded", true)]
        [InlineData(null, true)]          // created straight at Done (recreate)
        [InlineData("Active", false)]     // finishing a confirmed job stays one click
        [InlineData("Done", false)]
        public void OnlyMovesIntoDoneFromSomethingOtherThanActiveAreGated(string? from, bool gated)
        {
            Assert.Equal(gated, OrderDoneApprovalPolicy.IsGatedMove(from, "Done"));
        }

        [Theory]
        [InlineData("Active")]
        [InlineData("Pending")]
        [InlineData("Cancelled")]
        public void MovesThatAreNotIntoDoneAreNeverGated(string to)
        {
            Assert.False(OrderDoneApprovalPolicy.RequiresApproval(UserRole.Admin, false, "Pending", to));
        }

        [Theory]
        [InlineData(UserRole.SuperAdmin, false, false)]
        [InlineData(UserRole.Admin, true, false)]   // the direct-save grant is the Done grant
        [InlineData(UserRole.Admin, false, true)]
        [InlineData(UserRole.Moderator, false, true)]
        [InlineData(UserRole.Moderator, true, true)] // the grant only means something on an Admin
        public void WhoMustRequest_IsWhoCannotApplyOrderEditsDirectly(UserRole role, bool granted, bool mustRequest)
        {
            Assert.Equal(mustRequest, OrderDoneApprovalPolicy.RequiresApproval(role, granted, "Pending", "Done"));
            Assert.Equal(!mustRequest, OrderDoneApprovalPolicy.CanReview(role, granted));
        }

        // ── The status endpoint (row button + Done modal) ─────────────────────────────────

        [Fact]
        public async Task AnUngrantedAdmin_PendingToDone_SendsARequestAndChangesNothing()
        {
            var orderId = SeedOrder("Pending");

            var result = await Controller(AdminId, UserRole.Admin).UpdateOrderStatus(orderId,
                new UpdateOrderStatusDto { Status = "Done", PaymentMethod = "Zelle", PaymentReference = "ZL-1", SendReviewRequest = true });

            Assert.IsType<AcceptedResult>(result);
            var order = await _db.Orders.AsNoTracking().FirstAsync(o => o.Id == orderId, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("Pending", order.Status);
            Assert.Equal(PaymentMethod.Normal, order.PaymentMethod);   // nothing applied yet
            Assert.Empty(_points.Completed);

            var request = await _db.PendingOrderEdits.AsNoTracking().SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(PendingOrderEditKinds.Done, request.Kind);
            Assert.Equal("Pending", request.Status);
            Assert.Equal(AdminId, request.RequestedByUserId);
            Assert.Contains("Zelle", request.ProposedChangesJson);
            Assert.Contains("Status", request.FieldChangesJson);
        }

        [Fact]
        public async Task ASecondDoneRequestOnTheSameOrderIsRefused()
        {
            var orderId = SeedOrder("Pending");
            var controller = Controller(AdminId, UserRole.Admin);

            await controller.UpdateOrderStatus(orderId, new UpdateOrderStatusDto { Status = "Done" });
            var second = await controller.UpdateOrderStatus(orderId, new UpdateOrderStatusDto { Status = "Done" });

            Assert.IsType<ConflictObjectResult>(second);
            Assert.Equal(1, await _db.PendingOrderEdits.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task AnUngrantedAdmin_CancelledToDone_IsGatedToo()
        {
            var orderId = SeedOrder("Cancelled");

            var result = await Controller(AdminId, UserRole.Admin).UpdateOrderStatus(orderId,
                new UpdateOrderStatusDto { Status = "Done" });

            Assert.IsType<AcceptedResult>(result);
            Assert.Equal("Cancelled", (await _db.Orders.AsNoTracking().FirstAsync(cancellationToken: TestContext.Current.CancellationToken)).Status);
        }

        [Fact]
        public async Task AnUngrantedAdmin_ActiveToDone_StaysDirect()
        {
            var orderId = SeedOrder("Active");

            var result = await Controller(AdminId, UserRole.Admin).UpdateOrderStatus(orderId,
                new UpdateOrderStatusDto { Status = "Done", PaymentMethod = "Cash" });

            Assert.IsType<OkObjectResult>(result);
            var order = await _db.Orders.AsNoTracking().FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("Done", order.Status);
            Assert.Equal(PaymentMethod.Cash, order.PaymentMethod);
            Assert.Equal(AdminId, order.ManualPaymentRecordedByUserId);
            Assert.Equal(new[] { orderId }, _points.Completed);
            Assert.Empty(_db.PendingOrderEdits);
        }

        [Theory]
        [InlineData(GrantedAdminId, UserRole.Admin)]
        [InlineData(SuperAdminId, UserRole.SuperAdmin)]
        public async Task ReviewersMarkPendingDoneDirectly(int userId, UserRole role)
        {
            var orderId = SeedOrder("Pending");

            var result = await Controller(userId, role).UpdateOrderStatus(orderId,
                new UpdateOrderStatusDto { Status = "Done" });

            Assert.IsType<OkObjectResult>(result);
            Assert.Equal("Done", (await _db.Orders.AsNoTracking().FirstAsync(cancellationToken: TestContext.Current.CancellationToken)).Status);
            Assert.Equal(new[] { orderId }, _points.Completed);
            Assert.Empty(_db.PendingOrderEdits);
        }

        [Fact]
        public async Task AnUnknownStatusIsRejectedInsteadOfPersisted()
        {
            var orderId = SeedOrder("Pending");

            var result = await Controller(SuperAdminId, UserRole.SuperAdmin).UpdateOrderStatus(orderId,
                new UpdateOrderStatusDto { Status = "Finished" });

            Assert.IsType<BadRequestObjectResult>(result);
            Assert.Equal("Pending", (await _db.Orders.AsNoTracking().FirstAsync(cancellationToken: TestContext.Current.CancellationToken)).Status);
        }

        [Fact]
        public async Task StatusSpellingIsNormalised()
        {
            var orderId = SeedOrder("Active");

            await Controller(SuperAdminId, UserRole.SuperAdmin).UpdateOrderStatus(orderId,
                new UpdateOrderStatusDto { Status = "done" });

            Assert.Equal("Done", (await _db.Orders.AsNoTracking().FirstAsync(cancellationToken: TestContext.Current.CancellationToken)).Status);
        }

        // ── Approve / reject ───────────────────────────────────────────────────────────────

        [Fact]
        public async Task ApprovingTheRequest_HasExactlyTheSideEffectsOfADirectDone()
        {
            var orderId = SeedOrder("Pending");
            await Controller(AdminId, UserRole.Admin).UpdateOrderStatus(orderId,
                new UpdateOrderStatusDto { Status = "Done", PaymentMethod = "Zelle", PaymentReference = "ZL-9", PaymentNotes = "paid on site" });
            var requestId = (await _db.PendingOrderEdits.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).Id;

            var result = await Controller(SuperAdminId, UserRole.SuperAdmin).ApprovePendingOrderEdit(requestId);

            Assert.IsType<OkObjectResult>(result);
            var order = await _db.Orders.AsNoTracking().FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("Done", order.Status);
            Assert.Equal(PaymentMethod.Zelle, order.PaymentMethod);
            Assert.Equal("ZL-9", order.PaymentReference);
            Assert.Equal("paid on site", order.PaymentNotes);
            Assert.NotNull(order.ManualPaymentRecordedAt);
            // The requester took the money; the reviewer only agreed.
            Assert.Equal(AdminId, order.ManualPaymentRecordedByUserId);
            Assert.Equal(new[] { orderId }, _points.Completed);

            var request = await _db.PendingOrderEdits.AsNoTracking().SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("Approved", request.Status);
            Assert.Equal(SuperAdminId, request.ReviewedByUserId);
        }

        [Fact]
        public async Task AGrantedAdminMayApproveADoneRequest()
        {
            var orderId = SeedOrder("Pending");
            await Controller(AdminId, UserRole.Admin).UpdateOrderStatus(orderId, new UpdateOrderStatusDto { Status = "Done" });
            var requestId = (await _db.PendingOrderEdits.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).Id;

            Assert.IsType<OkObjectResult>(await Controller(GrantedAdminId, UserRole.Admin).ApprovePendingOrderEdit(requestId));
            Assert.Equal("Done", (await _db.Orders.AsNoTracking().FirstAsync(cancellationToken: TestContext.Current.CancellationToken)).Status);
        }

        [Fact]
        public async Task AnUngrantedAdminCannotApproveTheirOwnRequest()
        {
            var orderId = SeedOrder("Pending");
            var admin = Controller(AdminId, UserRole.Admin);
            await admin.UpdateOrderStatus(orderId, new UpdateOrderStatusDto { Status = "Done" });
            var requestId = (await _db.PendingOrderEdits.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).Id;

            Assert.IsType<ForbidResult>(await admin.ApprovePendingOrderEdit(requestId));
            Assert.Equal("Pending", (await _db.Orders.AsNoTracking().FirstAsync(cancellationToken: TestContext.Current.CancellationToken)).Status);
        }

        [Fact]
        public async Task RejectingTheRequest_LeavesTheOrderPending_AndTheRequesterSeesWhy()
        {
            var orderId = SeedOrder("Pending");
            await Controller(AdminId, UserRole.Admin).UpdateOrderStatus(orderId, new UpdateOrderStatusDto { Status = "Done" });
            var requestId = (await _db.PendingOrderEdits.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).Id;

            await Controller(SuperAdminId, UserRole.SuperAdmin).RejectPendingOrderEdit(requestId,
                new RejectPendingOrderEditDto { RejectReason = "Customer has not paid yet" });

            Assert.Equal("Pending", (await _db.Orders.AsNoTracking().FirstAsync(cancellationToken: TestContext.Current.CancellationToken)).Status);
            Assert.Empty(_points.Completed);

            // The requester - who cannot open the reviewer queue - reads the outcome on the order.
            var list = Assert.IsType<OkObjectResult>(
                (await Controller(AdminId, UserRole.Admin).GetOrderChangeRequests(orderId)).Result);
            var row = Assert.Single(Assert.IsAssignableFrom<List<OrderChangeRequestDto>>(list.Value));
            Assert.Equal("Done", row.Kind);
            Assert.Equal("Rejected", row.Status);
            Assert.Equal("Customer has not paid yet", row.RejectReason);
            Assert.Equal("Sue Super", row.ReviewedByName);
            Assert.True(row.IsMine);
        }

        [Fact]
        public async Task ARejectedRequestDoesNotBlockANewOne()
        {
            var orderId = SeedOrder("Pending");
            var admin = Controller(AdminId, UserRole.Admin);
            await admin.UpdateOrderStatus(orderId, new UpdateOrderStatusDto { Status = "Done" });
            await Controller(SuperAdminId, UserRole.SuperAdmin).RejectPendingOrderEdit(
                (await _db.PendingOrderEdits.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).Id, new RejectPendingOrderEditDto());

            Assert.IsType<AcceptedResult>(await admin.UpdateOrderStatus(orderId, new UpdateOrderStatusDto { Status = "Done" }));
        }

        [Fact]
        public async Task ARequestOvertakenByACancellationIsNotApplied()
        {
            var orderId = SeedOrder("Pending");
            await Controller(AdminId, UserRole.Admin).UpdateOrderStatus(orderId, new UpdateOrderStatusDto { Status = "Done" });
            var requestId = (await _db.PendingOrderEdits.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).Id;

            var order = await _db.Orders.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
            order.Status = "Cancelled";
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var result = await Controller(SuperAdminId, UserRole.SuperAdmin).ApprovePendingOrderEdit(requestId);

            Assert.IsType<BadRequestObjectResult>(result);
            Assert.Equal("Cancelled", (await _db.Orders.AsNoTracking().FirstAsync(cancellationToken: TestContext.Current.CancellationToken)).Status);
            Assert.Equal("Pending", (await _db.PendingOrderEdits.AsNoTracking().SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).Status);
            Assert.Empty(_points.Completed);
        }

        [Fact]
        public async Task ARequestForAnOrderSomebodyAlreadyFinishedClosesWithoutDoubleCounting()
        {
            var orderId = SeedOrder("Pending");
            await Controller(AdminId, UserRole.Admin).UpdateOrderStatus(orderId, new UpdateOrderStatusDto { Status = "Done" });
            var requestId = (await _db.PendingOrderEdits.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).Id;
            await Controller(SuperAdminId, UserRole.SuperAdmin).UpdateOrderStatus(orderId, new UpdateOrderStatusDto { Status = "Done" });

            var result = await Controller(SuperAdminId, UserRole.SuperAdmin).ApprovePendingOrderEdit(requestId);

            Assert.IsType<OkObjectResult>(result);
            Assert.Equal(new[] { orderId }, _points.Completed);   // once, from the direct Done
            Assert.Equal("Approved", (await _db.PendingOrderEdits.AsNoTracking().SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).Status);
        }

        [Fact]
        public async Task TheReviewerQueueAndDetailCarryTheKind()
        {
            var orderId = SeedOrder("Pending");
            await Controller(AdminId, UserRole.Admin).UpdateOrderStatus(orderId,
                new UpdateOrderStatusDto { Status = "Done", PaymentMethod = "Check", SendReviewRequest = true });
            var reviewer = Controller(SuperAdminId, UserRole.SuperAdmin);

            var list = Assert.IsAssignableFrom<List<PendingOrderEditListDto>>(
                Assert.IsType<OkObjectResult>((await reviewer.GetPendingOrderEdits()).Result).Value);
            Assert.Equal("Done", Assert.Single(list).Kind);
        }

        // ── The transition hooks every status path shares ─────────────────────────────────

        [Fact]
        public async Task LeavingDoneReversesThePoints_AndEnteringDoneAwardsThem()
        {
            var orderId = SeedOrder("Done");
            var service = StatusService();

            await service.RunTransitionHooksAsync(orderId, "Done", "Active");
            await service.RunTransitionHooksAsync(orderId, "Active", "Done");
            await service.RunTransitionHooksAsync(orderId, "Done", "Done");   // no transition

            Assert.Equal(new[] { orderId }, _points.Reversed);
            Assert.Equal(new[] { orderId }, _points.Completed);
        }

        [Fact]
        public async Task ADoneRequestCannotBeSentForAnOrderThatIsAlreadyDone()
        {
            var orderId = SeedOrder("Done");
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                StatusService().SubmitDoneRequestAsync(orderId, new DoneRequestPayload(), AdminId));
        }

        // ── Helpers ────────────────────────────────────────────────────────────────────────

        private int SeedOrder(string status)
        {
            var order = new Order
            {
                UserId = CustomerId,
                ServiceTypeId = 1,
                Status = status,
                ServiceDate = DateTime.UtcNow.Date,
                OrderDate = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                ContactFirstName = "Cleo",
                ContactLastName = "Customer",
                ContactEmail = "c@example.invalid",
                ContactPhone = "2125550100",
                ServiceAddress = "1 Test St",
                City = "Brooklyn",
                State = "NY",
                ZipCode = "11201",
                SubTotal = 200m,
                Tax = 17.75m,
                Total = 217.75m
            };
            _db.Orders.Add(order);
            _db.SaveChanges();
            return order.Id;
        }

        private OrderStatusChangeService StatusService() => new(
            _db, _points, new NoLoyalty(),
            new AuditService(_db, new HttpContextAccessor(), NullLogger<AuditService>.Instance),
            null!, null!, NullLogger<OrderStatusChangeService>.Instance);

        private AdminOrdersController Controller(int userId, UserRole role)
        {
            var audit = new AuditService(_db, new HttpContextAccessor(), NullLogger<AuditService>.Instance);
            var controller = new AdminOrdersController(_db, null!, audit, null!, null!, null!, null!, null!,
                _points, new NoLoyalty(), null!, null!, null!, null!, null!, null!, null!,
                StatusService(),
                NullLogger<AdminOrdersController>.Instance);
            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim("UserId", userId.ToString()),
                new Claim("Role", role.ToString()),
                new Claim(ClaimTypes.Role, role.ToString())
            }, "test");
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
            };
            return controller;
        }

        private sealed class RecordingBubblePoints : IBubblePointsService
        {
            public List<int> Completed { get; } = new();
            public List<int> Reversed { get; } = new();

            public Task ProcessOrderCompletion(int orderId) { Completed.Add(orderId); return Task.CompletedTask; }
            public Task ReverseOrderCompletion(int orderId) { Reversed.Add(orderId); return Task.CompletedTask; }

            public Task<string> GetTier(decimal totalSpent) => throw new NotSupportedException();
            public Task<decimal> GetMultiplier(string tier) => throw new NotSupportedException();
            public Task AddPoints(int userId, int points, string type, string description, int? orderId = null) => throw new NotSupportedException();
            public Task GrantWelcomeBonus(int userId) => throw new NotSupportedException();
            public Task<BubbleRewardsSummaryDto> GetSummary(int userId) => throw new NotSupportedException();
            public Task<HeaderSummaryDto> GetHeaderSummary(int userId) => throw new NotSupportedException();
            public Task<RedemptionResultDto> RedeemPoints(int userId, int points, int orderId) => throw new NotSupportedException();
            public Task<PagedResult<BubblePointsHistoryDto>> GetHistory(int userId, int page, int pageSize, bool adminView = false) => throw new NotSupportedException();
            public Task AdminAdjustPoints(int userId, int points, string description) => throw new NotSupportedException();
            public Task AdminGrantCredit(int userId, decimal amount, string description) => throw new NotSupportedException();
            public Task AdminGrantReviewBonus(int userId) => throw new NotSupportedException();
            public Task<(decimal creditAmount, bool valid, string message)> GetPointsCreditForBooking(int points) => throw new NotSupportedException();
            public Task DeductPointsForBooking(int userId, int points, int orderId) => throw new NotSupportedException();
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
