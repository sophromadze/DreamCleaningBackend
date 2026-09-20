using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Stripe;
using Xunit;
using PaymentMethod = DreamCleaningBackend.Models.PaymentMethod;
using DreamCleaningBackend.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;

namespace DreamCleaningBackend.Tests;

public class RecurringPaymentStateTests
{
    private sealed class Fixture : IDisposable
    {
        public readonly DreamCleaningBackend.Data.ApplicationDbContext Db = RecurringDiscountRegressionTests.Db();
        public readonly Dictionary<string, PaymentIntent> Intents = new();
        public readonly List<string> Canceled = new();
        public bool ConfirmationWinsCancellation;
        public readonly RecurringCustomerPaymentService Service;
        public Fixture()
        {
            Db.Users.Add(new User { Id = 1, FirstName = "Test", LastName = "Customer" });
            Db.ServiceTypes.Add(new ServiceType { Id = 1, Name = "Residential" });
            for (var id = 1; id <= 3; id++) Db.Orders.Add(new Order { Id = id, UserId = 1, ServiceTypeId = 1,
                RecurringSeriesId = 1, ServiceDate = NyTimeHelper.NowNy.Date.AddDays(id), ServiceTime = TimeSpan.FromHours(9),
                Total = 100, SubTotal = 91.85m, Tax = 8.15m, Status = OrderStatuses.Pending, PaymentMethod = PaymentMethod.Normal });
            Db.SaveChanges();
            var stripe = RecurringDiscountRegressionTests.Stub<IStripeService>((method, args) => {
                if (method.Name == "CreatePaymentIntentAsync") {
                    var intent = new PaymentIntent { Id = $"pi_test_{Intents.Count + 1}", Status = "requires_payment_method",
                        ClientSecret = "test-only-secret", Amount = (long)((decimal)args![0]! * 100), Metadata = (Dictionary<string,string>)args[1]! };
                    Intents.Add(intent.Id, intent); return Task.FromResult(intent);
                }
                var old = Intents[(string)args![0]!];
                if (method.Name == "CancelPaymentIntentAsync") {
                    if (ConfirmationWinsCancellation) { old.Status = "processing"; throw new InvalidOperationException("Stripe rejected cancellation: confirmation won"); }
                    old.Status = "canceled"; Canceled.Add(old.Id);
                } else Assert.Equal("GetPaymentIntentAsync", method.Name);
                return Task.FromResult(old);
            });
            Service = new RecurringCustomerPaymentService(Db, stripe, new RecordingAuditService(), NullLogger<RecurringCustomerPaymentService>.Instance);
        }
        public Task<CombinedPaymentDto> Start() => Service.StartCombinedPaymentAsync(1, new StartCombinedPaymentDto());
        public RecurringOrderSeriesService SeriesService() => new(Db,
            RecurringDiscountRegressionTests.Stub<IBookingCreationService>(), new RecordingAuditService(),
            NullLogger<RecurringOrderSeriesService>.Instance, Service);
        public async Task<Order> GeneratedOrder()
        {
            var order = (await Db.Orders.FindAsync(1))!;
            order.IsGeneratedByRecurringSeries = true;
            order.RecurrenceOccurrenceDate = order.ServiceDate;
            Db.OrderCleaners.Add(new OrderCleaner { OrderId = order.Id, AutoAssignedFromSeriesId = 1 });
            await Db.SaveChangesAsync();
            return order;
        }
        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public async Task OpeningAndAbandoningKeepsPayAllAndNearestIndividualAvailable_RetryCancelsOldSecret()
    {
        using var f = new Fixture(); var first = await f.Start();
        var view = await f.Service.GetUpcomingAsync(1);
        Assert.True(view.CanPayAll); Assert.Equal(3, view.PayAllCount); Assert.True(view.Orders[0].IsPayable);
        Assert.DoesNotContain(view.Orders, o => o.BlockedReason?.Contains("processed") == true);
        var retry = await f.Start();
        Assert.NotEqual(first.PaymentIntentId, retry.PaymentIntentId);
        Assert.Contains(first.PaymentIntentId, f.Canceled);
        Assert.Equal(OrderPaymentBatchStatus.Canceled, (await f.Db.OrderPaymentBatches.FindAsync(first.BatchId))!.Status);
        Assert.Equal(OrderPaymentBatchStatus.Pending, (await f.Db.OrderPaymentBatches.FindAsync(retry.BatchId))!.Status);
        Assert.All(await f.Db.Orders.ToListAsync(), o => Assert.False(o.IsPaid));
    }

