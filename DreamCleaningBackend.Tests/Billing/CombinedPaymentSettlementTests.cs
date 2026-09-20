using System.Data.Common;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using DreamCleaningBackend.Services.Billing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DreamCleaningBackend.Tests.Billing;

/// <summary>
/// "Pay all upcoming" — ONE Stripe charge covering several recurring cleanings (2026-09 fix).
///
/// The bug: settlement stamped the batch's PaymentIntentId onto every covered order, and
/// Orders.PaymentIntentId is UNIQUE. The second order's write failed, the transaction rolled back,
/// and the webhook swallowed the error — the customer was charged and every cleaning stayed
/// unpaid. These run on a REAL MariaDB so the unique indexes, row locks and savepoints are the
/// real ones; a command interceptor injects the database failures.
/// </summary>
public class CombinedPaymentSettlementTests : IClassFixture<MariaDbDatabase>
{
    private readonly MariaDbDatabase _db;
    public CombinedPaymentSettlementTests(MariaDbDatabase db) => _db = db;

    // ── Harness ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Fails chosen UPDATEs of the Orders table, the way a lost connection or a
    /// deadlock would.</summary>
    private sealed class FailingOrderWrites : DbCommandInterceptor
    {
        private int _seen;
        /// <summary>1-based index of the Orders UPDATE to fail; 0 = never.</summary>
        public int FailOnOrdersUpdate { get; set; }
        public bool FailEveryOrdersUpdate { get; set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("UPDATE `Orders`", StringComparison.Ordinal))
            {
                var n = Interlocked.Increment(ref _seen);
                if (FailEveryOrdersUpdate || n == FailOnOrdersUpdate)
                    throw new InvalidOperationException("Simulated database failure while writing an order.");
            }
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private ApplicationDbContext Context(FailingOrderWrites? failures = null)
    {
        var builder = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseMySql(_db.ConnectionString, new MariaDbServerVersion(new Version(10, 9, 8)));
        if (failures != null) builder.AddInterceptors(failures);
        return new ApplicationDbContext(builder.Options);
    }

    private static RecurringCustomerPaymentService Service(ApplicationDbContext db, BillingHarness h) =>
        new(db, h.Stripe, h.Audit, NullLogger<RecurringCustomerPaymentService>.Instance,
            new OrderPartialPaymentService(db, h.Audit, NullLogger<OrderPartialPaymentService>.Instance));

    private sealed record Plan(int UserId, int SeriesId, List<int> OrderIds);

    /// <summary>A customer with a recurring series and one upcoming unpaid cleaning per amount.</summary>
    private async Task<Plan> PlanAsync(BillingHarness h, params (decimal Total, decimal Paid)[] cleanings)
    {
        var user = await h.AddUserAsync();
        var template = await h.AddOrderAsync(user.Id, 150m, serviceDate: DateTime.UtcNow.Date.AddDays(-30), bookedByAdmin: false);
        int seriesId;
        await using (var db = h.NewContext())
        {
            var t = await db.Orders.FirstAsync(o => o.Id == template.Id);
            t.IsPaid = true;
            var series = new RecurringOrderSeries
            {
                UserId = user.Id, TemplateOrderId = template.Id, IntervalValue = 1, IntervalUnit = RecurrenceIntervalUnit.Weeks,
                AnchorDate = DateTime.UtcNow.Date, ServiceTime = TimeSpan.FromHours(10), IsActive = true,
                AutoRequestPayment = true, CreatedByUserId = user.Id
            };
            db.RecurringOrderSeries.Add(series);
            await db.SaveChangesAsync();
            seriesId = series.Id;
            t.RecurringSeriesId = seriesId;
            await db.SaveChangesAsync();
        }

        var ids = new List<int>();
        var day = 3;
        foreach (var (total, paid) in cleanings)
        {
            var order = await h.AddOrderAsync(user.Id, total, amountPaid: paid, seriesId: seriesId,
                serviceDate: DateTime.UtcNow.Date.AddDays(day), bookedByAdmin: false);
            ids.Add(order.Id);
            day += 7;
        }
        // Contact details unique to this plan, so a test counting confirmations counts its own —
        // every test in the class shares one database, and the global follow-up pass sees all of it.
        await using (var db = h.NewContext())
            await db.Orders.Where(o => ids.Contains(o.Id)).ExecuteUpdateAsync(s => s
                .SetProperty(o => o.ContactEmail, $"plan{user.Id}@example.com")
                .SetProperty(o => o.ContactPhone, $"917{user.Id:D7}"));
        return new Plan(user.Id, seriesId, ids);
    }

    private async Task<CombinedPaymentDto> StartAsync(BillingHarness h, Plan plan, FailingOrderWrites? failures = null)
    {
        await using var db = Context(failures);
        return await Service(db, h).StartCombinedPaymentAsync(plan.UserId, new StartCombinedPaymentDto());
    }

