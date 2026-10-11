using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Models.Billing;
using DreamCleaningBackend.Models.Commercial;
using DreamCleaningBackend.Services;
using DreamCleaningBackend.Services.Interfaces;
using DreamCleaningBackend.Tests.Billing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static DreamCleaningBackend.Tests.RecurringRefinementTests;

namespace DreamCleaningBackend.Tests;

/// <summary>
/// Cleaning up a recurring plan that was set up wrong (2026-10): the shared "may this order be
/// deleted / skipped?" check, Skip on any Pending unpaid cleaning, Stop recurrence with optional
/// deletion of the unfinished cleanings, the series source on delete, and starting a NEW plan once
/// the old one is stopped.
///
/// The bug these exist for: every admin edit writes an <see cref="OrderUpdateHistory"/> row (even a
/// $0 date move), and the old guard treated that row — like an unpaid payment link or a sent
/// assignment email — as "payment, invoice or staffing activity", so an unpaid, unstaffed
/// occurrence became undeletable the moment anybody touched it.
/// </summary>
public class RecurringCleanupTests
{
    private static ApplicationDbContext Db() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString(), b => b.EnableNullChecks(false))
        .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);

    private static Order NewOrder(DateTime date) => new()
    {
        UserId = 1, ServiceTypeId = 1, ServiceDate = date, ServiceTime = TimeSpan.FromHours(9),
        Status = OrderStatuses.Pending, PaymentMethod = PaymentMethod.Normal, Total = 925.43m, SubTotal = 849.99m,
        Tax = 75.44m, ServiceAddress = "1579 Flatbush Ave.", ContactEmail = "test@example.invalid",
        ContactFirstName = "Test", ContactLastName = "Client", ContactPhone = "2125550100"
    };

    private sealed record Rig(RecurringOrderSeriesService Series, OrderRemovalService Removal, RecordingAuditService Audit);

    private static async Task<Rig> RigAsync(ApplicationDbContext db)
    {
        db.ServiceTypes.Add(new ServiceType { Id = 1, Name = "Residential", BasePrice = 100, TimeDuration = 120 });
        db.Users.Add(new User { Id = 1, FirstName = "Test", LastName = "Client" });
        db.Users.Add(new User { Id = 5, FirstName = "Test", LastName = "Admin" });
        db.Cleaners.Add(new Cleaner { Id = 7, FirstName = "Nino", LastName = "Beridze" });
        var source = NewOrder(NyTimeHelper.NowNy.Date); source.Id = 1;
        db.Orders.Add(source);
        await db.SaveChangesAsync();
        var booking = Stub<IBookingCreationService>((method, args) =>
        {
            var dto = (CreateBookingDto)args![0]!;
            var options = (BookingCreationOptions)args[3]!;
            var o = NewOrder(dto.ServiceDate);
            o.RecurringSeriesId = options.RecurringSeriesId;
            o.RecurrenceOccurrenceDate = options.RecurrenceOccurrenceDate;
            o.IsGeneratedByRecurringSeries = true;
            db.Orders.Add(o);
            db.SaveChanges();
            return Task.FromResult(o);
        });
        var audit = new RecordingAuditService();
        var payments = new RecurringCustomerPaymentService(db, Stub<IStripeService>(), audit, NullLogger<RecurringCustomerPaymentService>.Instance);
        var removal = new OrderRemovalService(db, payments, Stub<IStripeService>(), audit);
        return new Rig(new RecurringOrderSeriesService(db, booking, audit, NullLogger<RecurringOrderSeriesService>.Instance, payments, removal),
            removal, audit);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<List<Order>> GeneratedAsync(ApplicationDbContext db) =>
        db.Orders.Where(o => o.IsGeneratedByRecurringSeries).OrderBy(o => o.ServiceDate).ToListAsync(Ct);

    // ── The shared check ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Guard_DoesNotBlockOnEditsUnpaidLinksCopiedOrNotifiedCleanersOrLogs()
    {
        using var db = Db(); var rig = await RigAsync(db);
        await rig.Series.CreateAsync(1, new SaveRecurringSeriesDto(), 5);
        var o = (await GeneratedAsync(db)).First();
        // The exact shape that used to be refused: an admin edit with no money involved, a
        // payment link reference, a copied cleaner who was emailed, a notification log.
        db.OrderUpdateHistories.Add(new OrderUpdateHistory { OrderId = o.Id, IsPaid = true, AdditionalAmount = 0m, UpdatedAt = DateTime.UtcNow });
        o.PaymentReference = "link sent";
        db.OrderCleaners.Add(new OrderCleaner { OrderId = o.Id, CleanerId = 7, AutoAssignedFromSeriesId = o.RecurringSeriesId, AssignmentNotificationSentAt = DateTime.UtcNow });
        db.NotificationLogs.Add(new NotificationLog { OrderId = o.Id, NotificationType = "RecurringPaymentRequest", SentAt = DateTime.UtcNow });
        await db.SaveChangesAsync(Ct);

        var check = await rig.Removal.CheckAsync(o);

        Assert.True(check.CanRemove, check.BlockedReason);
        Assert.True(check.InSeries);
        Assert.True(check.HandEdited);
        Assert.Equal(new[] { "Nino Beridze" }, check.NotifiedCleaners);
    }

    [Theory]
    [InlineData("paid", "Paid — refund first")]
    [InlineData("part-paid", "Part-paid ($100.00)")]
    [InlineData("manual", "recorded manually")]
    [InlineData("refund", "Refunded")]
    [InlineData("done", "Done")]
    [InlineData("invoice", "On invoice DCI-2026-00000042")]
    [InlineData("batch", "combined payment #")]
    [InlineData("wages", "Cleaner wages paid")]
    [InlineData("top-up", "extra amount was paid")]
    [InlineData("saved-card", "saved-card charge")]
    public async Task Guard_BlocksOnlyRealActivity_WithAReasonAnAdminCanActOn(string kind, string expected)
    {
        using var db = Db(); var rig = await RigAsync(db);
        await rig.Series.CreateAsync(1, new SaveRecurringSeriesDto(), 5);
        var o = (await GeneratedAsync(db)).First();
        switch (kind)
        {
            case "paid": o.IsPaid = true; break;
            case "part-paid": o.AmountPaid = 100m; break;
            case "manual": o.ManualPaymentRecordedAt = DateTime.UtcNow; o.PaymentMethod = PaymentMethod.Cash; break;
            case "refund": db.OrderRefunds.Add(new OrderRefund { OrderId = o.Id, Amount = 10m }); break;
            case "done": o.Status = OrderStatuses.Done; break;
            case "invoice":
                db.CommercialInvoiceOrders.Add(new CommercialInvoiceOrder { OrderId = o.Id,
                    Invoice = new CommercialInvoice { Status = InvoiceStatus.Draft, InvoiceNumber = "DCI-2026-00000042" } });
                break;
            case "batch":
                db.OrderPaymentBatches.Add(new OrderPaymentBatch { UserId = 1, Status = OrderPaymentBatchStatus.Paid,
                    Items = { new OrderPaymentBatchItem { OrderId = o.Id, Amount = o.Total } } });
                break;
            case "wages": db.OrderCleaners.Add(new OrderCleaner { OrderId = o.Id, CleanerId = 7, IsPaid = true, PaidAt = DateTime.UtcNow }); break;
            case "top-up": db.OrderUpdateHistories.Add(new OrderUpdateHistory { OrderId = o.Id, IsPaid = true, AdditionalAmount = 50m }); break;
            case "saved-card": db.BillingPaymentAttempts.Add(new BillingPaymentAttempt { OrderId = o.Id, ActiveLockKey = BillingPaymentAttempt.OrderObligationKey(o.Id) }); break;
        }
        await db.SaveChangesAsync(Ct);

        var check = await rig.Removal.CheckAsync(o);

        Assert.False(check.CanRemove);
        Assert.Contains(expected, check.BlockedReason);
    }

    [Fact]
    public async Task Guard_AnOrderOutsideAPlanKeepsTheHardDelete_ButNotAnInvoiceLink()
    {
        using var db = Db(); var rig = await RigAsync(db);
        var plain = (await db.Orders.FindAsync(new object?[] { 1 }, Ct))!;
        plain.IsPaid = true; await db.SaveChangesAsync(Ct);
        Assert.True((await rig.Removal.CheckAsync(plain)).CanRemove);

        db.CommercialInvoiceOrders.Add(new CommercialInvoiceOrder { OrderId = 1,
            Invoice = new CommercialInvoice { Status = InvoiceStatus.Sent, InvoiceNumber = "DCI-2026-00000007" } });
        await db.SaveChangesAsync(Ct);
        Assert.Contains("DCI-2026-00000007", (await rig.Removal.CheckAsync(plain)).BlockedReason);
    }

    // ── Skip ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Skip_WorksOnAnEditedOccurrence_TheSource_AndAPastOne_AndNamesNotifiedCleaners()
    {
        using var db = Db(); var rig = await RigAsync(db);
        var series = await rig.Series.CreateAsync(1, new SaveRecurringSeriesDto(), 5);
        var edited = (await GeneratedAsync(db)).First();
        db.OrderUpdateHistories.Add(new OrderUpdateHistory { OrderId = edited.Id, IsPaid = true, AdditionalAmount = 0m });
        db.OrderCleaners.Add(new OrderCleaner { OrderId = edited.Id, CleanerId = 7, AssignmentNotificationSentAt = DateTime.UtcNow });
        var past = NewOrder(NyTimeHelper.NowNy.Date.AddDays(-3));
        past.RecurringSeriesId = series.Id; past.IsGeneratedByRecurringSeries = true;
        db.Orders.Add(past);
        await db.SaveChangesAsync(Ct);

        var result = await rig.Series.SkipAsync(series.Id, edited.Id, 5);
        await rig.Series.SkipAsync(series.Id, 1, 5);
        await rig.Series.SkipAsync(series.Id, past.Id, 5);

        Assert.Equal(OrderStatuses.Cancelled, edited.Status);
        Assert.Equal(OrderStatuses.Cancelled, (await db.Orders.FindAsync(new object?[] { 1 }, Ct))!.Status);
        Assert.Equal(OrderStatuses.Cancelled, past.Status);
        Assert.Contains(result.Warnings, w => w.Contains("Nino Beridze"));
        Assert.Equal(3, rig.Audit.Actions.Count(a => a.Action == "RecurringOccurrenceSkipped"));
    }

    // ── Stop, with optional deletion ─────────────────────────────────────────────────────────

    [Fact]
    public async Task StopPreview_ListsOnlyUnfinishedOrders_AndExplainsTheOnesThatCannotGo()
    {
        using var db = Db(); var rig = await RigAsync(db);
        var series = await rig.Series.CreateAsync(1, new SaveRecurringSeriesDto { UpcomingOccurrenceTarget = 5 }, 5);
        var generated = await GeneratedAsync(db);
        generated[0].Status = OrderStatuses.Done;
        generated[1].Status = OrderStatuses.Cancelled;
        generated[2].IsPaid = true;
        db.OrderCleaners.Add(new OrderCleaner { OrderId = generated[3].Id, CleanerId = 7 });
        await db.SaveChangesAsync(Ct);

        var preview = await rig.Series.GetStopPreviewAsync(series.Id);

        Assert.DoesNotContain(preview.Orders, r => r.OrderId == generated[0].Id || r.OrderId == generated[1].Id);
        var paid = preview.Orders.Single(r => r.OrderId == generated[2].Id);
        Assert.False(paid.CanDelete); Assert.Contains("Paid", paid.BlockedReason);
        var staffed = preview.Orders.Single(r => r.OrderId == generated[3].Id);
        Assert.True(staffed.CanDelete); Assert.Equal(new[] { "Nino Beridze" }, staffed.Cleaners);
        Assert.True(preview.Orders.Single(r => r.OrderId == 1).IsTemplate);
    }

    [Fact]
    public async Task Stop_DeletesOnlyTheChosenOrders_KeepsTheRestAsOrdinaryOrders_AndAuditsEach()
    {
        using var db = Db(); var rig = await RigAsync(db);
        var series = await rig.Series.CreateAsync(1, new SaveRecurringSeriesDto { UpcomingOccurrenceTarget = 4 }, 5);
        var generated = await GeneratedAsync(db);
        var chosen = generated.Take(2).Select(o => o.Id).ToList();
        db.OrderCleaners.Add(new OrderCleaner { OrderId = chosen[0], CleanerId = 7, AssignmentNotificationSentAt = DateTime.UtcNow });
        await db.SaveChangesAsync(Ct);

        var result = await rig.Series.StopAsync(series.Id, chosen, 5, mayDeleteOrders: true);

        Assert.Equal(chosen, result.DeletedOrderIds);
        Assert.False(await db.Orders.AnyAsync(o => chosen.Contains(o.Id), Ct));
        Assert.All(generated.Skip(2), o => Assert.Equal(OrderStatuses.Pending, o.Status));
        Assert.NotNull(result.Series!.StoppedAt);
        Assert.False(result.Series.IsActive);
        Assert.Contains(result.Warnings, w => w.Contains("Nino Beridze"));
        Assert.Single(rig.Audit.Actions, a => a.Action == "RecurringSeriesstop");
        Assert.Equal(2, rig.Audit.Actions.Count(a => a.Action == "RecurringOccurrenceDeleted"));
        Assert.Equal(0, (await rig.Series.GenerateAsync(series.Id)).CreatedCount);
    }

    [Fact]
    public async Task Stop_RefusesABlockedSelection_AndChangesNothing()
    {
        using var db = Db(); var rig = await RigAsync(db);
        var series = await rig.Series.CreateAsync(1, new SaveRecurringSeriesDto(), 5);
        var generated = await GeneratedAsync(db);
        generated[1].IsPaid = true; await db.SaveChangesAsync(Ct);

        var error = await Assert.ThrowsAsync<RecurringSeriesException>(() =>
            rig.Series.StopAsync(series.Id, new[] { generated[0].Id, generated[1].Id }, 5, mayDeleteOrders: true));

        Assert.Contains($"#{generated[1].Id}", error.Message);
        Assert.Contains("Paid — refund first", error.Message);
        Assert.Null((await db.RecurringOrderSeries.FindAsync(new object?[] { series.Id }, Ct))!.StoppedAt);
        Assert.Equal(generated.Count, await db.Orders.CountAsync(o => o.IsGeneratedByRecurringSeries, Ct));
    }

    [Fact]
    public async Task Stop_DeletingIsSuperAdminOnly_ButAnyoneMayStopAndKeepEverything()
    {
        using var db = Db(); var rig = await RigAsync(db);
        var series = await rig.Series.CreateAsync(1, new SaveRecurringSeriesDto(), 5);
        var first = (await GeneratedAsync(db)).First();

        await Assert.ThrowsAsync<RecurringSeriesException>(() =>
            rig.Series.StopAsync(series.Id, new[] { first.Id }, 5, mayDeleteOrders: false));
        var kept = await rig.Series.StopAsync(series.Id, Array.Empty<int>(), 5, mayDeleteOrders: false);

        Assert.NotNull(kept.Series!.StoppedAt);
        Assert.True(await db.Orders.AnyAsync(o => o.Id == first.Id, Ct));
    }

    [Fact]
    public async Task Stop_RefusesFinishedOrdersAndOtherPlansOrders()
    {
        using var db = Db(); var rig = await RigAsync(db);
        var series = await rig.Series.CreateAsync(1, new SaveRecurringSeriesDto(), 5);
        var done = (await GeneratedAsync(db)).First(); done.Status = OrderStatuses.Done;
        var stranger = NewOrder(NyTimeHelper.NowNy.Date.AddDays(2)); db.Orders.Add(stranger);
        await db.SaveChangesAsync(Ct);

        await Assert.ThrowsAsync<RecurringSeriesException>(() => rig.Series.StopAsync(series.Id, new[] { done.Id }, 5, true));
        await Assert.ThrowsAsync<RecurringSeriesException>(() => rig.Series.StopAsync(series.Id, new[] { stranger.Id }, 5, true));
        Assert.Equal(OrderStatuses.Done, done.Status);
    }

    // ── The plan's source on delete ──────────────────────────────────────────────────────────

    [Fact]
    public async Task DeletingTheSourceOfARunningPlan_SaysStopTheRecurrenceFirst()
    {
        using var db = Db(); var rig = await RigAsync(db);
        await rig.Series.CreateAsync(1, new SaveRecurringSeriesDto(), 5);
        var source = (await db.Orders.FindAsync(new object?[] { 1 }, Ct))!;

        Assert.True((await rig.Removal.CheckAsync(source)).CanRemove);
        var error = await Assert.ThrowsAsync<OrderRemovalException>(() => rig.Removal.RemoveAsync(source, 5));
        Assert.Contains("Stop the recurrence first", error.Message);
        Assert.True(await db.Orders.AnyAsync(o => o.Id == 1, Ct));
    }

    [Fact]
    public async Task Stop_DeletingTheSource_MovesItToTheEarliestOrderThatStays()
    {
        using var db = Db(); var rig = await RigAsync(db);
        var series = await rig.Series.CreateAsync(1, new SaveRecurringSeriesDto { UpcomingOccurrenceTarget = 4 }, 5);
        var generated = await GeneratedAsync(db);

        // The source and the earliest generated one go; the next one becomes the source.
        await rig.Series.StopAsync(series.Id, new[] { 1, generated[0].Id }, 5, true);

        var row = (await db.RecurringOrderSeries.FindAsync(new object?[] { series.Id }, Ct))!;
        Assert.Equal(generated[1].Id, row.TemplateOrderId);
        Assert.Contains(rig.Audit.Actions, a => a.Action == "RecurringSeriesSourceMoved");
    }

    [Fact]
    public async Task Stop_DeletingEverything_RemovesThePlanItself()
    {
        using var db = Db(); var rig = await RigAsync(db);
        var series = await rig.Series.CreateAsync(1, new SaveRecurringSeriesDto { UpcomingOccurrenceTarget = 3 }, 5);
        var all = (await rig.Series.GetStopPreviewAsync(series.Id)).Orders.Select(r => r.OrderId).ToList();

        var result = await rig.Series.StopAsync(series.Id, all, 5, true);

        Assert.Null(result.Series);
        Assert.False(await db.RecurringOrderSeries.AnyAsync(Ct));
        Assert.False(await db.Orders.AnyAsync(Ct));
        Assert.Contains(rig.Audit.Actions, a => a.Action == "RecurringSeriesRemoved");
    }

    [Fact]
    public async Task DeletingTheSourceOfAStoppedPlan_MovesTheSource_ThroughTheSameServiceDeleteUses()
    {
        using var db = Db(); var rig = await RigAsync(db);
        var series = await rig.Series.CreateAsync(1, new SaveRecurringSeriesDto(), 5);
        await rig.Series.StopAsync(series.Id, Array.Empty<int>(), 5, true);
        var source = (await db.Orders.FindAsync(new object?[] { 1 }, Ct))!;
        var earliest = (await GeneratedAsync(db)).First();

        await rig.Removal.RemoveAsync(source, 5);

        Assert.Equal(earliest.Id, (await db.RecurringOrderSeries.FindAsync(new object?[] { series.Id }, Ct))!.TemplateOrderId);
        Assert.False(await db.Orders.AnyAsync(o => o.Id == 1, Ct));
    }

    // ── A new plan after Stop ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NewPlan_StartsFromTheOldSource_AGeneratedOrder_OrAnUnrelatedOrder_OnceTheOldOneIsStopped()
    {
        using var db = Db(); var rig = await RigAsync(db);
        var old = await rig.Series.CreateAsync(1, new SaveRecurringSeriesDto { RecurringLoyaltyDiscountPercent = 10 }, 5);
        await rig.Series.StopAsync(old.Id, Array.Empty<int>(), 5, true);
        var generated = (await GeneratedAsync(db)).Last();

        var fromGenerated = await rig.Series.CreateAsync(generated.Id, new SaveRecurringSeriesDto(), 5);

        Assert.NotEqual(old.Id, fromGenerated.Id);
        Assert.Equal(generated.Id, fromGenerated.TemplateOrderId);
        Assert.Equal(fromGenerated.Id, generated.RecurringSeriesId);
        Assert.False(generated.IsGeneratedByRecurringSeries);
        Assert.Null(fromGenerated.RecurringLoyaltyDiscountPercent); // nothing is carried from the old plan
        Assert.NotNull((await db.RecurringOrderSeries.FindAsync(new object?[] { old.Id }, Ct))!.StoppedAt);
        Assert.Contains(rig.Audit.Actions, a => a.Action == "RecurringSeriesCreated" && a.EntityId == fromGenerated.Id);

        // Only one plan that is not stopped per order.
        await Assert.ThrowsAsync<RecurringSeriesException>(() => rig.Series.CreateAsync(generated.Id, new SaveRecurringSeriesDto(), 5));

        // The old source and an unrelated order may each start their own.
        Assert.NotNull(await rig.Series.CreateAsync(1, new SaveRecurringSeriesDto(), 5));
        var unrelated = NewOrder(NyTimeHelper.NowNy.Date.AddDays(1)); db.Orders.Add(unrelated); await db.SaveChangesAsync(Ct);
        Assert.NotNull(await rig.Series.CreateAsync(unrelated.Id, new SaveRecurringSeriesDto(), 5));
    }

    [Fact]
    public async Task NewPlan_WarnsWhenACleaningKeptFromTheStoppedPlanFallsOnTheSameDay()
    {
        using var db = Db(); var rig = await RigAsync(db);
        var old = await rig.Series.CreateAsync(1, new SaveRecurringSeriesDto { IntervalValue = 1 }, 5);
        await rig.Series.StopAsync(old.Id, Array.Empty<int>(), 5, true);

        var replacement = await rig.Series.CreateAsync(1, new SaveRecurringSeriesDto { IntervalValue = 1 }, 5);

        Assert.Contains(replacement.GenerationWarnings, w => w.Contains("same day"));
    }

    // ── Automatic regeneration stays stricter ────────────────────────────────────────────────

    [Fact]
    public async Task Regenerate_StillKeepsAnOccurrenceAPersonEdited()
    {
        using var db = Db(); var rig = await RigAsync(db);
        var series = await rig.Series.CreateAsync(1, new SaveRecurringSeriesDto { IntervalValue = 1 }, 5);
        var generated = await GeneratedAsync(db);
        var edited = generated[0];
        db.OrderUpdateHistories.Add(new OrderUpdateHistory { OrderId = edited.Id, IsPaid = true, AdditionalAmount = 0m });
        await db.SaveChangesAsync(Ct);

        await rig.Series.UpdateAsync(series.Id, new SaveRecurringSeriesDto { IntervalValue = 2, FutureOrdersAction = "Regenerate" }, 5);

        Assert.Equal(OrderStatuses.Pending, edited.Status);
        Assert.All(generated.Skip(1), o => Assert.Equal(OrderStatuses.Cancelled, o.Status));
    }
}