    [Fact]
    public async Task IndividualPaymentInvalidatesTheEntireAbandonedCombinedIntent()
    {
        using var f = new Fixture(); var batch = await f.Start();
        await f.Service.PrepareIndividualPaymentAsync(1);
        Assert.Contains(batch.PaymentIntentId, f.Canceled);
        Assert.True((await f.Service.GetUpcomingAsync(1)).Orders[0].IsPayable);
    }

    [Theory]
    [InlineData("processing")]
    [InlineData("requires_capture")]
    public async Task AuthoritativeSubmittedStateBlocksCombinedAndIndividualPayment(string state)
    {
        using var f = new Fixture(); var batch = await f.Start(); f.Intents[batch.PaymentIntentId].Status = state;
        await f.Service.RefreshBatchStateAsync(batch.PaymentIntentId);
        var view = await f.Service.GetUpcomingAsync(1);
        Assert.False(view.CanPayAll); Assert.All(view.Orders, o => Assert.False(o.IsPayable));
        await Assert.ThrowsAsync<CombinedPaymentException>(() => f.Start());
        await Assert.ThrowsAsync<CombinedPaymentException>(() => f.Service.PrepareIndividualPaymentAsync(1));
        Assert.Empty(f.Canceled); Assert.Single(f.Intents);
    }

    [Theory]
    [InlineData("canceled")]
    [InlineData("requires_payment_method")]
    public async Task CanceledOrFailedAttemptsRetainHistoryAndReleaseOrders(string state)
    {
        using var f = new Fixture(); var batch = await f.Start(); var intent = f.Intents[batch.PaymentIntentId];
        intent.Status = state; intent.LastPaymentError = state == "requires_payment_method" ? new StripeError { Message = "Declined" } : null;
        await f.Service.MarkBatchFailedAsync(intent.Id, "Declined");
        Assert.Equal(state == "canceled" ? OrderPaymentBatchStatus.Canceled : OrderPaymentBatchStatus.Failed,
            (await f.Db.OrderPaymentBatches.FindAsync(batch.BatchId))!.Status);
        Assert.True((await f.Service.GetUpcomingAsync(1)).CanPayAll);
        Assert.All(await f.Db.Orders.ToListAsync(), o => Assert.False(o.IsPaid));
        await f.Start(); Assert.Equal(2, await f.Db.OrderPaymentBatches.CountAsync());
    }

    // "Success between page load and retry settles exactly once" MOVED to
    // Billing/CombinedPaymentSettlementTests (2026-09). It lived here on the InMemory provider,
    // which enforces no unique index — so it asserted every order carried the batch's intent,
    // the very write that violates IX_Orders_PaymentIntentId on a real database and left paid
    // customers with unpaid cleanings. Settlement now uses conditional UPDATEs, which only a
    // relational provider runs, so the scenario is asserted on MariaDB instead.

    [Fact]
    public async Task ConfirmationRacingCancellationCannotIssueASecondIntent()
    {
        using var f = new Fixture(); var batch = await f.Start(); f.ConfirmationWinsCancellation = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Start());
        Assert.Single(f.Intents); Assert.Empty(f.Canceled);
        Assert.False((await f.Service.GetUpcomingAsync(1)).CanPayAll);
    }

    [Fact]
    public async Task CombinedRetryCancelsAnAbandonedIndividualIntentToo()
    {
        using var f = new Fixture(); f.Intents.Add("pi_individual", new PaymentIntent { Id = "pi_individual", Status = "requires_action" });
        (await f.Db.Orders.FindAsync(1))!.PaymentIntentId = "pi_individual"; await f.Db.SaveChangesAsync();
        await f.Start(); Assert.Contains("pi_individual", f.Canceled);
    }