    /// <summary>What the Stripe webhook does on payment_intent.succeeded.</summary>
    private async Task<bool> WebhookAsync(BillingHarness h, CombinedPaymentDto batch, FailingOrderWrites? failures = null)
    {
        await using var db = Context(failures);
        return await Service(db, h).SettleBatchAsync(batch.PaymentIntentId!, batch.BatchId);
    }

    /// <summary>What the customer's page does after Stripe.js reports success: reload the list.</summary>
    private async Task<UpcomingRecurringOrdersDto> PageLoadAsync(BillingHarness h, Plan plan, FailingOrderWrites? failures = null)
    {
        await using var db = Context(failures);
        return await Service(db, h).GetUpcomingAsync(plan.UserId);
    }

    private async Task<List<Order>> OrdersAsync(Plan plan)
    {
        await using var db = Context();
        return await db.Orders.AsNoTracking().Where(o => plan.OrderIds.Contains(o.Id)).OrderBy(o => o.Id).ToListAsync();
    }

    private async Task<OrderPaymentBatch> BatchAsync(int batchId)
    {
        await using var db = Context();
        return await db.OrderPaymentBatches.AsNoTracking().Include(b => b.Items).FirstAsync(b => b.Id == batchId);
    }

    private static int SettledAudits(BillingHarness h, int batchId) =>
        h.Audit.Actions.Count(a => a.Action == "CombinedPaymentSettled" && a.EntityId == batchId);

    // ═══ 1–3. Two, three-plus, different amounts ════════════════════════════════════════════

    [MariaDbFact]
    public async Task TwoUpcomingOrders_OneCharge_BothPaid_AndTheOrderUniqueIndexStillHolds()
    {
        var h = new BillingHarness(_db);
        var plan = await PlanAsync(h, (150m, 0m), (150m, 0m));

        var batch = await StartAsync(h, plan);
        Assert.Equal(300m, batch.Amount);
        Assert.True(h.Stripe.CustomerConfirms(batch.PaymentIntentId!));

        Assert.True(await WebhookAsync(h, batch));

        var orders = await OrdersAsync(plan);
        Assert.All(orders, o =>
        {
            Assert.True(o.IsPaid);
            Assert.Equal(OrderStatuses.Active, o.Status);
            Assert.Equal(150m, o.InitialTotal);
            // The shared charge is NOT stamped on the orders — that write is what violated
            // IX_Orders_PaymentIntentId. The batch items are the link.
            Assert.NotEqual(batch.PaymentIntentId, o.PaymentIntentId);
        });
        var settled = await BatchAsync(batch.BatchId);
        Assert.Equal(OrderPaymentBatchStatus.Paid, settled.Status);
        Assert.All(settled.Items, i => Assert.True(i.AppliedToOrder));
        Assert.Null(settled.SettlementWarning);
        Assert.Single(h.Stripe.SucceededCharges);

        // The constraint was kept, not removed: two orders still cannot share an intent.
        await using var db = Context();
        await db.Database.ExecuteSqlRawAsync("UPDATE Orders SET PaymentIntentId = 'pi_dup_probe' WHERE Id = {0}", plan.OrderIds[0]);
        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            db.Database.ExecuteSqlRawAsync("UPDATE Orders SET PaymentIntentId = 'pi_dup_probe' WHERE Id = {0}", plan.OrderIds[1]));
        Assert.Contains("Duplicate entry", ex.Message);
        await db.Database.ExecuteSqlRawAsync("UPDATE Orders SET PaymentIntentId = NULL WHERE Id = {0}", plan.OrderIds[0]);
    }

    [MariaDbFact]
    public async Task FourOrdersOfDifferentAmounts_ChargeTheirSum_AndEachGetsItsOwnShare()
    {
        var h = new BillingHarness(_db);
        var plan = await PlanAsync(h, (150m, 0m), (99.99m, 0m), (212.34m, 0m), (80.01m, 0m));

        var batch = await StartAsync(h, plan);
        Assert.Equal(542.34m, batch.Amount);
        Assert.Equal(54234, h.Stripe.Intents[batch.PaymentIntentId!].Amount);
        Assert.Equal(plan.OrderIds, batch.OrderIds);

        h.Stripe.CustomerConfirms(batch.PaymentIntentId!);
        Assert.True(await WebhookAsync(h, batch));

        var orders = await OrdersAsync(plan);
        Assert.All(orders, o => Assert.True(o.IsPaid));
        var items = (await BatchAsync(batch.BatchId)).Items.OrderBy(i => i.OrderId).ToList();
        Assert.Equal(new[] { 150m, 99.99m, 212.34m, 80.01m }, items.Select(i => i.Amount));
        Assert.Equal(batch.Amount, items.Sum(i => i.Amount));
    }

    // ═══ 4. Partially paid orders ════════════════════════════════════════════════════════════

