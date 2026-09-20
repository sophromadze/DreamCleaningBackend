using System.Security.Claims;
using DreamCleaningBackend.Controllers;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Stripe;
using Xunit;
using PaymentMethod = DreamCleaningBackend.Models.PaymentMethod;

namespace DreamCleaningBackend.Tests;

/// <summary>
/// The order-edit top-up payment (create-pending-update-payment-intent) may only ever charge a
/// price INCREASE on an order that is already PAID (2026-09).
///
/// Found during the billing E2E review: with no Initial* snapshot and no edit history the helper
/// read the "original" price as zero and reported the order's WHOLE total as an unpaid top-up,
/// and the endpoint would have charged it — a second full charge on a legacy paid order. On an
/// unpaid order it would have collected the price difference on top of the booking payment that
/// already charges the current total.
/// </summary>
public class PendingUpdatePaymentGuardTests
{
    private sealed class Fixture
    {
        public readonly DreamCleaningBackend.Data.ApplicationDbContext Db = RecurringDiscountRegressionTests.Db();
        public int IntentsCreated;
        public decimal? LastAmount;
        public readonly OrderController Controller;

        public Fixture()
        {
            var stripe = RecurringDiscountRegressionTests.Stub<IStripeService>((method, args) =>
            {
                if (method.Name == nameof(IStripeService.CreatePaymentIntentAsync))
                {
                    IntentsCreated++;
                    LastAmount = (decimal)args![0]!;
                    return Task.FromResult(new PaymentIntent { Id = "pi_topup", ClientSecret = "secret" });
                }
                return method.ReturnType == typeof(Task) ? Task.CompletedTask : null;
            });

            Controller = new OrderController(
                RecurringDiscountRegressionTests.Stub<IOrderService>(), Db, new RecordingAuditService(), stripe,
                RecurringDiscountRegressionTests.Stub<IEmailService>(), RecurringDiscountRegressionTests.Stub<IAdminBonusService>(),
                RecurringDiscountRegressionTests.Stub<IOrderPaymentStatusReconciler>(), NullLogger<OrderController>.Instance,
                new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>())
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "7") }, "test"))
                    }
                }
            };
        }

        public Order AddOrder(bool isPaid, decimal total, decimal initialTotal)
        {
            var order = new Order
            {
                Id = 500, UserId = 7, ServiceTypeId = 1, Total = total, SubTotal = total, Tax = 0m,
                IsPaid = isPaid, InitialTotal = initialTotal, InitialSubTotal = initialTotal,
                Status = isPaid ? OrderStatuses.Active : OrderStatuses.Pending, PaymentMethod = PaymentMethod.Normal,
                ServiceDate = DateTime.UtcNow.Date.AddDays(5), ServiceTime = TimeSpan.FromHours(10)
            };
            Db.Orders.Add(order);
            Db.SaveChanges();
            return order;
        }

        public void AddEdit(decimal originalTotal, decimal newTotal, bool isPaid)
        {
            Db.OrderUpdateHistories.Add(new OrderUpdateHistory
            {
                OrderId = 500, UpdatedAt = DateTime.UtcNow, OriginalTotal = originalTotal, NewTotal = newTotal,
                AdditionalAmount = newTotal - originalTotal, IsPaid = isPaid
            });
            Db.SaveChanges();
        }
    }

    [Fact]
    public async Task APaidLegacyOrderWithNoSnapshotAndNoEdits_OwesNoTopUp_AndNothingIsCharged()
    {
        var f = new Fixture();
        var order = f.AddOrder(isPaid: true, total: 650m, initialTotal: 0m);   // pre-snapshot legacy order

        Assert.Equal(0m, await OrderAdditionalCharge.OutstandingAsync(f.Db, order));
        var result = await f.Controller.CreatePendingUpdatePaymentIntent(500);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(0, f.IntentsCreated);
    }

    [Fact]
    public async Task AnUnpaidOrder_IsNeverChargedATopUp_EvenWithAnEditOnRecord()
    {
        var f = new Fixture();
        f.AddOrder(isPaid: false, total: 700m, initialTotal: 0m);
        f.AddEdit(originalTotal: 500m, newTotal: 700m, isPaid: false);

        var result = await f.Controller.CreatePendingUpdatePaymentIntent(500);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("not been paid", bad.Value!.ToString());
        Assert.Equal(0, f.IntentsCreated);
    }

    [Fact]
    public async Task APaidOrderWhosePriceRose_IsChargedExactlyTheIncrease()
    {
        var f = new Fixture();
        f.AddOrder(isPaid: true, total: 700m, initialTotal: 500m);
        f.AddEdit(originalTotal: 500m, newTotal: 700m, isPaid: false);

        var result = await f.Controller.CreatePendingUpdatePaymentIntent(500);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(1, f.IntentsCreated);
        Assert.Equal(200m, f.LastAmount);
    }
}