/// <summary>
/// The same Stop-and-delete and source-on-delete flows on REAL MariaDB, where the RESTRICT and
/// CASCADE foreign keys are enforced (InMemory enforces neither). Runs with DC_TEST_MARIADB.
/// </summary>
public class RecurringCleanupMariaDbTests : IClassFixture<MariaDbDatabase>
{
    private readonly MariaDbDatabase _db;
    public RecurringCleanupMariaDbTests(MariaDbDatabase db) => _db = db;

    private sealed record Plan(int UserId, int SeriesId, int SourceId, List<int> OrderIds);

    private async Task<Plan> PlanAsync(BillingHarness h, int cleanings)
    {
        var user = await h.AddUserAsync();
        var source = await h.AddOrderAsync(user.Id, 150m, serviceDate: DateTime.UtcNow.Date.AddDays(1), bookedByAdmin: false);
        int seriesId; var ids = new List<int>();
        await using (var db = h.NewContext())
        {
            var series = new RecurringOrderSeries
            {
                UserId = user.Id, TemplateOrderId = source.Id, IntervalValue = 1, IntervalUnit = RecurrenceIntervalUnit.Weeks,
                AnchorDate = DateTime.UtcNow.Date, ServiceTime = TimeSpan.FromHours(10), IsActive = true, CreatedByUserId = user.Id
            };
            db.RecurringOrderSeries.Add(series);
            await db.SaveChangesAsync();
            seriesId = series.Id;
            var s = await db.Orders.FirstAsync(o => o.Id == source.Id);
            s.RecurringSeriesId = seriesId; s.RecurrenceOccurrenceDate = s.ServiceDate;
            await db.SaveChangesAsync();
        }
        for (var i = 0; i < cleanings; i++)
        {
            var o = await h.AddOrderAsync(user.Id, 150m, seriesId: seriesId, serviceDate: DateTime.UtcNow.Date.AddDays(8 + 7 * i), bookedByAdmin: false);
            ids.Add(o.Id);
        }
        await using (var db = h.NewContext())
            await db.Orders.Where(o => ids.Contains(o.Id)).ExecuteUpdateAsync(x => x
                .SetProperty(o => o.IsGeneratedByRecurringSeries, true)
                .SetProperty(o => o.RecurrenceOccurrenceDate, o => o.ServiceDate));
        return new Plan(user.Id, seriesId, source.Id, ids);
    }