    [Theory]
    [InlineData("requires_payment_method")]
    [InlineData("requires_action")]
    [InlineData("canceled")]
    public async Task SkipAfterAbandonedPayAllCancelsOldSecretAndPreservesLaterCleanings(string state)
    {
        using var f = new Fixture(); var order = await f.GeneratedOrder(); var batch = await f.Start();
        f.Intents[batch.PaymentIntentId].Status = state;
        await f.SeriesService().SkipAsync(1, order.Id, 1);
        Assert.Equal(OrderStatuses.Cancelled, order.Status);
        Assert.Equal(order.ServiceDate, order.RecurrenceOccurrenceDate);
        Assert.Equal("canceled", f.Intents[batch.PaymentIntentId].Status);
        Assert.Equal(3, await f.Db.OrderPaymentBatchItems.CountAsync()); // skip preserves history
        Assert.All(await f.Db.Orders.Where(o => o.Id != order.Id).ToListAsync(), o => Assert.Equal(OrderStatuses.Pending, o.Status));
        Assert.Equal(2, (await f.Service.GetUpcomingAsync(1)).PayAllCount);
    }

    [Fact]
    public async Task SkipCancelsAbandonedIndividualPaymentBeforeRetiringOrder()
    {
        using var f = new Fixture(); var order = await f.GeneratedOrder();
        order.PaymentIntentId = "pi_single";
        f.Intents.Add(order.PaymentIntentId, new PaymentIntent { Id = order.PaymentIntentId, Status = "requires_payment_method" });
        await f.Db.SaveChangesAsync();
        await f.SeriesService().SkipAsync(1, order.Id, 1);
        Assert.Equal(OrderStatuses.Cancelled, order.Status); Assert.Contains("pi_single", f.Canceled);
    }

    [Theory]
    [InlineData("processing")]
    [InlineData("requires_capture")]
    [InlineData("succeeded")]
    public async Task SkipAndDeleteRejectRealSubmittedPaymentsWithoutRemovingHistory(string state)
    {
        using var f = new Fixture(); var order = await f.GeneratedOrder(); var batch = await f.Start();
        f.Intents[batch.PaymentIntentId].Status = state;
        await Assert.ThrowsAsync<RecurringSeriesException>(() => f.SeriesService().SkipAsync(1, order.Id, 1));
        await Assert.ThrowsAsync<RecurringSeriesException>(() => f.SeriesService().PrepareDeleteAsync(order));
        Assert.NotEqual(OrderStatuses.Cancelled, order.Status);
        Assert.Equal(3, await f.Db.OrderPaymentBatchItems.CountAsync()); Assert.Empty(f.Canceled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteEndpointReleasesOnlyRemovedOrdersRestrictedLinksAndKeepsBatchHistory(bool skipFirst)
    {
        using var f = new Fixture(); var order = await f.GeneratedOrder(); var batch = await f.Start();
        var series = f.SeriesService();
        if (skipFirst) await series.SkipAsync(1, order.Id, 1);
        using var provider = new ServiceCollection().AddSingleton<IRecurringOrderSeriesService>(series).BuildServiceProvider();
        var constructor = typeof(AdminOrdersController).GetConstructors().Single();
        var args = constructor.GetParameters().Select(p => p.Name switch {
            "context" => (object)f.Db,
            "auditService" => new RecordingAuditService(),
            "configuration" => new ConfigurationBuilder().Build(),
            "logger" => NullLogger<AdminOrdersController>.Instance,
            _ => null // The delete endpoint never uses these unrelated services.
        }).ToArray();
        var controller = (AdminOrdersController)constructor.Invoke(args);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext {
            RequestServices = provider, User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("Role", "SuperAdmin") }, "test"))
        } };
        Assert.IsType<OkObjectResult>(await controller.DeleteOrder(order.Id));
        Assert.Null(await f.Db.Orders.FindAsync(order.Id));
        Assert.Equal(2, await f.Db.OrderPaymentBatchItems.CountAsync());
        Assert.Equal(OrderPaymentBatchStatus.Canceled, (await f.Db.OrderPaymentBatches.FindAsync(batch.BatchId))!.Status);
        Assert.Contains(batch.PaymentIntentId, f.Canceled);
    }

    [Fact]
    public async Task ConfirmationRacingSkipCannotRetireOrDeleteTheCleaning()
    {
        using var f = new Fixture(); var order = await f.GeneratedOrder(); await f.Start();
        f.ConfirmationWinsCancellation = true;
        await Assert.ThrowsAsync<RecurringSeriesException>(() => f.SeriesService().SkipAsync(1, order.Id, 1));
        Assert.Equal(OrderStatuses.Pending, order.Status); Assert.Equal(3, await f.Db.OrderPaymentBatchItems.CountAsync());
        Assert.Empty(f.Canceled);
    }
}