    [MariaDbFact]
    public async Task APartiallyPaidOrder_IsChargedOnlyItsBalance_AndEndsPaid()
    {
        var h = new BillingHarness(_db);
        var plan = await PlanAsync(h, (386.16m, 100m), (150m, 0m));

        var before = await PageLoadAsync(h, plan);
        Assert.Equal(436.16m, before.PayAllTotal);                       // 286.16 + 150, never 536.16
        Assert.Equal(286.16m, before.Orders.First(o => o.OrderId == plan.OrderIds[0]).AmountDue);

        var batch = await StartAsync(h, plan);
        Assert.Equal(436.16m, batch.Amount);
        h.Stripe.CustomerConfirms(batch.PaymentIntentId!);
        await WebhookAsync(h, batch);

        var orders = await OrdersAsync(plan);
        Assert.All(orders, o => Assert.True(o.IsPaid));
        Assert.Equal(100m, orders[0].AmountPaid);                       // the deposit record is untouched
        Assert.Equal(286.16m, (await BatchAsync(batch.BatchId)).Items.First(i => i.OrderId == plan.OrderIds[0]).Amount);
    }

    [MariaDbFact]
    public async Task PricesRaisedAfterAuthorising_AreCreditedAsSlices_WithTheRestStillOwed()
    {
        var h = new BillingHarness(_db);
        var plan = await PlanAsync(h, (150m, 0m), (150m, 0m), (150m, 0m));
        var batch = await StartAsync(h, plan);

        // An admin raises TWO of the three prices between authorisation and capture. Two slices
        // for one intent is exactly what a UNIQUE PaymentIntentId on the slice would refuse.
        await using (var db = Context())
            await db.Orders.Where(o => o.Id == plan.OrderIds[0] || o.Id == plan.OrderIds[1])
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.Total, 200m));

        h.Stripe.CustomerConfirms(batch.PaymentIntentId!);
        Assert.True(await WebhookAsync(h, batch));

        var orders = await OrdersAsync(plan);
        Assert.False(orders[0].IsPaid);
        Assert.False(orders[1].IsPaid);
        Assert.Equal(150m, orders[0].AmountPaid);                       // 50 still owed on each
        Assert.Equal(150m, orders[1].AmountPaid);
        Assert.True(orders[2].IsPaid);

        await using var check = Context();
        var slices = await check.OrderPartialPayments.AsNoTracking()
            .Where(p => plan.OrderIds.Contains(p.OrderId)).ToListAsync();
        Assert.Equal(2, slices.Count);
        Assert.All(slices, p =>
        {
            Assert.Equal(OrderPartialPaymentStatus.Paid, p.Status);
            Assert.Equal(150m, p.PaidAmount);
            Assert.Null(p.PaymentIntentId);
            Assert.Equal(batch.PaymentIntentId, p.PaymentReference);
        });
        Assert.Contains("still due", (await BatchAsync(batch.BatchId)).SettlementWarning);
    }

    // ═══ 5. Orders already covered by another payment ════════════════════════════════════════

    [MariaDbFact]
    public async Task AnOrderAlreadyPaid_IsLeftOutOfTheCharge()
    {
        var h = new BillingHarness(_db);
        var plan = await PlanAsync(h, (150m, 0m), (120m, 0m), (130m, 0m));
        await using (var db = Context())
            await db.Orders.Where(o => o.Id == plan.OrderIds[0]).ExecuteUpdateAsync(s => s.SetProperty(o => o.IsPaid, true));

        var batch = await StartAsync(h, plan);
        Assert.Equal(250m, batch.Amount);
        Assert.DoesNotContain(plan.OrderIds[0], batch.OrderIds);
    }

    [MariaDbFact]
    public async Task AnOrderPaidByAnotherRouteMidFlight_IsNotCreditedTwice_AndIsFlagged()
    {
        var h = new BillingHarness(_db);
        var plan = await PlanAsync(h, (150m, 0m), (120m, 0m));
        var batch = await StartAsync(h, plan);

        await using (var db = Context())
            await db.Orders.Where(o => o.Id == plan.OrderIds[1])
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.IsPaid, true).SetProperty(o => o.PaymentIntentId, "pi_other_route"));

        h.Stripe.CustomerConfirms(batch.PaymentIntentId!);
        Assert.True(await WebhookAsync(h, batch));

        var orders = await OrdersAsync(plan);
        Assert.True(orders[0].IsPaid);
        Assert.Equal("pi_other_route", orders[1].PaymentIntentId);      // the other payment's record survives
        var settled = await BatchAsync(batch.BatchId);
        Assert.False(settled.Items.Single(i => i.OrderId == plan.OrderIds[1]).AppliedToOrder);
        Assert.Contains("needs refunding", settled.SettlementWarning);
    }

    // ═══ 6, 7, 12. Duplicate confirmations, duplicate webhooks, refreshing the page ═════════

    [MariaDbFact]
    public async Task PageRefreshesAndWebhookRetries_SettleExactlyOnce_AndNeverCreateAnotherCharge()
    {
        var h = new BillingHarness(_db);
        var plan = await PlanAsync(h, (150m, 0m), (150m, 0m), (150m, 0m));
        var batch = await StartAsync(h, plan);
        h.Stripe.CustomerConfirms(batch.PaymentIntentId!);

        var first = await PageLoadAsync(h, plan);                        // the success page reloads
        Assert.All(first.Orders, o => Assert.True(o.IsPaid));
        await PageLoadAsync(h, plan);                                    // refreshed again
        await PageLoadAsync(h, plan);
        Assert.False(await WebhookAsync(h, batch));                      // the webhook arrives late
        Assert.False(await WebhookAsync(h, batch));                      // and is redelivered

        Assert.Equal(1, SettledAudits(h, batch.BatchId));
        Assert.Single(h.Stripe.CreatedIntents);
        Assert.Single(h.Stripe.SucceededCharges);
        Assert.Empty(h.Stripe.Refunds);
        Assert.All(await OrdersAsync(plan), o => Assert.True(o.IsPaid));
    }

    [MariaDbFact]
    public async Task ConcurrentWebhookDeliveries_SettleExactlyOnce()
    {
        var h = new BillingHarness(_db);
        var plan = await PlanAsync(h, (150m, 0m), (120m, 50m), (99.99m, 0m));
        var batch = await StartAsync(h, plan);
        h.Stripe.CustomerConfirms(batch.PaymentIntentId!);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => WebhookAsync(h, batch))));

        Assert.Equal(1, results.Count(r => r));
        Assert.Equal(1, SettledAudits(h, batch.BatchId));
        var orders = await OrdersAsync(plan);
        Assert.All(orders, o => Assert.True(o.IsPaid));
        Assert.Equal(50m, orders[1].AmountPaid);                         // not credited again
        await using var db = Context();
        Assert.Equal(0, await db.OrderPartialPayments.CountAsync(p => plan.OrderIds.Contains(p.OrderId)));
    }

    [MariaDbFact]
    public async Task SuccessBetweenPageLoadAndRetry_SettlesOnce_RefusesANewCharge_AndALateFailureEventChangesNothing()
    {
        // Moved from RecurringPaymentStateTests (InMemory), with the assertion corrected: the
        // orders must NOT carry the batch intent.
        var h = new BillingHarness(_db);
        var plan = await PlanAsync(h, (100m, 0m), (100m, 0m), (100m, 0m));
        var batch = await StartAsync(h, plan);
        h.Stripe.CustomerConfirms(batch.PaymentIntentId!);           // paid while the page still showed "Pay all"

        // The customer presses Pay all again: the refresh inside it records the first charge,
        // and there is nothing left to pay.
        await Assert.ThrowsAsync<CombinedPaymentException>(() => StartAsync(h, plan));
        Assert.False(await WebhookAsync(h, batch));

        await using (var db = Context())
            await Service(db, h).MarkBatchFailedAsync(batch.PaymentIntentId!, "Late failure event");

        Assert.All(await OrdersAsync(plan), o =>
        {
            Assert.True(o.IsPaid);
            Assert.Equal(OrderStatuses.Active, o.Status);
            Assert.Null(o.PaymentIntentId);
        });
        var settled = await BatchAsync(batch.BatchId);
        Assert.Equal(OrderPaymentBatchStatus.Paid, settled.Status);
        Assert.All(settled.Items, i => Assert.True(i.AppliedToOrder));
        Assert.Single(h.Stripe.CreatedIntents);
        Assert.False((await PageLoadAsync(h, plan)).CanPayAll);
        Assert.Equal(1, SettledAudits(h, batch.BatchId));
    }

    // ═══ 8. Concurrent payment attempts ══════════════════════════════════════════════════════

    [MariaDbFact]
    public async Task TwoPayAllAttempts_LeaveOnlyOneChargeable()
    {
        var h = new BillingHarness(_db);
        var plan = await PlanAsync(h, (150m, 0m), (150m, 0m));

        var attempts = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            try { return await StartAsync(h, plan); } catch (CombinedPaymentException) { return null; }
        })));

        // Whatever the interleaving, the customer's browser can complete at most one of them:
        // a superseded intent is cancelled at Stripe before the replacement is issued.
        var confirmed = attempts.Where(a => a != null).Count(a => h.Stripe.CustomerConfirms(a!.PaymentIntentId!));
        Assert.Equal(1, confirmed);

        var winner = attempts.First(a => a != null && h.Stripe.Intents[a.PaymentIntentId!].Status == "succeeded")!;
        Assert.True(await WebhookAsync(h, winner));
        Assert.All(await OrdersAsync(plan), o => Assert.True(o.IsPaid));
        Assert.Single(h.Stripe.SucceededCharges);
    }

    // ═══ 9–10. Stripe success, then the database fails; then recovery ══════════════════════

    [MariaDbFact]
    public async Task ChargedButRecordingFails_NothingIsHalfWritten_TheCleaningsAreHeld_AndNobodyIsAskedToPayAgain()
    {
        var h = new BillingHarness(_db);
        var plan = await PlanAsync(h, (150m, 0m), (150m, 0m), (150m, 0m));
        var batch = await StartAsync(h, plan);
        h.Stripe.CustomerConfirms(batch.PaymentIntentId!);

        // The SECOND order's write fails — the first has already been written inside the transaction.
        var failures = new FailingOrderWrites { FailOnOrdersUpdate = 2 };
        await Assert.ThrowsAnyAsync<Exception>(() => WebhookAsync(h, batch, failures));   // → 500, Stripe redelivers

        // Atomic: not one order is marked paid by a settlement that did not complete.
        Assert.All(await OrdersAsync(plan), o => Assert.False(o.IsPaid));
        var parked = await BatchAsync(batch.BatchId);
        Assert.Equal(OrderPaymentBatchStatus.Processing, parked.Status);
        Assert.Equal(RecurringCustomerPaymentService.SettlementDeferredReason, parked.FailureReason);

        // The page loads (no error) with every cleaning HELD, never offered for payment again.
        var page = await PageLoadAsync(h, plan, new FailingOrderWrites { FailEveryOrdersUpdate = true });
        Assert.False(page.CanPayAll);
        Assert.All(page.Orders, o => Assert.False(o.IsPayable));

        // Every route to a second charge is refused.
        await Assert.ThrowsAsync<CombinedPaymentException>(() => StartAsync(h, plan, new FailingOrderWrites { FailEveryOrdersUpdate = true }));
        await using (var db = Context(new FailingOrderWrites { FailEveryOrdersUpdate = true }))
        {
            var service = Service(db, h);
            await Assert.ThrowsAsync<CombinedPaymentException>(() => service.PrepareIndividualPaymentAsync(plan.OrderIds[0]));
        }
        Assert.Single(h.Stripe.CreatedIntents);
        Assert.Empty(h.Stripe.Refunds);                                  // a recoverable failure is never refunded
    }

    [MariaDbFact]
    public async Task AfterATemporaryFailure_TheReconcilerCompletesEveryAllocation_Once()
    {
        var h = new BillingHarness(_db);
        var plan = await PlanAsync(h, (150m, 0m), (212.34m, 12.34m), (99.99m, 0m));
        var batch = await StartAsync(h, plan);
        h.Stripe.CustomerConfirms(batch.PaymentIntentId!);
        await Assert.ThrowsAnyAsync<Exception>(() => WebhookAsync(h, batch, new FailingOrderWrites { FailOnOrdersUpdate = 3 }));
        Assert.All(await OrdersAsync(plan), o => Assert.False(o.IsPaid));

        // The database is back. The background pass (AutoPayWorker, every 5 min) finishes it.
        await using (var db = Context())
            Assert.Equal(1, await Service(db, h).ReconcileUnsettledBatchesAsync());

        var orders = await OrdersAsync(plan);
        Assert.All(orders, o => Assert.True(o.IsPaid));
        Assert.Equal(12.34m, orders[1].AmountPaid);
        var settled = await BatchAsync(batch.BatchId);
        Assert.Equal(OrderPaymentBatchStatus.Paid, settled.Status);
        Assert.Null(settled.FailureReason);

        // A second pass, the late webhook and a page load change nothing.
        await using (var db = Context())
            Assert.Equal(0, await Service(db, h).ReconcileUnsettledBatchesAsync());
        Assert.False(await WebhookAsync(h, batch));
        await PageLoadAsync(h, plan);
        Assert.Equal(1, SettledAudits(h, batch.BatchId));
        Assert.Single(h.Stripe.SucceededCharges);
        Assert.Empty(h.Stripe.Refunds);
    }

    [MariaDbFact]
    public async Task AFailureInsideThePageLoad_IsRolledBackToItsSavepoint_AndTheNextLoadCompletesIt()
    {
        var h = new BillingHarness(_db);
        var plan = await PlanAsync(h, (150m, 0m), (150m, 0m));
        var batch = await StartAsync(h, plan);
        h.Stripe.CustomerConfirms(batch.PaymentIntentId!);

        // Settlement runs NESTED here (inside the refresh's customer lock), so only a savepoint
        // stands between a half-written batch and the outer commit.
        var page = await PageLoadAsync(h, plan, new FailingOrderWrites { FailOnOrdersUpdate = 2 });
        Assert.All(page.Orders, o => Assert.False(o.IsPayable));
        Assert.All(await OrdersAsync(plan), o => Assert.False(o.IsPaid));
        Assert.Equal(OrderPaymentBatchStatus.Processing, (await BatchAsync(batch.BatchId)).Status);

        var next = await PageLoadAsync(h, plan);
        Assert.All(next.Orders, o => Assert.True(o.IsPaid));
        Assert.Equal(1, SettledAudits(h, batch.BatchId));
    }

    [MariaDbFact]
    public async Task PayingOneCleaningAfterAnUnrecordedPayAll_FindsItAlreadyPaid()
    {
        var h = new BillingHarness(_db);
        var plan = await PlanAsync(h, (150m, 0m), (150m, 0m));
        var batch = await StartAsync(h, plan);
        h.Stripe.CustomerConfirms(batch.PaymentIntentId!);             // webhook not delivered yet

        await using var db = Context();
        var order = await db.Orders.FirstAsync(o => o.Id == plan.OrderIds[0]);   // the caller's tracked copy
        var ex = await Assert.ThrowsAsync<CombinedPaymentException>(() => Service(db, h).PrepareIndividualPaymentAsync(order.Id));
        Assert.Contains("already paid", ex.Message);
        Assert.True(order.IsPaid);                                       // the caller's instance was refreshed
        Assert.Single(h.Stripe.CreatedIntents);
    }

    // ═══ Money going back, and what the customer is shown ═══════════════════════════════════

    [MariaDbFact]
    public async Task RefundingOneBatchPaidOrder_ReachesOnlyItsOwnShare()
    {
        var h = new BillingHarness(_db);
        var plan = await PlanAsync(h, (150m, 0m), (99.99m, 0m));
        var batch = await StartAsync(h, plan);
        h.Stripe.CustomerConfirms(batch.PaymentIntentId!);
        await WebhookAsync(h, batch);

        await using var db = Context();
        var refunds = new OrderRefundService(db, h.Stripe, h.Messaging.Email, h.Audit, NullLogger<OrderRefundService>.Instance);
        var summary = await refunds.GetRefundSummaryAsync(plan.OrderIds[1]);
        Assert.Equal(99.99m, summary.TotalCharged);
        Assert.Equal(99.99m, summary.RemainingRefundable);             // not the batch's 249.99

        var result = await refunds.IssueRefundAsync(plan.OrderIds[1], null, "test", adminUserId: plan.UserId, sendEmail: false);
        Assert.True(result.Success, result.Message);
        Assert.Equal(99.99m, result.AmountRefunded);
        Assert.Equal(9999, h.Stripe.RefundedCents[batch.PaymentIntentId!]);

        var other = await refunds.GetRefundSummaryAsync(plan.OrderIds[0]);
        Assert.Equal(150m, other.RemainingRefundable);                 // the other share is untouched
    }

    [MariaDbFact]
    public async Task BillingHistory_ShowsOneCombinedPayment_ForTheWholeCharge()
    {
        var h = new BillingHarness(_db);
        var plan = await PlanAsync(h, (150m, 0m), (99.99m, 0m), (80m, 0m));
        var batch = await StartAsync(h, plan);
        h.Stripe.CustomerConfirms(batch.PaymentIntentId!);
        await WebhookAsync(h, batch);

        await using var scope = h.Scope();
        var history = await scope.ServiceProvider.GetRequiredService<IBillingHistoryService>().GetHistoryAsync(plan.UserId, 1, 50);
        var combined = Assert.Single(history.Items, i => i.Key == $"order-payment:{batch.PaymentIntentId}");
        Assert.Equal(329.99m, combined.Amount);
        Assert.StartsWith("Combined payment", combined.Description);
    }

    // ═══ Post-payment follow-up: loyalty, subscription, confirmation — exactly once ═════════

    private static CombinedPaymentFollowUpService FollowUp(ApplicationDbContext db, BillingHarness h,
        ILoyaltyDiscountService? loyalty = null) =>
        new(db,
            loyalty ?? new LoyaltyDiscountService(db, h.Audit, NullLogger<LoyaltyDiscountService>.Instance),
            new SubscriptionService(db),
            h.Messaging.Email, h.Messaging.SmsService,
            NullLogger<CombinedPaymentFollowUpService>.Instance);

    private async Task<int> RunFollowUpAsync(BillingHarness h, int? batchId = null, ILoyaltyDiscountService? loyalty = null)
    {
        await using var db = Context();
        return await FollowUp(db, h, loyalty).ProcessAsync(batchId);
    }

    private async Task<CombinedPaymentDto> PaidBatchAsync(BillingHarness h, Plan plan)
    {
        var batch = await StartAsync(h, plan);
        h.Stripe.CustomerConfirms(batch.PaymentIntentId!);
        Assert.True(await WebhookAsync(h, batch));
        return batch;
    }

    private static int CustomerConfirmations(BillingHarness h, Plan plan) => h.Messaging.Emails.Count(e => e.To == $"plan{plan.UserId}@example.com");
    private static int ConfirmationSms(BillingHarness h, Plan plan) => h.Messaging.Sms.Count(s => s.To == $"917{plan.UserId:D7}");

    [MariaDbFact]
    public async Task FollowUp_ConfirmsEachCleaningOnce_HoweverOftenAndConcurrentlyItRuns()
    {
        var h = new BillingHarness(_db);
        var plan = await PlanAsync(h, (150m, 0m), (120m, 0m), (99.99m, 0m));
        var batch = await PaidBatchAsync(h, plan);

        // Webhook retries, page loads and the worker all trigger it — here all at once, twice over.
        var runs = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => RunFollowUpAsync(h, batch.BatchId))));
        await RunFollowUpAsync(h);
        await RunFollowUpAsync(h, batch.BatchId);
        Assert.False(await WebhookAsync(h, batch));                     // a redelivered webhook settles nothing
        await RunFollowUpAsync(h);

        Assert.Equal(3, runs.Sum());
        Assert.Equal(3, CustomerConfirmations(h, plan));                      // one per cleaning, never more
        Assert.Equal(3, ConfirmationSms(h, plan));
        var items = (await BatchAsync(batch.BatchId)).Items;
        Assert.All(items, i => { Assert.NotNull(i.BookkeepingAppliedAt); Assert.NotNull(i.ConfirmationSentAt); });
    }

    [MariaDbFact]
    public async Task FollowUp_RenewsThePlanFromTheLatestVisit_AndClearsTheFirstOrderFlag_Once()
    {
        var h = new BillingHarness(_db);
        var plan = await PlanAsync(h, (150m, 0m), (150m, 0m), (150m, 0m));
        await using (var db = Context())
        {
            await db.Orders.Where(o => plan.OrderIds.Contains(o.Id)).ExecuteUpdateAsync(s => s.SetProperty(o => o.SubscriptionId, 2)); // Weekly, 7 days
            await db.Users.Where(u => u.Id == plan.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.FirstTimeOrder, true));
        }
        await PaidBatchAsync(h, plan);
        await RunFollowUpAsync(h);

        var lastVisit = (await OrdersAsync(plan)).Max(o => o.ServiceDate);
        await using (var db = Context())
        {
            var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == plan.UserId);
            Assert.Equal(2, user.SubscriptionId);
            Assert.Equal(lastVisit.Date, user.SubscriptionStartDate!.Value.Date);   // same end state as paying one by one
            Assert.False(user.FirstTimeOrder);

            // Proof it never runs again: move the plan somewhere else, re-run everything.
            await db.Users.Where(u => u.Id == plan.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.SubscriptionStartDate, new DateTime(2020, 1, 1)));
        }
        await RunFollowUpAsync(h);
        await using var check = Context();
        Assert.Equal(new DateTime(2020, 1, 1), (await check.Users.AsNoTracking().FirstAsync(u => u.Id == plan.UserId)).SubscriptionStartDate);
    }

    [MariaDbFact]
    public async Task FollowUp_ConsumesAOneTimeLoyaltyAwardOnce_AndNeverOnAGeneratedOccurrence()
    {
        var h = new BillingHarness(_db);
        var plan = await PlanAsync(h, (150m, 0m), (150m, 0m));
        await using (var db = Context())
        {
            // First: the series TEMPLATE, still unpaid, carrying a real one-time 10% award.
            // Second: a generated occurrence carrying the standing series discount.
            await db.Orders.Where(o => plan.OrderIds.Contains(o.Id)).ExecuteUpdateAsync(s => s
                .SetProperty(o => o.LoyaltyDiscountPercentage, 10m).SetProperty(o => o.LoyaltyDiscountAmount, 13.78m));
            await db.Orders.Where(o => o.Id == plan.OrderIds[1]).ExecuteUpdateAsync(s => s.SetProperty(o => o.IsGeneratedByRecurringSeries, true));
            await db.Users.Where(u => u.Id == plan.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.LoyaltyDiscountPercentage, 10m));
        }
        await PaidBatchAsync(h, plan);
        await RunFollowUpAsync(h);

        await using (var db = Context())
        {
            var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == plan.UserId);
            Assert.Equal(0m, user.LoyaltyDiscountPercentage);            // the award on the template was used
            Assert.NotNull(user.LoyaltyDiscountLastUsedAt);
            // Give the customer a NEW award; a second application would wrongly consume it.
            await db.Users.Where(u => u.Id == plan.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.LoyaltyDiscountPercentage, 15m));
        }
        await RunFollowUpAsync(h);
        await using var check = Context();
        Assert.Equal(15m, (await check.Users.AsNoTracking().FirstAsync(u => u.Id == plan.UserId)).LoyaltyDiscountPercentage);
    }

    [MariaDbFact]
    public async Task FollowUp_EmailAndSmsFailures_NeverTouchThePayment_AndAreNotRetriedIntoDuplicates()
    {
        var h = new BillingHarness(_db);
        var plan = await PlanAsync(h, (150m, 0m), (150m, 0m));
        var batch = await PaidBatchAsync(h, plan);
        h.Messaging.FailEmail = true;
        h.Messaging.FailSms = true;

        await RunFollowUpAsync(h);

        Assert.All(await OrdersAsync(plan), o => Assert.True(o.IsPaid));
        Assert.Equal(OrderPaymentBatchStatus.Paid, (await BatchAsync(batch.BatchId)).Status);
        h.Messaging.FailEmail = false;
        h.Messaging.FailSms = false;
        await RunFollowUpAsync(h);
        Assert.Equal(0, CustomerConfirmations(h, plan));                      // at most once — never a late duplicate
        Assert.All((await BatchAsync(batch.BatchId)).Items, i => Assert.NotNull(i.ConfirmationSentAt));
    }

    [MariaDbFact]
    public async Task FollowUp_ABookkeepingFailure_StillConfirms_AndTheBookkeepingIsRetriedLater()
    {
        var h = new BillingHarness(_db);
        var plan = await PlanAsync(h, (150m, 0m));
        await using (var db = Context())
            await db.Orders.Where(o => o.Id == plan.OrderIds[0]).ExecuteUpdateAsync(s => s
                .SetProperty(o => o.LoyaltyDiscountPercentage, 10m).SetProperty(o => o.LoyaltyDiscountAmount, 13.78m));
        var batch = await PaidBatchAsync(h, plan);

        var broken = RecurringDiscountRegressionTests.Stub<ILoyaltyDiscountService>((m, a) =>
            Task.FromException(new InvalidOperationException("loyalty store unavailable")));
        await RunFollowUpAsync(h, loyalty: broken);

        Assert.Equal(1, CustomerConfirmations(h, plan));
        var item = (await BatchAsync(batch.BatchId)).Items.Single();
        Assert.Null(item.BookkeepingAppliedAt);                         // rolled back, nothing half-applied
        Assert.True((await OrdersAsync(plan))[0].IsPaid);

        // The lease expires; the next pass completes the bookkeeping and sends nothing again.
        await using (var db = Context())
            await db.OrderPaymentBatchItems.Where(i => i.Id == item.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.FollowUpClaimedAt, DateTime.UtcNow.AddHours(-1)));
        await RunFollowUpAsync(h);
        Assert.NotNull((await BatchAsync(batch.BatchId)).Items.Single().BookkeepingAppliedAt);
        Assert.Equal(1, CustomerConfirmations(h, plan));
    }

    [MariaDbFact]
    public async Task FollowUp_APartlyCoveredOrder_IsNotConfirmed_UntilItIsActuallyPaid()
    {
        var h = new BillingHarness(_db);
        var plan = await PlanAsync(h, (150m, 0m), (150m, 0m));
        var batch = await StartAsync(h, plan);
        await using (var db = Context())
            await db.Orders.Where(o => o.Id == plan.OrderIds[0]).ExecuteUpdateAsync(s => s.SetProperty(o => o.Total, 200m));
        h.Stripe.CustomerConfirms(batch.PaymentIntentId!);
        await WebhookAsync(h, batch);

        await RunFollowUpAsync(h);
        Assert.Equal(1, CustomerConfirmations(h, plan));                      // only the order that is actually paid
    }

    [MariaDbFact]
    public async Task Migration_MarksOnlyAlreadySettledBatchesAsFollowedUp()
    {
        var h = new BillingHarness(_db);
        var plan = await PlanAsync(h, (150m, 0m), (150m, 0m));
        var settled = await PaidBatchAsync(h, plan);
        var otherPlan = await PlanAsync(h, (120m, 0m), (120m, 0m));
        var open = await StartAsync(h, otherPlan);                      // e.g. charged but never recorded

        await using var db = Context();
        var migrations = db.GetInfrastructure().GetRequiredService<Microsoft.EntityFrameworkCore.Migrations.IMigrationsAssembly>();
        var migration = migrations.CreateMigration(migrations.Migrations["20260919141738_AddCombinedPaymentFollowUp"],
            "Pomelo.EntityFrameworkCore.MySql");
        var sql = Assert.Single(migration.UpOperations.OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation>());
        await db.Database.ExecuteSqlRawAsync(sql.Sql);
        await db.Database.ExecuteSqlRawAsync(sql.Sql);                  // idempotent

        Assert.All((await BatchAsync(settled.BatchId)).Items, i => Assert.NotNull(i.ConfirmationSentAt));
        Assert.All((await BatchAsync(open.BatchId)).Items, i => { Assert.Null(i.ConfirmationSentAt); Assert.Null(i.BookkeepingAppliedAt); });
        await RunFollowUpAsync(h, settled.BatchId);
        Assert.Equal(0, CustomerConfirmations(h, plan));                      // nothing historical is re-mailed
    }
}