    private static RecurringOrderSeriesService Service(ApplicationDbContext db, BillingHarness h)
    {
        var payments = new RecurringCustomerPaymentService(db, h.Stripe, h.Audit, NullLogger<RecurringCustomerPaymentService>.Instance);
        return new RecurringOrderSeriesService(db, RecurringRefinementTests.Stub<IBookingCreationService>(), h.Audit,
            NullLogger<RecurringOrderSeriesService>.Instance, payments, new OrderRemovalService(db, payments, h.Stripe, h.Audit));
    }

    /// <summary>Every dependent row the delete has to clear, on every order of the plan.</summary>
    private static async Task DecorateAsync(BillingHarness h, Plan plan)
    {
        await using var db = h.NewContext();
        var cleaner = new Cleaner { FirstName = "Nino", LastName = "Beridze" };
        db.Cleaners.Add(cleaner);
        await db.SaveChangesAsync();
        foreach (var id in plan.OrderIds.Append(plan.SourceId))
        {
            db.OrderCleaners.Add(new OrderCleaner { OrderId = id, CleanerId = cleaner.Id, AssignedBy = plan.UserId,
                AutoAssignedFromSeriesId = plan.SeriesId, AssignmentNotificationSentAt = DateTime.UtcNow });
            db.NotificationLogs.Add(new NotificationLog { OrderId = id, NotificationType = "RecurringPaymentRequest", SentAt = DateTime.UtcNow });
            db.OrderUpdateHistories.Add(new OrderUpdateHistory { OrderId = id, UpdatedByUserId = plan.UserId, UpdatedAt = DateTime.UtcNow,
                IsPaid = true, AdditionalAmount = 0m });
        }
        // An abandoned "Pay all" (already cancelled): its items are RESTRICT and must be cleared.
        db.OrderPaymentBatches.Add(new OrderPaymentBatch
        {
            UserId = plan.UserId, Status = OrderPaymentBatchStatus.Canceled, Amount = 300m,
            Items = plan.OrderIds.Take(2).Select(id => new OrderPaymentBatchItem { OrderId = id, Amount = 150m }).ToList()
        });
        await db.SaveChangesAsync();
    }

