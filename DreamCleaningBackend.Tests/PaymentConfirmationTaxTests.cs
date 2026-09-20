using System.Security.Claims;
using DreamCleaningBackend.Controllers;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DreamCleaningBackend.Tests;

public class PaymentConfirmationTaxTests
{
    [Theory]
    [InlineData(350, 50, false, true, 0, false)] // $300 cleaning + $50 tip: used to become $350.01
    [InlineData(350, 50, false, true, 91.85, false)] // post-discount tax base
    [InlineData(350, 50, false, false, 91.85, false)] // ordinary admin Total edit
    [InlineData(300, 0, true, false, 0, false)] // custom order without a booking session
    [InlineData(350, 50, true, true, 0, false)]
    [InlineData(108.88, 0, false, false, 0, false)] // ordinary rate-priced booking
    [InlineData(350, 50, false, true, 91.85, true)] // post-tax credits must still be deducted
    public async Task ConfirmPaymentPreservesSavedTaxAndExactChargedTotal(
        decimal total, decimal tips, bool custom, bool recurring, decimal discounts, bool applyCredits)
    {
        using var db = RecurringDiscountRegressionTests.Db();
        var split = OrderPricingCalculator.SplitTaxInclusiveAmount(total - tips);
        var user = new User { Id = 1, FirstName = "Test", LastName = "Customer", FirstTimeOrder = false,
            BubbleCredits = applyCredits ? 25 : 0 };
        db.Users.Add(user);
        db.ServiceTypes.Add(new ServiceType { Id = 1, Name = "Cleaning", IsCustom = custom });
        var order = new Order { Id = 1, UserId = 1, ServiceTypeId = 1,
            ServiceDate = NyTimeHelper.NowNy.Date.AddDays(7), ServiceTime = TimeSpan.FromHours(9),
            Status = OrderStatuses.Pending, PaymentMethod = PaymentMethod.Normal,
            RecurringSeriesId = recurring ? 1 : null, IsGeneratedByRecurringSeries = recurring,
            SubTotal = split.subTotal + discounts, Tax = split.tax, Tips = tips,
            DiscountAmount = discounts / 2, SubscriptionDiscountAmount = discounts / 2,
            GiftCardAmountUsed = applyCredits ? 5 : 0, PointsRedeemedDiscount = applyCredits ? 10 : 0,
            Total = total - (applyCredits ? 15 : 0), ContactFirstName = "Test", ContactLastName = "Customer" };
        db.Orders.Add(order); await db.SaveChangesAsync();
        var expected = total - (applyCredits ? 40 : 0);
        var stripe = RecurringDiscountRegressionTests.Stub<IStripeService>((method, args) => {
            Assert.Equal("GetPaymentIntentAsync", method.Name); Assert.Equal("pi_test_paid", args![0]);
            return Task.FromResult(new Stripe.PaymentIntent { Id = "pi_test_paid", Status = "succeeded",
                Amount = (long)(expected * 100), AmountReceived = (long)(expected * 100) });
        });
        var sessions = new BookingDataService(NullLogger<BookingDataService>.Instance);
        if (applyCredits) sessions.StoreBookingData("booking_1_1", new CreateBookingDto { UseCredits = true, CreditsToApply = 25 });
        // The company notification is asynchronous; all notification methods are inert doubles.
        var email = RecurringDiscountRegressionTests.Stub<IEmailService>((_, _) => Task.CompletedTask);
        var constructor = typeof(BookingController).GetConstructors().Single();
        // Built first so the controller's IServiceScopeFactory (detached notifications — see
        // Helpers/BackgroundWork) can be taken from it.
        using var provider = new ServiceCollection().BuildServiceProvider();
        var args = constructor.GetParameters().Select(p => p.Name switch {
            "context" => (object)db,
            "configuration" => new ConfigurationBuilder().Build(),
            "bookingDataService" => sessions,
            "stripeService" => stripe,
            "emailService" => email,
            "logger" => NullLogger<BookingController>.Instance,
            "scopeFactory" => provider.GetRequiredService<IServiceScopeFactory>(),
            _ => null // Unused dependencies on this existing-order confirmation path.
        }).ToArray();
        var controller = (BookingController)constructor.Invoke(args);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext {
            RequestServices = provider, User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("UserId", "1") }, "test"))
        } };

        Assert.IsType<OkObjectResult>(await controller.ConfirmPayment(1, new ConfirmPaymentDto { PaymentIntentId = "pi_test_paid" }));
        var saved = await db.Orders.AsNoTracking().SingleAsync();
        Assert.True(saved.IsPaid); Assert.Equal(OrderStatuses.Active, saved.Status);
        Assert.Equal(expected, saved.Total); Assert.Equal(split.tax, saved.Tax);
        Assert.Equal(split.subTotal + discounts, saved.SubTotal); Assert.Equal(tips, saved.Tips);
        Assert.Equal(applyCredits ? 25m : 0m, saved.RewardBalanceUsed);
        // A replayed confirm of the SAME, already-recorded payment (network retry, second tab) is
        // answered as the success it is — never "Order is already paid" as an error, which a customer
        // who has just paid reads as a decline and answers by paying again (2026-09 billing change).
        // What must NOT happen is unchanged: the order is not re-priced or re-settled.
        var replay = Assert.IsType<OkObjectResult>(await controller.ConfirmPayment(1, new ConfirmPaymentDto { PaymentIntentId = "pi_test_paid" }));
        Assert.Equal(true, replay.Value!.GetType().GetProperty("alreadyPaid")!.GetValue(replay.Value));
        Assert.Equal(expected, (await db.Orders.AsNoTracking().SingleAsync()).Total);
    }
}
