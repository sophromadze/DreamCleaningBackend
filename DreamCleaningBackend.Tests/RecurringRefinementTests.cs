using System.Reflection;
using DreamCleaningBackend.Controllers;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.DTOs.Commercial;
using DreamCleaningBackend.Models.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Helpers.Recurring;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Models.Commercial;
using DreamCleaningBackend.Services;
using DreamCleaningBackend.Services.Commercial;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DreamCleaningBackend.Tests;

public class RecurringRefinementTests
{
    private static ApplicationDbContext Db() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString(), b => b.EnableNullChecks(false))
        .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);

    private static Order Order(int id, DateTime date, PaymentMethod method = PaymentMethod.Normal) => new()
    {
        Id = id, UserId = 1, ServiceTypeId = 1, ServiceDate = date, ServiceTime = TimeSpan.FromHours(9),
        Status = OrderStatuses.Pending, PaymentMethod = method, Total = 925.43m, SubTotal = 849.99m,
        Tax = 75.44m, ServiceAddress = "1579 Flatbush Ave.", ContactEmail = "test@example.invalid",
        ContactFirstName = "Test", ContactLastName = "Client", ContactPhone = "2125550100"
    };

    private static async Task<RecurringOrderSeriesService> SeriesService(ApplicationDbContext db, RecordingAuditService? audit = null, IStripeService? stripe = null)
    {
        db.ServiceTypes.Add(new ServiceType { Id = 1, Name = "Residential", BasePrice = 100, TimeDuration = 120 });
        db.Users.Add(new User { Id = 1, FirstName = "Test", LastName = "Client" });
        db.Users.Add(new User { Id = 5, FirstName = "Test", LastName = "Admin" });
        db.Orders.Add(Order(1, NyTimeHelper.NowNy.Date));
        await db.SaveChangesAsync();
        var booking = Stub<IBookingCreationService>((method, args) => {
            if (method.Name != "CreateOrderAsync") throw new InvalidOperationException(method.Name);
            var dto = (CreateBookingDto)args![0]!;
            var options = (BookingCreationOptions)args[3]!;
            var o = Order(0, dto.ServiceDate, options.PaymentMethod);
            o.RecurringSeriesId = options.RecurringSeriesId;
            o.RecurrenceOccurrenceDate = options.RecurrenceOccurrenceDate;
            o.IsGeneratedByRecurringSeries = true;
            db.Orders.Add(o);
            db.SaveChanges();
            return Task.FromResult(o);
        });
        return new RecurringOrderSeriesService(db, booking, audit ?? new RecordingAuditService(), NullLogger<RecurringOrderSeriesService>.Instance,
            new RecurringCustomerPaymentService(db, stripe ?? Stub<IStripeService>(), audit ?? new RecordingAuditService(), NullLogger<RecurringCustomerPaymentService>.Instance));
    }

    [Fact]
    public async Task NewSeriesDefaultsOn_ExplicitDisableAndOmittedUpdateArePreserved()
    {
        using var db = Db(); var service = await SeriesService(db);
        var created = await service.CreateAsync(1, new SaveRecurringSeriesDto { IsActive = false }, 5);
        Assert.True(created.AutoRequestPayment);
        Assert.True(new RecurringOrderSeries().AutoRequestPayment);
        var disabled = await service.UpdateAsync(created.Id, new SaveRecurringSeriesDto { IsActive = false, AutoRequestPayment = false }, 5);
        Assert.False(disabled.AutoRequestPayment);
        var unchanged = await service.UpdateAsync(created.Id, new SaveRecurringSeriesDto { IsActive = false }, 5);
        Assert.False(unchanged.AutoRequestPayment);
    }

    [Fact]
    public async Task PauseResumeAndStopPreserveOrders_ResumeIsIdempotent()
    {
        using var db = Db(); var service = await SeriesService(db);
        var series = await service.CreateAsync(1, new SaveRecurringSeriesDto { IsActive = false }, 5);
        Assert.Equal(0, (await service.GenerateAsync(series.Id)).CreatedCount);
        await service.SetStateAsync(series.Id, "resume", 5);
        var count = await db.Orders.CountAsync(); Assert.True(count > 1);
        await service.SetStateAsync(series.Id, "resume", 5);
        Assert.Equal(count, await db.Orders.CountAsync());
        await service.SetStateAsync(series.Id, "pause", 5);
        Assert.Equal(0, (await service.GenerateAsync(series.Id)).CreatedCount);
        await service.SetStateAsync(series.Id, "stop", 5);
        Assert.Equal(0, (await service.GenerateAsync(series.Id)).CreatedCount);
        await Assert.ThrowsAsync<RecurringSeriesException>(() => service.SetStateAsync(series.Id, "resume", 5));
        Assert.Equal(count, await db.Orders.CountAsync());
    }

    [Fact]
    public async Task StoppedPlanWithFutureOrdersRemovedCanBeReplacedFromItsOriginalSource()
    {
        using var db = Db(); var service = await SeriesService(db);
        var first = await service.CreateAsync(1, new SaveRecurringSeriesDto(), 5);
        await service.SetStateAsync(first.Id, "stop", 5);
        var stoppedAt = (await db.RecurringOrderSeries.FindAsync(first.Id))!.StoppedAt;
        db.Orders.RemoveRange(await db.Orders.Where(o => o.IsGeneratedByRecurringSeries).ToListAsync());
        var past = Order(0, NyTimeHelper.NowNy.Date.AddDays(-7));
        past.RecurringSeriesId = first.Id; past.IsGeneratedByRecurringSeries = true; past.IsPaid = true;
        db.Orders.Add(past); await db.SaveChangesAsync();
        var source = (await db.Orders.FindAsync(1))!;
        var total = source.Total;
        var replacement = await service.CreateAsync(1, new SaveRecurringSeriesDto { IntervalValue = 2 }, 5);
        Assert.NotEqual(first.Id, replacement.Id);
        Assert.Equal(replacement.Id, (await service.GetForOrderAsync(1))!.Id);
        Assert.Equal(total, source.Total);
        Assert.Equal(stoppedAt, (await db.RecurringOrderSeries.FindAsync(first.Id))!.StoppedAt);
        Assert.False((await db.RecurringOrderSeries.FindAsync(first.Id))!.IsActive);
        Assert.Equal(first.Id, past.RecurringSeriesId); Assert.True(past.IsPaid);
        Assert.Contains(replacement.Occurrences, o => o.WasGenerated);
        Assert.Equal(0, (await service.GenerateAsync(first.Id)).CreatedCount);
        await Assert.ThrowsAsync<RecurringSeriesException>(() => service.CreateAsync(1, new SaveRecurringSeriesDto(), 5));
        Assert.Equal(2, await db.RecurringOrderSeries.CountAsync());
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("stop")]
    public async Task ReplacementCannotBypassPausedPlansOrRemainingFutureCleanings(string state)
    {
        using var db = Db(); var service = await SeriesService(db);
        var first = await service.CreateAsync(1, new SaveRecurringSeriesDto(), 5);
        await service.SetStateAsync(first.Id, state, 5);
        await Assert.ThrowsAsync<RecurringSeriesException>(() => service.CreateAsync(1, new SaveRecurringSeriesDto(), 5));
        var generated = await db.Orders.FirstAsync(o => o.IsGeneratedByRecurringSeries);
        await Assert.ThrowsAsync<RecurringSeriesException>(() => service.CreateAsync(generated.Id, new SaveRecurringSeriesDto(), 5));
        Assert.Single(await db.RecurringOrderSeries.ToListAsync());
        Assert.Equal(first.Id, (await db.Orders.FindAsync(1))!.RecurringSeriesId);
    }

    [Fact]
    public async Task SkipKeepsOccurrenceKeyAndAnchor_AndNeverRefillsTheSkippedDate()
    {
        using var db = Db(); var service = await SeriesService(db);
        var series = await service.CreateAsync(1, new SaveRecurringSeriesDto { IntervalValue = 2 }, 5);
        var future = await db.Orders.Where(o => o.IsGeneratedByRecurringSeries).OrderBy(o => o.ServiceDate).ToListAsync();
        var first = future.First(); var date = first.RecurrenceOccurrenceDate;
        var later = future.Last().ServiceDate;
        await service.SkipAsync(series.Id, first.Id, 5);
        await service.GenerateAsync(series.Id);
        Assert.Equal(OrderStatuses.Cancelled, first.Status);
        Assert.Equal(date, first.RecurrenceOccurrenceDate);
        Assert.Equal(later, future.Last().ServiceDate);
        Assert.Equal(series.AnchorDate, (await service.GetAsync(series.Id))!.AnchorDate);
        Assert.Single(await db.Orders.Where(o => o.RecurringSeriesId == series.Id && o.RecurrenceOccurrenceDate == date).ToListAsync());
    }

    [Theory]
    [InlineData("Keep")]
    [InlineData("Regenerate")]
    public async Task RuleEditRequiresExplicitChoiceAndPreservesProtectedHistory(string choice)
    {
        using var db = Db(); var service = await SeriesService(db);
        var series = await service.CreateAsync(1, new SaveRecurringSeriesDto { IntervalValue = 1 }, 5);
        var old = await db.Orders.Where(o => o.IsGeneratedByRecurringSeries).OrderBy(o => o.ServiceDate).ToListAsync();
        var edit = new SaveRecurringSeriesDto { IntervalValue = 2 };
        await Assert.ThrowsAsync<RecurringSeriesException>(() => service.UpdateAsync(series.Id, edit, 5));
        edit.FutureOrdersAction = choice;
        await service.UpdateAsync(series.Id, edit, 5);
        Assert.Equal(old.Count + 1, await db.Orders.CountAsync(o => old.Select(x => x.Id).Contains(o.Id) || o.Id == 1));
        Assert.All(old, o => Assert.Equal(choice == "Keep" ? OrderStatuses.Pending : OrderStatuses.Cancelled, o.Status));
        if (choice == "Keep") Assert.Equal(old.Last().ServiceDate, (await db.RecurringOrderSeries.FindAsync(series.Id))!.GenerateAfterDate);
    }

    [Fact]
    public async Task RegenerationRetiresOnlyUncommittedOrdersAndKeepsAPaidBoundary()
    {
        using var db = Db(); var audit = new RecordingAuditService(); var service = await SeriesService(db, audit);
        var series = await service.CreateAsync(1, new SaveRecurringSeriesDto(), 5);
        var future = await db.Orders.Where(o => o.IsGeneratedByRecurringSeries).OrderBy(o => o.ServiceDate).ToListAsync();
        var paid = future[1]; paid.IsPaid = true; await db.SaveChangesAsync();
        await service.UpdateAsync(series.Id, new SaveRecurringSeriesDto { IntervalValue = 2, FutureOrdersAction = "Regenerate" }, 5);
        Assert.Equal(OrderStatuses.Pending, paid.Status); Assert.True(paid.IsPaid);
        Assert.NotNull(paid.RecurrenceOccurrenceDate);
        Assert.Equal(paid.ServiceDate, (await db.RecurringOrderSeries.FindAsync(series.Id))!.GenerateAfterDate);
        Assert.All(future.Where(o => o.Id != paid.Id), o => Assert.Equal(OrderStatuses.Cancelled, o.Status));
        Assert.Contains(audit.Actions, a => a.Action == "RecurringOccurrenceReplaced");
        var next = await db.Orders.FirstAsync(o => o.IsGeneratedByRecurringSeries && o.Status == OrderStatuses.Pending && !o.IsPaid);
        await service.SkipAsync(series.Id, next.Id, 5);
        Assert.Contains(audit.Actions, a => a.Action == "RecurringOccurrenceSkipped" && a.ActingUserId == 5);
        Assert.True((await service.GetAsync(series.Id))!.Occurrences.Single(o => o.OrderId == next.Id).IsSkipped);
    }

    [Fact]
    public async Task RegenerationReplacesUnnotifiedCleaningsAfterAbandonedCombinedPayment()
    {
        using var db = Db();
        var intent = new Stripe.PaymentIntent { Id = "pi_abandoned", Status = "requires_payment_method" };
        var cancellations = 0;
        var stripe = Stub<IStripeService>((method, args) => {
            Assert.Equal(intent.Id, args![0]);
            if (method.Name == "CancelPaymentIntentAsync") { intent.Status = "canceled"; cancellations++; }
            else Assert.Equal("GetPaymentIntentAsync", method.Name);
            return Task.FromResult(intent);
        });
        var service = await SeriesService(db, stripe: stripe);
        var series = await service.CreateAsync(1, new SaveRecurringSeriesDto(), 5);
        var old = await db.Orders.Where(o => o.IsGeneratedByRecurringSeries).ToListAsync();
        var batch = new OrderPaymentBatch { UserId = 1, Status = OrderPaymentBatchStatus.Pending, PaymentIntentId = intent.Id };
        foreach (var order in old)
        {
            batch.Items.Add(new OrderPaymentBatchItem { OrderId = order.Id, Amount = order.Total });
            db.OrderCleaners.Add(new OrderCleaner { OrderId = order.Id, AutoAssignedFromSeriesId = series.Id });
        }
        db.OrderPaymentBatches.Add(batch); await db.SaveChangesAsync();
        await service.UpdateAsync(series.Id, new SaveRecurringSeriesDto { IntervalValue = 2, FutureOrdersAction = "Regenerate" }, 5);
        Assert.Equal(1, cancellations); Assert.Equal(OrderPaymentBatchStatus.Canceled, batch.Status);
        Assert.All(old, order => { Assert.Equal(OrderStatuses.Cancelled, order.Status); Assert.Null(order.RecurrenceOccurrenceDate); });
        Assert.True(await db.Orders.AnyAsync(o => o.IsGeneratedByRecurringSeries && o.Status == OrderStatuses.Pending));
        Assert.Equal(old.Count, await db.OrderPaymentBatchItems.CountAsync());
    }

    [Fact]
    public async Task CustomerRecurringMutationsAreBlockedByTheService()
    {
        using var db = Db(); var o = Order(1, DateTime.Today.AddDays(10)); o.RecurringSeriesId = 1;
        var repository = Stub<DreamCleaningBackend.Repositories.Interfaces.IOrderRepository>((m,a) => Task.FromResult(o));
        var service = new DreamCleaningBackend.Services.OrderService(repository, db, Stub<IStripeService>(), Stub<IEmailService>(),
            Stub<ISmsService>(), new ConfigurationBuilder().Build(), NullLogger<DreamCleaningBackend.Services.OrderService>.Instance, Stub<ILoyaltyDiscountService>());
        foreach (var action in new Func<Task>[] {
            () => service.CancelOrder(1, 1, new CancelOrderDto()),
            () => service.UpdateOrder(1, 1, new UpdateOrderDto()),
            () => service.CalculateAdditionalAmount(1, new UpdateOrderDto()),
            () => service.CreateUpdatePaymentIntent(1, 1, new UpdateOrderDto()) })
        {
            var error = await Assert.ThrowsAsync<Exception>(action);
            Assert.Contains("contact Dream Cleaning", error.Message);
        }
        Assert.Equal(OrderStatuses.Pending, o.Status); Assert.Equal(925.43m, o.Total);
    }

    [Fact]
    public void InFlightNearestOrderDoesNotUnlockTheNextSinglePayment()
    {
        var rows = new[] {
            new RecurringPayableOccurrence { OrderId = 1, ServiceDateTime = DateTime.Today, AmountDue = 100, PaymentInFlight = true },
            new RecurringPayableOccurrence { OrderId = 2, ServiceDateTime = DateTime.Today.AddDays(7), AmountDue = 100 } };
        Assert.All(RecurringPaymentPolicy.ResolvePayability(rows), v => Assert.False(v.IsPayable));
        Assert.Equal(new[] { 2 }, RecurringPaymentPolicy.ResolveCombinedPaymentSet(rows));
    }

    [Theory]
    [InlineData("paid")]
    [InlineData("invoice")]
    [InlineData("notified")]
    [InlineData("active")]
    public async Task SkipRejectsPaymentInvoiceAndOperationalCommitments(string kind)
    {
        using var db = Db(); var service = await SeriesService(db);
        var series = await service.CreateAsync(1, new SaveRecurringSeriesDto(), 5);
        var o = await db.Orders.FirstAsync(o => o.IsGeneratedByRecurringSeries);
        if (kind == "paid") o.IsPaid = true;
        if (kind == "active") o.Status = OrderStatuses.Active;
        if (kind == "notified") db.OrderCleaners.Add(new OrderCleaner { OrderId = o.Id, AssignmentNotificationSentAt = DateTime.UtcNow });
        if (kind == "invoice") db.CommercialInvoiceOrders.Add(new CommercialInvoiceOrder { OrderId = o.Id,
            Invoice = new CommercialInvoice { Status = InvoiceStatus.Draft } });
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<RecurringSeriesException>(() => service.SkipAsync(series.Id, o.Id, 5));
        Assert.NotEqual(OrderStatuses.Cancelled, o.Status);
    }

    [Fact]
    public async Task PayAllIncludesOnlyOnlineOrders_ExcludesInFlightWithoutChangingMethods()
    {
        using var db = Db(); var date = NyTimeHelper.NowNy.Date.AddDays(1);
        db.Users.Add(new User { Id = 1, FirstName = "Test", LastName = "Client" });
        db.ServiceTypes.Add(new ServiceType { Id = 1, Name = "Residential" });
        var methods = new[] { PaymentMethod.Normal, PaymentMethod.Normal, PaymentMethod.Cash, PaymentMethod.Invoice, PaymentMethod.Normal };
        for (int i = 0; i < methods.Length; i++) { var o = Order(i + 1, date.AddDays(i), methods[i]); o.RecurringSeriesId = 1; db.Orders.Add(o); }
        db.OrderPaymentBatchItems.Add(new OrderPaymentBatchItem { OrderId = 5, Batch = new OrderPaymentBatch { Status = OrderPaymentBatchStatus.Processing } });
        await db.SaveChangesAsync();
        decimal charged = 0;
        var stripe = Stub<IStripeService>((m,a) => {
            if (m.Name == "GetPaymentIntentAsync") return Task.FromResult(new Stripe.PaymentIntent { Id = "pi_refinement_fake", Status = "processing" });
            Assert.Equal("CreatePaymentIntentAsync", m.Name); charged = (decimal)a![0]!;
            return Task.FromResult(new Stripe.PaymentIntent { Id = "pi_refinement_fake", ClientSecret = "fake_secret" });
        });
        var service = new RecurringCustomerPaymentService(db, stripe, new RecordingAuditService(), NullLogger<RecurringCustomerPaymentService>.Instance);
        var upcoming = await service.GetUpcomingAsync(1);
        Assert.Equal(1850.86m, upcoming.PayAllTotal); Assert.Equal(2, upcoming.PayAllCount);
        Assert.Equal(new[] { 1, 2 }, upcoming.Orders.Where(o => o.IncludedInPayAll).Select(o => o.OrderId));
        Assert.True(upcoming.Orders[0].IsPayable); Assert.False(upcoming.Orders[1].IsPayable);
        (await db.Orders.FindAsync(1))!.IsPaid = true; await db.SaveChangesAsync();
        Assert.True((await service.GetUpcomingAsync(1)).Orders.Single(o => o.OrderId == 2).IsPayable);
        await service.StartCombinedPaymentAsync(1, new StartCombinedPaymentDto());
        Assert.Equal(925.43m, charged);
        var batch = await db.OrderPaymentBatches.Include(b => b.Items).SingleAsync(b => b.PaymentIntentId == "pi_refinement_fake");
        Assert.Equal(new[] { 2 }, batch.Items.Select(i => i.OrderId));
        await Assert.ThrowsAsync<CombinedPaymentException>(() => service.StartCombinedPaymentAsync(1, new StartCombinedPaymentDto()));
        Assert.Equal(methods, await db.Orders.OrderBy(o => o.Id).Select(o => o.PaymentMethod).ToArrayAsync());
    }

    [Fact]
    public async Task InvoiceAllocationDoesNotMovePayHoursSplitsTipsOrPaidLines()
    {
        using var db = Db(); var o = Order(1, DateTime.Today); o.Tips = 20; o.TotalDuration = 600;
        o.MaidsCount = 2; o.CleanerHourlyRate = 23; o.CleanerTotalSalary = 230;
        var line = new OrderCleaner { Id = 1, OrderId = 1, CleanerId = 1, SalaryHourlyRate = 25,
            SalaryBillableMinutes = 300, IsPaid = true, PaidAmount = 135, PaidAt = DateTime.UtcNow };
        db.Orders.Add(o); db.OrderCleaners.Add(line); await db.SaveChangesAsync();
        var before = System.Text.Json.JsonSerializer.Serialize(CleanerPayrollCalculator.Build(o, false, new[] { line }));
        var service = new OrderInvoiceAllocationService(db, new RecordingAuditService(), NullLogger<OrderInvoiceAllocationService>.Instance);
        var snapshot = await service.ApplyAllocatedTotalAsync(1, 875, 7, 1, "DCI-2026-12345678", 5);
        // Adopting the order onto the invoice is the ONLY billing change: the method, the client
        // and the previous method for a later void. Nothing about the crew's pay may move with it.
        Assert.Equal(PaymentMethod.Invoice, o.PaymentMethod); Assert.Equal(7, o.ContractClientId);
        Assert.Equal((int)PaymentMethod.Normal, snapshot.PreviousPaymentMethod);
        Assert.Null(snapshot.PreviousContractClientId);
        Assert.Equal(875, o.Total); Assert.Equal(20, o.Tips); Assert.Equal(230, o.CleanerTotalSalary);
        Assert.Equal(135, line.PaidAmount); Assert.Equal(600, o.TotalDuration);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(CleanerPayrollCalculator.Build(o, false, new[] { line })));
    }

    [Theory]
    [InlineData(InvoicePaymentProvider.Manual)]
    [InlineData(InvoicePaymentProvider.Stripe)]
    public async Task ActualAdminReportsCountGrossTaxNetOnce_WithOrWithoutLinkedOrders(InvoicePaymentProvider provider)
    {
        using var db = Db(); var date = new DateTime(2026, 10, 4);
        var invoice = new CommercialInvoice { Id = 1, Total = 925.43m, SubTotal = 849.99m,
            TaxAmount = 75.44m, TaxRate = 8.875m, Status = InvoiceStatus.Paid, FirstSentAt = date,
            PaidAt = date, AmountPaid = 925.43m };
        invoice.Payments.Add(new CommercialInvoicePayment { Amount = 925.43m, PaymentDate = date, Provider = provider });
        db.CommercialInvoices.Add(invoice); await db.SaveChangesAsync();
        var controller = new AdminStatisticsController(db, new ConfigurationBuilder().Build(),
            Stub<IExpenseService>(), Stub<IFinancialRateService>(), new RecordingAuditService(), Stub<IAdminBonusService>());
        for (var linked = 0; linked < 2; linked++)
        {
            var result = await controller.GetOrderStatistics(date, date);
            var stats = Assert.IsType<OrderStatisticsDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
            Assert.Equal(925.43m, stats.TotalAmount + stats.TotalTaxes);
            Assert.Equal(75.44m, stats.TotalTaxes); Assert.Equal(849.99m, stats.TotalAmount);
            var daily = await controller.GetDailyStatistics(date, date);
            var rows = Assert.IsType<List<DailyStatisticsDto>>(Assert.IsType<OkObjectResult>(daily.Result).Value);
            Assert.Equal(925.43m, rows.Sum(r => r.Amount + r.Taxes)); Assert.Equal(75.44m, rows.Sum(r => r.Taxes));
            if (linked == 0) {
                var o = Order(1, date, PaymentMethod.Invoice); o.Status = OrderStatuses.Done; o.InvoicePaidAt = date;
                db.Orders.Add(o); db.CommercialInvoiceOrders.Add(new CommercialInvoiceOrder { CommercialInvoiceId = 1, OrderId = 1, AllocatedAmount = 925.43m });
                await db.SaveChangesAsync();
            }
        }
    }

    [Fact]
    public async Task DraftMatchingUsesOverlap_DifferentAndUndatedDraftsAreSeparate()
    {
        using var db = Db(); var october = new DateTime(2026, 10, 1);
        db.CommercialInvoices.AddRange(
            new CommercialInvoice { Id = 1, ContractId = 1, Status = InvoiceStatus.Draft, ServiceStartDate = october, ServiceEndDate = october.AddDays(14) },
            new CommercialInvoice { Id = 2, ContractId = 1, Status = InvoiceStatus.Draft, ServiceStartDate = october.AddMonths(-1), ServiceEndDate = october.AddDays(-1) },
            new CommercialInvoice { Id = 3, ContractId = 1, Status = InvoiceStatus.Draft });
        await db.SaveChangesAsync();
        var service = new RecurringInvoiceService(db, null!, null!, null!, NullLogger<RecurringInvoiceService>.Instance);
        Assert.Equal(1, (await service.FindOpenDraftAsync(1, october.AddDays(10), october.AddDays(30)))!.Id);
        Assert.Null(await service.FindOpenDraftAsync(1, october.AddMonths(1), october.AddMonths(2)));
        Assert.Equal(3, (await service.FindUndatedDraftAsync(1))!.Id);
        Assert.True(ExistingDraftInvoiceException.For((await db.CommercialInvoices.FindAsync(3))!, true).Draft.IsUndated);
    }

    [Theory]
    [InlineData(PaymentMethod.Invoice, null)]
    [InlineData(PaymentMethod.Normal, 3)]
    public async Task CommercialOrdersNeverConsumeResidentialLifetimeOrOneTimeLoyalty(PaymentMethod method, int? client)
    {
        using var db = Db(); var o = Order(1, DateTime.Today, method); o.ContractClientId = client;
        o.LoyaltyDiscountAmount = 15; o.LoyaltyDiscountPercentage = 15;
        db.Users.Add(new User { Id = 1, LoyaltyDiscountPercentage = 15 }); db.Orders.Add(o); await db.SaveChangesAsync();
        Assert.False(ResidentialLoyaltyPolicy.AppliesTo(o));
        var service = new LoyaltyDiscountService(db, new RecordingAuditService(), NullLogger<LoyaltyDiscountService>.Instance);
        await service.ApplyToOrderAsync(1);
        Assert.Equal(15, (await db.Users.FindAsync(1))!.LoyaltyDiscountPercentage);
        Assert.True(ResidentialLoyaltyPolicy.AppliesTo(Order(2, DateTime.Today)));
    }

    [Theory]
    [InlineData(PaymentMethod.Invoice, null, false)]
    [InlineData(PaymentMethod.Invoice, null, true)]
    [InlineData(PaymentMethod.Normal, 3, true)]
    [InlineData(PaymentMethod.Normal, null, true)]
    public async Task RealBookingPricingExcludesCommercialLoyaltyButPreservesResidential(PaymentMethod method, int? client, bool lifetime)
    {
        using var db = Db();
        db.ServiceTypes.Add(new ServiceType { Id = 1, Name = "Residential", BasePrice = 100, TimeDuration = 120 });
        db.Users.Add(new User { Id = 1, LoyaltyDiscountPercentage = 15, LoyaltyDiscountIsLifetime = lifetime });
        await db.SaveChangesAsync();
        var loyalty = new LoyaltyDiscountService(db, new RecordingAuditService(), NullLogger<LoyaltyDiscountService>.Instance);
        var service = new BookingCreationService(db, loyalty, Stub<IGiftCardService>(), NullLogger<BookingCreationService>.Instance);
        var dto = new CreateBookingDto { ServiceTypeId = 1, ServiceDate = DateTime.Today.AddDays(7), ServiceTime = "09:00", MaidsCount = 1 };
        var order = await service.CreateOrderAsync(dto, 1, false, new BookingCreationOptions { PaymentMethod = method, ContractClientId = client });
        Assert.Equal(method == PaymentMethod.Normal && client == null ? 15 : 0, order.LoyaltyDiscountPercentage);
        Assert.Equal(method == PaymentMethod.Normal && client == null ? order.SubTotal * .15m : 0, order.LoyaltyDiscountAmount);
        Assert.Equal(15, (await db.Users.FindAsync(1))!.LoyaltyDiscountPercentage);
        Assert.Equal(lifetime, (await db.Users.FindAsync(1))!.LoyaltyDiscountIsLifetime);
    }

    [Fact]
    public async Task CampaignUsesResidentialHistoryOnly_AndStillServesMixedCustomers()
    {
        using var db = Db();
        for (var id = 1; id <= 3; id++) {
            db.Users.Add(new User { Id = id, IsActive = true, Email = "test@example.invalid", LastCompletedOrderDate = DateTime.UtcNow.AddDays(-60), CanReceiveEmails = false, CanReceiveMessages = false });
            var o = Order(id, DateTime.UtcNow.Date.AddDays(-60), id == 1 ? PaymentMethod.Invoice : PaymentMethod.Normal);
            o.UserId = id; o.Status = OrderStatuses.Done; db.Orders.Add(o);
        }
        // User 2's only apparently residential order is actually invoice-backed.
        db.CommercialInvoiceOrders.Add(new CommercialInvoiceOrder { OrderId = 2 });
        // A mixed customer's future commercial appointment must not suppress residential eligibility.
        var future = Order(4, DateTime.UtcNow.AddDays(7), PaymentMethod.Invoice); future.UserId = 3; db.Orders.Add(future);
        await db.SaveChangesAsync();
        var settings = Stub<IBubbleRewardsSettingsService>((m, args) => typeof(Task).GetMethod(nameof(Task.FromResult))!
            .MakeGenericMethod(m.ReturnType.GetGenericArguments()[0]).Invoke(null, new[] { args![1] }));
        using var provider = new ServiceCollection().AddSingleton(db).AddSingleton(settings)
            .AddSingleton(Stub<IEmailService>((m,a) => throw new InvalidOperationException("No email expected")))
            .AddSingleton(Stub<ISmsService>((m,a) => throw new InvalidOperationException("No SMS expected")))
            .AddSingleton<IAuditService>(new RecordingAuditService()).BuildServiceProvider();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Loyalty:ForceRun"] = "true" }).Build();
        var environment = Stub<IHostEnvironment>((m,a) => m.Name == "get_EnvironmentName" ? "Development" : null);
        using var worker = new LoyaltyReengagementService(provider, NullLogger<LoyaltyReengagementService>.Instance, environment, config);
        await (Task)typeof(LoyaltyReengagementService).GetMethod("RunCycleAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(worker, new object[] { CancellationToken.None })!;
        Assert.Equal(0, (await db.Users.FindAsync(1))!.LoyaltyDiscountPercentage);
        Assert.Equal(0, (await db.Users.FindAsync(2))!.LoyaltyDiscountPercentage);
        Assert.Equal(10, (await db.Users.FindAsync(3))!.LoyaltyDiscountPercentage);
        Assert.Empty(await db.NotificationLogs.Where(n => n.CustomerId == 1 || n.CustomerId == 2).ToListAsync());
    }

    [Fact]
    public async Task DraftChoicesAreAudited_OnlyChosenWarningsDisappear_AndPaidInvoicesRejectThem()
    {
        using var db = Db();
        db.ContractClients.Add(new ContractClient { Id = 1, LegalEntityName = "Test business" });
        db.CommercialInvoices.Add(new CommercialInvoice { Id = 1, ContractClientId = 1, Status = InvoiceStatus.Draft,
            DraftWarningsJson = "[\"Current contract pricing differs.\",\"Current contract tax rate differs.\"]" });
        await db.SaveChangesAsync();
        var service = new InvoiceService(db, new InvoiceNumberService(db, NullLogger<InvoiceNumberService>.Instance),
            new BillingSettingsService(db), new ConfigurationBuilder().Build(), NullLogger<InvoiceService>.Instance);
        var dto = new SaveInvoiceDto { ContractClientId = 1, TaxType = InvoiceTaxType.Included, TaxRate = 8.875m,
            Items = new() { new SaveInvoiceItemDto { Description = "Cleaning", Quantity = 1, UnitPrice = 925.43m } } };
        var saved = await service.UpdateAsync(1, dto, 5);
        Assert.Equal(2, InvoiceService.ParseDraftWarnings(saved.DraftWarningsJson).Count);
        dto.DraftDriftChoices = new() { "KeepPrice" }; saved = await service.UpdateAsync(1, dto, 5);
        Assert.Single(InvoiceService.ParseDraftWarnings(saved.DraftWarningsJson));
        Assert.Equal(925.43m, saved.Total);
        dto.DraftDriftChoices = new() { "CurrentTax" }; dto.TaxRate = 9;
        saved = await service.UpdateAsync(1, dto, 5);
        Assert.Null(saved.DraftWarningsJson); Assert.Equal(925.43m, saved.Total); Assert.Equal(76.41m, saved.TaxAmount);
        Assert.Equal(2, await db.CommercialInvoiceActivityLogs.CountAsync(a => a.Action == "invoice_drift_choice"));
        Assert.Null(saved.FirstSentAt); Assert.Equal(InvoiceStatus.Draft, saved.Status);
        saved.Status = InvoiceStatus.Paid; saved.FirstSentAt = DateTime.UtcNow; await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvoiceWorkflowException>(() => service.UpdateAsync(1, dto, 5));
        Assert.Equal(925.43m, saved.Total);
    }

    public static T Stub<T>(Func<MethodInfo, object?[]?, object?>? handler = null) where T : class
    {
        var proxy = DispatchProxy.Create<T, EmptyServiceProxy>();
        ((EmptyServiceProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    public class EmptyServiceProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?>? Handler { get; set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (Handler != null) return Handler(method!, args);
            var type = method!.ReturnType;
            if (type == typeof(Task)) return Task.CompletedTask;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>)) {
                var valueType = type.GetGenericArguments()[0];
                var value = valueType == typeof(string) ? null : Activator.CreateInstance(valueType);
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(valueType).Invoke(null, new[] { value });
            }
            throw new InvalidOperationException("Unexpected external call: " + method.Name);
        }
    }
}