    [MariaDbFact]
    public async Task Stop_DeletesTheChosenOrders_IncludingTheSource_WithRealForeignKeys()
    {
        var h = new BillingHarness(_db);
        var plan = await PlanAsync(h, 3);
        await DecorateAsync(h, plan);
        var chosen = new[] { plan.SourceId, plan.OrderIds[0], plan.OrderIds[1] };

        RecurringStopResultDto result;
        await using (var db = h.NewContext())
            result = await Service(db, h).StopAsync(plan.SeriesId, chosen, 1, mayDeleteOrders: true);

        await using var check = h.NewContext();
        Assert.Equal(chosen.OrderBy(x => x), result.DeletedOrderIds.OrderBy(x => x));
        Assert.False(await check.Orders.AnyAsync(o => chosen.Contains(o.Id)));
        Assert.False(await check.OrderCleaners.AnyAsync(c => chosen.Contains(c.OrderId)));
        Assert.False(await check.NotificationLogs.AnyAsync(n => n.OrderId != null && chosen.Contains(n.OrderId.Value)));
        Assert.False(await check.OrderPaymentBatchItems.AnyAsync(i => chosen.Contains(i.OrderId)));
        var series = await check.RecurringOrderSeries.FirstAsync(s => s.Id == plan.SeriesId);
        Assert.NotNull(series.StoppedAt);
        Assert.Equal(plan.OrderIds[2], series.TemplateOrderId);
        Assert.True(await check.Orders.AnyAsync(o => o.Id == plan.OrderIds[2] && o.Status == OrderStatuses.Pending));
    }

