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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DreamCleaningBackend.Tests;

/// <summary>
/// ONE BOOKING, ONE CHARGE, ONE ORDER.
///
/// On 2026-08-30 a customer ended up with orders 317 and 318: the same cleaning, booked once,
/// paid for twice. Two PaymentIntents were created seven seconds apart and both were charged.
/// Nothing in the payment path objected, because nothing in it was idempotent:
///
///   * prepare-payment minted a fresh session id from DateTime.UtcNow.Ticks on EVERY call and
///     created a fresh PaymentIntent to go with it;
///   * CreatePaymentIntentAsync passed no Stripe idempotency key (the refund and off-session
///     paths in the same file already did);
///   * confirm-payment created the order without ever asking whether that intent had already
///     produced one;
///   * and Order.PaymentIntentId carried no unique index, unlike OrderPaymentBatch's.
///
/// These tests exercise the REAL BookingController against a real ApplicationDbContext, the way
/// PaymentConfirmationTaxTests does — the guards live in the endpoint bodies, so asserting them
/// against a hand-rolled double would prove nothing about production.
///
/// Note on the unique index: the in-memory provider does not enforce it, so the concurrent case
/// below SIMULATES the rejection (the competing order is inserted, then the creation call throws
/// the way a unique-index violation would). What is under test there is the catch block's
/// behaviour, not MariaDB's.
/// </summary>
public class DuplicateBookingGuardTests
{
    private const int UserId = 1;
    private const int ServiceTypeId = 1;

    // ── fixture ──────────────────────────────────────────────────────────────────────────────

    private static void Seed(ApplicationDbContext db)
    {
        db.Users.Add(new User
        {
            Id = UserId,
            FirstName = "Test",
            LastName = "Customer",
            Email = "test.customer@example.invalid",
            IsActive = true,
            FirstTimeOrder = false
        });
        db.ServiceTypes.Add(new ServiceType
        {
            Id = ServiceTypeId,
            Name = "Residential Cleaning",
            BasePrice = 200m,
            TimeDuration = 120
        });
        db.SaveChanges();
    }

    /// <summary>The same booking, submitted twice — two DTO instances that must fingerprint equal.</summary>
    private static CreateBookingDto NewBookingDto(decimal tips = 0m) => new()
    {
        ServiceTypeId = ServiceTypeId,
        ServiceDate = new DateTime(2026, 9, 20),
        ServiceTime = "10:00",
        Tips = tips,
        ContactFirstName = "Test",
        ContactLastName = "Customer",
        ContactEmail = "test.customer@example.invalid",
        ContactPhone = "2125550123",
        ServiceAddress = "1 Test Street",
        City = "Brooklyn",
        State = "New York",
        ZipCode = "11201"
    };

    /// <summary>Discounts are somebody else's tests; here they are all zero so the total — which
    /// is part of the fingerprint — depends only on what the DTO says.</summary>
    private static IBookingCreationService NoDiscounts(Action? onCreate = null) =>
        RecurringDiscountRegressionTests.Stub<IBookingCreationService>((method, _) => method.Name switch
        {
            nameof(IBookingCreationService.ResolveGiftCardAndPromo)
                => (object)((string?)null, (string?)null, 0m),
            nameof(IBookingCreationService.ResolveDiscountsAsync)
                => (object)Task.FromResult((0m, 0m)),
            nameof(IBookingCreationService.CreateOrderAsync) => throw Created(onCreate),
            _ => throw new InvalidOperationException($"Unexpected call: {method.Name}")
        });

    private static Exception Created(Action? onCreate)
    {
        onCreate?.Invoke();
        // Stands in for the unique-index violation on Order.PaymentIntentId.
        return new InvalidOperationException("Failed to create order: duplicate PaymentIntentId");
    }

