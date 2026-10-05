using DreamCleaningBackend.Data;
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
    /// Bubble points on refunds and cancellations (owner's rules, 2026-10): new refunds take the
    /// order's points back (all of them, or in proportion to the money refunded), never below a zero
    /// balance; past refunds and cancellations go through a one-time correction - dry run, then apply
    /// exactly that plan, idempotently. The customer sees only "Balance adjustment"; staff see it all.
    /// </summary>
    public class RefundPointsReversalTests : IDisposable
    {
        private readonly ApplicationDbContext _db;

        public RefundPointsReversalTests()
        {
            _db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase($"refund-points-{Guid.NewGuid()}", b => b.EnableNullChecks(false))
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
        }

        public void Dispose() => _db.Dispose();

        private User AddUser(int id, int balance)
        {
            var user = new User { Id = id, FirstName = "Casey", LastName = $"Client{id}", Email = $"c{id}@example.invalid", Role = UserRole.Customer, BubblePoints = balance };
            _db.Users.Add(user);
            return user;
        }

        /// <summary>An order that earned <paramref name="earned"/> (+ <paramref name="streak"/>) points when it was done.</summary>
        private Order AddOrder(int id, int userId, decimal total, string status = "Done", decimal refunded = 0m, int earned = 100, int streak = 0)
        {
            var order = new Order
            {
                Id = id, UserId = userId, ServiceTypeId = 1, Status = status, Total = total, IsPaid = true,
                TotalRefundedAmount = refunded, ServiceDate = DateTime.Today, ServiceTime = TimeSpan.FromHours(9),
                ContactEmail = "c@example.invalid", OrderDate = DateTime.UtcNow, CreatedAt = DateTime.UtcNow
            };
            _db.Orders.Add(order);
            if (earned > 0) _db.BubblePointsHistories.Add(new BubblePointsHistory { UserId = userId, OrderId = id, Points = earned, Type = "OrderEarned", Description = "earned" });
            if (streak > 0) _db.BubblePointsHistories.Add(new BubblePointsHistory { UserId = userId, OrderId = id, Points = streak, Type = "StreakBonus", Description = "streak" });
            return order;
        }

        private Task Refund(Order order, decimal charged, decimal refundedTotal) =>
            RefundPointsReversal.ApplyForRefundAsync(_db, NullLogger.Instance, order, charged, refundedTotal, "the test");

        private int Balance(int userId) => _db.Users.AsNoTracking().Single(u => u.Id == userId).BubblePoints;
        private List<BubblePointsHistory> Reversals(int orderId) =>
            _db.BubblePointsHistories.AsNoTracking().Where(h => h.OrderId == orderId && RefundPointsReversal.ReversalTypes.Contains(h.Type)).ToList();

        // ── The rule ──────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(120, 200, 200, 120)]   // full refund: everything
        [InlineData(120, 200, 250, 120)]   // over-refund reads as full
        [InlineData(120, 200, 50, 30)]     // a quarter of the money -> a quarter of the points
        [InlineData(100, 300, 100, 33)]    // rounded
        [InlineData(100, 300, 150, 50)]
        [InlineData(0, 200, 200, 0)]       // nothing earned, nothing to take
        [InlineData(120, 200, 0, 0)]
        public void TargetReversal_IsAllForAFullRefund_AndProportionalForAPartOne(int earned, decimal charged, decimal refunded, int expected) =>
            Assert.Equal(expected, RefundPointsReversal.TargetReversal(earned, charged, refunded));

        // ── New refunds ───────────────────────────────────────────────────────────────────

        [Fact]
        public async Task FullRefund_ReversesEveryPointTheOrderEarned_StreakBonusIncluded()
        {
            AddUser(1, 500);
            var order = AddOrder(10, 1, 200m, earned: 100, streak: 20);
            await _db.SaveChangesAsync();

            await Refund(order, 200m, 200m);
            await _db.SaveChangesAsync();

            Assert.Equal(380, Balance(1));
            var row = Assert.Single(Reversals(10));
            Assert.Equal(-120, row.Points);
            Assert.Equal(RefundPointsReversal.RefundReversalType, row.Type);
            Assert.StartsWith("Refund/cancellation of order #10", row.Description);
            Assert.Contains("full refund", row.Description);
            Assert.Contains("Applied by the test", row.Description);
        }

        [Fact]
        public async Task PartialRefunds_AreProportional_Cumulative_AndASecondSyncTakesNothing()
        {
            AddUser(1, 500);
            var order = AddOrder(10, 1, 200m, earned: 120);
            await _db.SaveChangesAsync();

            await Refund(order, 200m, 50m);  await _db.SaveChangesAsync();   // a quarter
            Assert.Equal(470, Balance(1));
            await Refund(order, 200m, 100m); await _db.SaveChangesAsync();   // half in total: only the second quarter
            Assert.Equal(440, Balance(1));
            await Refund(order, 200m, 100m); await _db.SaveChangesAsync();   // the same total again (Stripe sync re-run)
            Assert.Equal(440, Balance(1));
            await Refund(order, 200m, 200m); await _db.SaveChangesAsync();   // the rest
            Assert.Equal(380, Balance(1));
            Assert.Equal(-120, Reversals(10).Sum(r => r.Points));
        }

        [Fact]
        public async Task SpentPoints_LeaveTheBalanceAtZero_NeverBelow_AndTheShortfallIsRecorded()
        {
            AddUser(1, 40);
            var order = AddOrder(10, 1, 200m, earned: 120);
            await _db.SaveChangesAsync();

            await Refund(order, 200m, 200m);
            await _db.SaveChangesAsync();

            Assert.Equal(0, Balance(1));
            var row = Assert.Single(Reversals(10));
            Assert.Equal(-40, row.Points);
            Assert.Contains("80 more were due but the customer had already spent them", row.Description);
        }

        // ── What the customer and staff see ─────────────────────────────────────────────────

        private BubblePointsService Points() => new(_db, RecurringDiscountRegressionTests.Stub<IBubbleRewardsSettingsService>(),
            RecurringDiscountRegressionTests.Stub<IReferralService>(), NullLogger<BubblePointsService>.Instance,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

        [Fact]
        public async Task TheCustomerSeesANeutralBalanceAdjustment_StaffSeeTheFullRecord()
        {
            AddUser(1, 500);
            var order = AddOrder(10, 1, 200m, earned: 120);
            await _db.SaveChangesAsync();
            await Refund(order, 200m, 200m);
            await _db.SaveChangesAsync();

            var customer = (await Points().GetHistory(1, 1, 50)).Items;
            var adjustment = Assert.Single(customer, h => h.Points < 0);
            Assert.Equal("BalanceAdjustment", adjustment.Type);
            Assert.Null(adjustment.Description);
            Assert.Null(adjustment.OrderId);
            // The rows still add up: +120 earned, -120 adjusted.
            Assert.Equal(-120, adjustment.Points);
            Assert.Equal(0, customer.Sum(h => h.Points));

            var admin = (await Points().GetHistory(1, 1, 50, adminView: true)).Items;
            var record = Assert.Single(admin, h => h.Points < 0);
            Assert.Equal(RefundPointsReversal.RefundReversalType, record.Type);
            Assert.Equal(10, record.OrderId);
            Assert.StartsWith("Refund/cancellation of order #10", record.Description);
        }

        [Fact]
        public async Task MovingARefundedOrderOutOfDone_DoesNotTakeThePointsASecondTime()
        {
            AddUser(1, 500);
            var order = AddOrder(10, 1, 200m, earned: 120);
            await _db.SaveChangesAsync();
            await Refund(order, 200m, 50m);              // 30 back
            await _db.SaveChangesAsync();
            Assert.Equal(470, Balance(1));

            await Points().ReverseOrderCompletion(10);   // admin moves it out of Done

            Assert.Equal(380, Balance(1));               // 120 in total, not 150
            Assert.Empty(_db.BubblePointsHistories.AsNoTracking().Where(h => h.OrderId == 10));
        }

        // ── One-time correction of past refunds and cancellations ───────────────────────────

        private async Task SeedPast()
        {
            AddUser(1, 1000);
            AddOrder(10, 1, 200m, status: "Refunded", refunded: 200m, earned: 120, streak: 30);  // full refund
            AddOrder(11, 1, 300m, status: "Done", refunded: 75m, earned: 100);                   // a quarter refunded
            AddOrder(12, 1, 150m, status: "Cancelled", earned: 60);                              // cancelled, still holds points
            AddOrder(13, 1, 150m, status: "Done", earned: 80);                                   // untouched
            AddUser(2, 50);
            AddOrder(20, 2, 200m, status: "Refunded", refunded: 200m, earned: 90);               // spent most of them
            AddUser(3, 500);
            var handled = AddOrder(30, 3, 200m, status: "Refunded", refunded: 200m, earned: 70); // refunded after deploy
            await _db.SaveChangesAsync();
            await Refund(handled, 200m, 200m);
            await _db.SaveChangesAsync();
        }

        [Fact]
        public async Task DryRun_ListsEveryAffectedOrder_AndChangesNothing()
        {
            await SeedPast();
            var historyBefore = _db.BubblePointsHistories.Count();

            var plan = await RefundPointsReversal.BuildCorrectionPlanAsync(_db);

            Assert.Equal(new[] { 10, 11, 12, 20 }, plan.Rows.Select(r => r.OrderId));
            var full = plan.Rows.Single(r => r.OrderId == 10);
            Assert.Equal(("full refund", 150, 150, 1000, 850), (full.Kind, full.PointsEarned, full.PointsToReverse, full.BalanceBefore, full.BalanceAfter));
            var part = plan.Rows.Single(r => r.OrderId == 11);
            Assert.Equal(("partial refund", 25, 850, 825), (part.Kind, part.PointsToReverse, part.BalanceBefore, part.BalanceAfter));
            Assert.Equal(("cancelled", 60), (plan.Rows.Single(r => r.OrderId == 12).Kind, plan.Rows.Single(r => r.OrderId == 12).PointsToReverse));
            var spent = plan.Rows.Single(r => r.OrderId == 20);
            Assert.Equal((90, 50, 0), (spent.PointsToReverse, spent.PointsTaken, spent.BalanceAfter));
            // Order 30 was refunded after deploy and handled automatically: not listed.
            Assert.DoesNotContain(plan.Rows, r => r.OrderId == 30);

            Assert.Equal(historyBefore, _db.BubblePointsHistories.Count());
            Assert.Equal(1000, Balance(1));
            Assert.Equal(50, Balance(2));
        }

        [Fact]
        public async Task Apply_PerformsExactlyTheReviewedPlan_AndASecondRunReversesNothing()
        {
            await SeedPast();
            var plan = await RefundPointsReversal.BuildCorrectionPlanAsync(_db);

            var applied = await RefundPointsReversal.ApplyCorrectionPlanAsync(_db, NullLogger.Instance, plan.PlanId, "the test");

            Assert.NotNull(applied);
            Assert.Equal(1000 - 150 - 25 - 60, Balance(1));
            Assert.Equal(0, Balance(2));
            var cancelled = Assert.Single(Reversals(12));
            Assert.Equal(RefundPointsReversal.CancellationCorrectionType, cancelled.Type);
            Assert.Equal(-60, cancelled.Points);

            var again = await RefundPointsReversal.BuildCorrectionPlanAsync(_db);
            Assert.Empty(again.Rows);
            var noop = await RefundPointsReversal.ApplyCorrectionPlanAsync(_db, NullLogger.Instance, again.PlanId, "the test");
            Assert.Equal(1000 - 235, Balance(1));
            Assert.Empty(noop!.Rows);
        }

        [Fact]
        public async Task Apply_RefusesWhenAnythingChangedSinceTheDryRun()
        {
            await SeedPast();
            var plan = await RefundPointsReversal.BuildCorrectionPlanAsync(_db);

            // The customer earned more points after the dry run was reviewed.
            _db.Users.Single(u => u.Id == 2).BubblePoints = 75;
            await _db.SaveChangesAsync();

            Assert.Null(await RefundPointsReversal.ApplyCorrectionPlanAsync(_db, NullLogger.Instance, plan.PlanId, "the test"));
            Assert.Equal(1000, Balance(1));
            Assert.Empty(Reversals(10));
        }

        [Fact]
        public async Task AnOrderWhoseCustomerHadNothingLeft_IsMarkedDone_WithARowTheCustomerNeverSees()
        {
            AddUser(1, 0);
            AddOrder(10, 1, 200m, status: "Refunded", refunded: 200m, earned: 120);
            await _db.SaveChangesAsync();

            var plan = await RefundPointsReversal.BuildCorrectionPlanAsync(_db);
            await RefundPointsReversal.ApplyCorrectionPlanAsync(_db, NullLogger.Instance, plan.PlanId, "the test");

            var marker = Assert.Single(Reversals(10));
            Assert.Equal(0, marker.Points);
            Assert.Empty((await RefundPointsReversal.BuildCorrectionPlanAsync(_db)).Rows);
            Assert.DoesNotContain((await Points().GetHistory(1, 1, 50)).Items, h => h.Type == "BalanceAdjustment");
            Assert.Contains((await Points().GetHistory(1, 1, 50, adminView: true)).Items, h => h.Type == RefundPointsReversal.RefundCorrectionType);
        }
    }
}