    [MariaDbFact]
    public async Task Stop_RollsBackTheStopAndEveryDeletion_WhenAPaymentTurnsOutToBeInFlight()
    {
        var h = new BillingHarness(_db);
        var plan = await PlanAsync(h, 2);
        // The second cleaning's payment page was submitted at Stripe a moment ago. Nothing in the
        // database says so yet — only the authoritative Stripe check, run mid-transaction, finds it.
        h.Stripe.Intents["pi_in_flight"] = new Stripe.PaymentIntent { Id = "pi_in_flight", Status = "processing" };
        await using (var db = h.NewContext())
            await db.Orders.Where(o => o.Id == plan.OrderIds[1])
                .ExecuteUpdateAsync(x => x.SetProperty(o => o.PaymentIntentId, "pi_in_flight"));

        await using (var db = h.NewContext())
        {
            var error = await Assert.ThrowsAsync<RecurringSeriesException>(() =>
                Service(db, h).StopAsync(plan.SeriesId, plan.OrderIds, 1, mayDeleteOrders: true));
            Assert.StartsWith("Nothing was changed.", error.Message);
        }

        // The first order had already been deleted and the plan stopped inside the transaction.
        await using var check = h.NewContext();
        Assert.Null((await check.RecurringOrderSeries.FirstAsync(s => s.Id == plan.SeriesId)).StoppedAt);
        Assert.Equal(2, await check.Orders.CountAsync(o => plan.OrderIds.Contains(o.Id)));
    }

    [MariaDbFact]
    public async Task DeletingTheLastOrderOfAStoppedPlan_RemovesThePlanInsteadOfTrippingTheRestrictKey()
    {
        var h = new BillingHarness(_db);
        var plan = await PlanAsync(h, 0);
        await using (var db = h.NewContext())
            await Service(db, h).StopAsync(plan.SeriesId, Array.Empty<int>(), 1, mayDeleteOrders: true);

        await using (var db = h.NewContext())
        {
            var payments = new RecurringCustomerPaymentService(db, h.Stripe, h.Audit, NullLogger<RecurringCustomerPaymentService>.Instance);
            var removal = new OrderRemovalService(db, payments, h.Stripe, h.Audit);
            var source = await db.Orders.FirstAsync(o => o.Id == plan.SourceId);
            await using var tx = await db.Database.BeginTransactionAsync();
            await removal.RemoveAsync(source, 1);
            await tx.CommitAsync();
        }

        await using var check = h.NewContext();
        Assert.False(await check.Orders.AnyAsync(o => o.Id == plan.SourceId));
        Assert.False(await check.RecurringOrderSeries.AnyAsync(s => s.Id == plan.SeriesId));
    }
}