    private static ILoyaltyDiscountService NoLoyalty() =>
        RecurringDiscountRegressionTests.Stub<ILoyaltyDiscountService>((method, args) => method.Name switch
        {
            nameof(ILoyaltyDiscountService.CalculateForOrderAsync)
                => (object)Task.FromResult((0m, 0m)),
            // Nothing to stack: hand the subscription and promo amounts straight back.
            nameof(ILoyaltyDiscountService.ResolveStacking)
                => (object)(0m, 0m, (decimal)args![2]!, (decimal)args[3]!),
            _ => throw new InvalidOperationException($"Unexpected call: {method.Name}")
        });

    private static BookingController NewController(
        ApplicationDbContext db,
        IBookingDataService sessions,
        IStripeService stripe,
        IBookingCreationService? booking = null,
        ILoyaltyDiscountService? loyalty = null)
    {
        var constructor = typeof(BookingController).GetConstructors().Single();
        var args = constructor.GetParameters().Select(p => p.Name switch
        {
            "context" => (object?)db,
            "configuration" => new ConfigurationBuilder().Build(),
            "bookingDataService" => sessions,
            "stripeService" => stripe,
            "bookingCreationService" => booking ?? NoDiscounts(),
            "loyaltyDiscountService" => loyalty ?? NoLoyalty(),
            "logger" => NullLogger<BookingController>.Instance,
            _ => null // Untouched on the two paths under test.
        }).ToArray();

        var provider = new ServiceCollection().BuildServiceProvider();
        var controller = (BookingController)constructor.Invoke(args);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                RequestServices = provider,
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim("UserId", UserId.ToString()) }, "test"))
            }
        };
        return controller;
    }

    private static BookingResponseDto Prepared(ActionResult<BookingResponseDto> result) =>
        (BookingResponseDto)((OkObjectResult)result.Result!).Value!;

    private static (int orderId, string status) Confirmed(ActionResult result)
    {
        var body = ((OkObjectResult)result).Value!;
        var type = body.GetType();
        return ((int)type.GetProperty("orderId")!.GetValue(body)!,
                (string)type.GetProperty("status")!.GetValue(body)!);
    }

    // ── 1. prepare-payment reuses the outstanding attempt ────────────────────────────────────

    /// <summary>
    /// The seven-second gap from the incident. Two prepare-payment calls for one booking must
    /// come back with ONE PaymentIntent — previously this produced two, and the customer could
    /// then be charged on both.
    /// </summary>
    [Fact]
    public async Task PreparePayment_CalledTwiceForTheSameBooking_ReusesTheSessionAndTheIntent()
    {
        using var db = RecurringDiscountRegressionTests.Db();
        Seed(db);

        var intentsCreated = new List<string>();
        var idempotencyKeys = new List<string?>();
        var stripe = StripeCreatingIntents(intentsCreated, idempotencyKeys);
        var sessions = new BookingDataService(NullLogger<BookingDataService>.Instance);
        var controller = NewController(db, sessions, stripe);

        var first = Prepared(await controller.PreparePayment(NewBookingDto()));
        var second = Prepared(await controller.PreparePayment(NewBookingDto()));

        // The whole point: Stripe was asked for an intent exactly once.
        Assert.Single(intentsCreated);
        Assert.Equal(first.PaymentIntentId, second.PaymentIntentId);
        Assert.Equal(first.SessionId, second.SessionId);
        Assert.Equal(first.PaymentClientSecret, second.PaymentClientSecret);
        Assert.Equal(first.Total, second.Total);

        // The session id doubles as the Stripe idempotency key, so even a retry that got past
        // the reuse check could not mint a second chargeable intent.
        Assert.Equal(first.SessionId, Assert.Single(idempotencyKeys));
    }

    /// <summary>
    /// The fingerprint includes the RECOMPUTED TOTAL, not just the booking content. A second
    /// attempt that now prices differently (a gift card balance moved, a promo expired, the tip
    /// changed) must NOT be handed an intent for the old amount.
    /// </summary>
    [Fact]
    public async Task PreparePayment_WhenTheTotalChanges_DoesNotReuseTheIntentForTheOldAmount()
    {
        using var db = RecurringDiscountRegressionTests.Db();
        Seed(db);

        var intentsCreated = new List<string>();
        var stripe = StripeCreatingIntents(intentsCreated, new List<string?>());
        var sessions = new BookingDataService(NullLogger<BookingDataService>.Instance);
        var controller = NewController(db, sessions, stripe);

        var first = Prepared(await controller.PreparePayment(NewBookingDto()));
        var second = Prepared(await controller.PreparePayment(NewBookingDto(tips: 40m)));

        Assert.Equal(2, intentsCreated.Count);
        Assert.NotEqual(first.PaymentIntentId, second.PaymentIntentId);
        Assert.NotEqual(first.SessionId, second.SessionId);
        Assert.NotEqual(first.Total, second.Total);
    }

    /// <summary>
    /// Reuse is scoped to a LIVE attempt, never to the booking content. Once the session is
    /// consumed at confirm-payment, booking the identical cleaning again is a real second
    /// booking and must be charged — otherwise a content-keyed idempotency key would hand the
    /// customer the already-succeeded intent and a free cleaning.
    /// </summary>
    [Fact]
    public async Task PreparePayment_AfterTheSessionIsConsumed_ChargesTheNextBookingProperly()
    {
        using var db = RecurringDiscountRegressionTests.Db();
        Seed(db);

        var intentsCreated = new List<string>();
        var stripe = StripeCreatingIntents(intentsCreated, new List<string?>());
        var sessions = new BookingDataService(NullLogger<BookingDataService>.Instance);
        var controller = NewController(db, sessions, stripe);

        var first = Prepared(await controller.PreparePayment(NewBookingDto()));

        // What confirm-payment does once the order exists.
        sessions.RemoveBookingData(first.SessionId);

        var second = Prepared(await controller.PreparePayment(NewBookingDto()));

        Assert.Equal(2, intentsCreated.Count);
        Assert.NotEqual(first.PaymentIntentId, second.PaymentIntentId);
    }

    private static IStripeService StripeCreatingIntents(List<string> created, List<string?> idempotencyKeys) =>
        RecurringDiscountRegressionTests.Stub<IStripeService>((method, args) =>
        {
            Assert.Equal(nameof(IStripeService.CreatePaymentIntentAsync), method.Name);
            var id = $"pi_test_{created.Count + 1}";
            created.Add(id);
            idempotencyKeys.Add(args![5] as string);
            return Task.FromResult(new Stripe.PaymentIntent
            {
                Id = id,
                ClientSecret = $"{id}_secret",
                Status = "requires_payment_method",
                Amount = (long)((decimal)args[0]! * 100)
            });
        });

    // ── 2. confirm-payment refuses to build a second order for one intent ────────────────────

    /// <summary>
    /// A replayed confirm for an intent that already produced an order returns THAT order. It
    /// must not build a second one, and — just as important — it must not fall through to the
    /// post-creation tail, which would re-mark the order paid and consume the customer's loyalty
    /// discount, bubble points and reward credits all over again.
    /// </summary>
    [Fact]
    public async Task ConfirmPayment_ForAnIntentThatAlreadyHasAnOrder_ReturnsItWithoutCreatingAnother()
    {
        using var db = RecurringDiscountRegressionTests.Db();
        Seed(db);

        const string intentId = "pi_test_already_fulfilled";
        db.Orders.Add(new Order
        {
            Id = 317,
            UserId = UserId,
            ServiceTypeId = ServiceTypeId,
            ServiceDate = new DateTime(2026, 9, 20),
            ServiceTime = TimeSpan.FromHours(10),
            Status = OrderStatuses.Active,
            PaymentMethod = PaymentMethod.Normal,
            PaymentIntentId = intentId,
            IsPaid = true,
            SubTotal = 200m,
            Total = 217.75m
        });
        await db.SaveChangesAsync();

        var createCalls = 0;
        var booking = NoDiscounts(onCreate: () => createCalls++);

        var sessions = new BookingDataService(NullLogger<BookingDataService>.Instance);
        const string sessionId = "prepare_payment_1_638000000000000000";
        sessions.StoreBookingData(sessionId, NewBookingDto());

        var controller = NewController(db, sessions, SucceededIntent(intentId), booking);

        var result = await controller.ConfirmPayment(
            0, new ConfirmPaymentDto { PaymentIntentId = intentId, SessionId = sessionId });

        var (orderId, status) = Confirmed(Assert.IsType<OkObjectResult>(result));
        Assert.Equal(317, orderId);
        Assert.Equal(OrderStatuses.Active, status);

        // Order creation was never reached — this is the PRE-insert guard, not the catch block.
        Assert.Equal(0, createCalls);
        Assert.Equal(1, await db.Orders.CountAsync());
    }

    // ── 3. a lost race must not refund the winner's charge ───────────────────────────────────

    /// <summary>
    /// The dangerous interaction between the two halves of this fix. With the unique index in
    /// place, a concurrent duplicate confirm fails at the INSERT — and the charge it is holding
    /// is the very one that paid for the order the other request just created. The catch block
    /// used to refund unconditionally whenever the order had not persisted, which would have
    /// handed that customer a completed cleaning and their money back.
    /// </summary>
    [Fact]
    public async Task ConfirmPayment_WhenTheInsertLosesTheRace_ReturnsTheWinnersOrderAndDoesNotRefund()
    {
        using var db = RecurringDiscountRegressionTests.Db();
        Seed(db);

        const string intentId = "pi_test_raced";
        var refunds = new List<string>();

        // The competing request commits its order between our lookup and our insert, then the
        // unique index rejects ours. Simulated, because the in-memory provider has no index.
        var booking = NoDiscounts(onCreate: () =>
        {
            db.Orders.Add(new Order
            {
                Id = 318,
                UserId = UserId,
                ServiceTypeId = ServiceTypeId,
                ServiceDate = new DateTime(2026, 9, 20),
                ServiceTime = TimeSpan.FromHours(10),
                Status = OrderStatuses.Active,
                PaymentMethod = PaymentMethod.Normal,
                PaymentIntentId = intentId,
                IsPaid = true,
                SubTotal = 200m,
                Total = 217.75m
            });
            db.SaveChanges();
        });

        var stripe = RecurringDiscountRegressionTests.Stub<IStripeService>((method, args) =>
        {
            if (method.Name == nameof(IStripeService.CreateRefundAsync))
            {
                refunds.Add((string)args![0]!);
                return Task.FromResult(new Stripe.Refund { Id = "re_test" });
            }
            Assert.Equal(nameof(IStripeService.GetPaymentIntentAsync), method.Name);
            return Task.FromResult(new Stripe.PaymentIntent { Id = intentId, Status = "succeeded" });
        });

        var sessions = new BookingDataService(NullLogger<BookingDataService>.Instance);
        const string sessionId = "prepare_payment_1_638000000000000001";
        sessions.StoreBookingData(sessionId, NewBookingDto());

        var controller = NewController(db, sessions, stripe, booking);

        var result = await controller.ConfirmPayment(
            0, new ConfirmPaymentDto { PaymentIntentId = intentId, SessionId = sessionId });

        // The customer keeps the booking the winner created, and keeps having paid for it.
        var (orderId, _) = Confirmed(Assert.IsType<OkObjectResult>(result));
        Assert.Equal(318, orderId);
        Assert.Empty(refunds);
        Assert.Equal(1, await db.Orders.CountAsync());
    }

    private static IStripeService SucceededIntent(string intentId) =>
        RecurringDiscountRegressionTests.Stub<IStripeService>((method, _) =>
        {
            Assert.Equal(nameof(IStripeService.GetPaymentIntentAsync), method.Name);
            return Task.FromResult(new Stripe.PaymentIntent { Id = intentId, Status = "succeeded" });
        });
}
