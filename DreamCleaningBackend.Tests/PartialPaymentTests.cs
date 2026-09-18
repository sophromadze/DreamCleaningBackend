using System.Reflection;
using DreamCleaningBackend.Controllers;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DreamCleaningBackend.Tests;

/// <summary>
/// Part-payments (2026-09): an admin splits an unpaid order's total across several links
/// ("$1,000 now, the rest before the cleaning"), and the order stays Pending and unpaid,
/// carrying a visible balance, until the last slice lands.
///
/// What these assert, and why each one is here rather than being obvious:
///
///  • <b>The balance rule.</b> Before this feature an order was paid or it was not, and the
///    amount to charge was always <c>Order.Total</c>. Every surface that reads a balance now has
///    to agree with every other one, so the rule lives in <see cref="OrderBalance"/> and is
///    asserted directly.
///  • <b>An ordinary order is untouched.</b> <c>AmountPaid</c> is zero on every order the normal
///    full-payment flow settles, so the naive <c>Total − AmountPaid</c> would report every paid
///    order in the database as owing its whole total.
///  • <b>The Stripe discriminator is not "booking".</b> The webhook's booking handler marks the
///    WHOLE order paid from that value alone — a $1,000 deposit arriving under it would settle a
///    $2,743.65 order.
///
/// NOTE ON COVERAGE: <c>OrderPartialPaymentService.SettleAsync</c> claims its row with
/// <c>ExecuteUpdateAsync</c>, which the in-memory provider cannot run — that atomic claim is what
/// stops the browser's confirm and the Stripe webhook both crediting one charge. The rules it
/// composes (<see cref="OrderBalance.SettlesOrder"/>, <see cref="OrderBalance.AmountDue"/>) are
/// asserted here; the claim itself needs a relational database and is exercised by running a real
/// part-payment against the dev MySQL instance.
/// </summary>
public class PartialPaymentTests
{
    private static ApplicationDbContext Db() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString(), b => b.EnableNullChecks(false))
        .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);

    private static Order Order(decimal total = 2743.65m, decimal amountPaid = 0m,
        bool isPaid = false, string status = OrderStatuses.Pending,
        PaymentMethod method = PaymentMethod.Normal) => new()
    {
        Id = 1,
        UserId = 1,
        ServiceTypeId = 1,
        ServiceDate = DateTime.UtcNow.Date.AddDays(7),
        ServiceTime = TimeSpan.FromHours(10),
        Status = status,
        PaymentMethod = method,
        IsPaid = isPaid,
        Total = total,
        AmountPaid = amountPaid,
        SubTotal = 2519.93m,
        Tax = 223.72m,
        ServiceAddress = "1579 Flatbush Ave.",
        ContactEmail = "test@example.invalid",
        ContactFirstName = "Test",
        ContactLastName = "Client",
        ContactPhone = "2125550100"
    };

    private static async Task<(ApplicationDbContext Db, OrderPartialPaymentService Service, RecordingAuditService Audit)>
        ServiceWith(Order order)
    {
        var db = Db();
        db.Users.Add(new User { Id = 1, FirstName = "Test", LastName = "Client" });
        db.Users.Add(new User { Id = 5, FirstName = "Ana", LastName = "Admin" });
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        var audit = new RecordingAuditService();
        return (db, new OrderPartialPaymentService(db, audit,
            NullLogger<OrderPartialPaymentService>.Instance), audit);
    }

    // ── The balance rule ──────────────────────────────────────────────────────────────────

    [Fact]
    public void AnOrdinaryOrderOwesItsWholeTotalAndNothingMore()
    {
        var unpaid = Order();
        Assert.Equal(2743.65m, OrderBalance.AmountDue(unpaid));
        Assert.False(OrderBalance.IsPartiallyPaid(unpaid));
        Assert.Equal(0m, OrderBalance.OverpaidAmount(unpaid));
    }

    [Fact]
    public void APaidOrderOwesNothing_EvenThoughAmountPaidIsZeroOnIt()
    {
        // The regression this exists for: the normal full-payment flow settles an order by setting
        // IsPaid and never touches AmountPaid, so a bare subtraction reports every paid order in
        // the database as still owing its entire total.
        var paid = Order(isPaid: true);
        Assert.Equal(0m, paid.AmountPaid);
        Assert.Equal(0m, OrderBalance.AmountDue(paid));
        Assert.False(OrderBalance.IsPartiallyPaid(paid));
    }

    [Fact]
    public void ADepositLeavesTheRestOwing()
    {
        var order = Order(amountPaid: 1000m);
        Assert.Equal(1743.65m, OrderBalance.AmountDue(order));
        Assert.True(OrderBalance.IsPartiallyPaid(order));
    }

    [Fact]
    public void TheFinalSlicePaidIsNotStillPartiallyPaid()
    {
        // Money has arrived AND the balance is clear — the pill must go off, or an order about to
        // be marked paid reads as half-paid forever.
        var order = Order(amountPaid: 2743.65m);
        Assert.Equal(0m, OrderBalance.AmountDue(order));
        Assert.False(OrderBalance.IsPartiallyPaid(order));
        Assert.True(OrderBalance.SettlesOrder(OrderBalance.AmountDue(order)));
    }

    [Fact]
    public void ARemainderStripeCannotChargeSettlesTheOrder()
    {
        // 30¢ can never be collected online. Leaving the order unpaid over it would strand it.
        Assert.True(OrderBalance.SettlesOrder(0.30m));
        Assert.False(OrderBalance.SettlesOrder(0.50m));
        Assert.False(OrderBalance.SettlesOrder(1743.65m));
    }

    [Fact]
    public void MoneyBeyondTheTotalIsReportedRatherThanHidden()
    {
        // Only reachable when an admin lowers the price after a deposit. Never auto-refunded:
        // the price moving is a decision a person just made, and the refund is theirs too.
        var order = Order(total: 800m, amountPaid: 1000m);
        Assert.Equal(200m, OrderBalance.OverpaidAmount(order));
        Assert.Equal(0m, OrderBalance.AmountDue(order));
    }

    // ── Which orders may be split at all ──────────────────────────────────────────────────

    [Fact]
    public void AnUnpaidStripeOrderCanBeAskedForADeposit()
    {
        Assert.True(OrderBalance.CanRequestPartialPayment(Order(), out var refusal));
        Assert.Null(refusal);
    }

    [Theory]
    [InlineData(true, OrderStatuses.Pending, PaymentMethod.Normal)]           // already settled
    [InlineData(false, OrderStatuses.Cancelled, PaymentMethod.Normal)]        // dead order
    [InlineData(false, OrderStatuses.Refunded, PaymentMethod.Normal)]         // dead order
    [InlineData(false, OrderStatuses.Pending, PaymentMethod.Cash)]            // settled off-site
    [InlineData(false, OrderStatuses.Pending, PaymentMethod.Invoice)]         // the ledger decides
    public void OrdersWithNoOnlineBalanceAreRefusedWithAReason(
        bool isPaid, string status, PaymentMethod method)
    {
        var order = Order(isPaid: isPaid, status: status, method: method);
        Assert.False(OrderBalance.CanRequestPartialPayment(order, out var refusal));
        Assert.False(string.IsNullOrWhiteSpace(refusal));
    }

    [Fact]
    public void ARecurringOccurrenceIsRefused_BecauseItsPaymentsAreAlreadySequenced()
    {
        // RecurringPaymentPolicy decides what the customer may pay next. A second rule deciding
        // the same thing would contradict it.
        var order = Order();
        order.RecurringSeriesId = 4;
        Assert.False(OrderBalance.CanRequestPartialPayment(order, out var refusal));
        Assert.Contains("Recurring", refusal!);
    }

    // ── Asking for an amount ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ARequestRecordsWhatWasAgreedAndLeavesTheOrderUnpaid()
    {
        var (db, service, audit) = await ServiceWith(Order());

        var row = await service.CreateRequestAsync(1, 1000m, "deposit agreed on the phone", adminUserId: 5);

        Assert.Equal(1000m, row.RequestedAmount);
        Assert.Equal(OrderPartialPaymentStatus.Pending, row.Status);
        Assert.Null(row.PaidAmount);
        Assert.Null(row.NotificationSentAt);          // nothing sent yet — the panel says so
        Assert.Equal(5, row.RequestedByUserId);

        // Asking for money moves none: the order is exactly as unpaid as it was.
        var order = await db.Orders.FirstAsync();
        Assert.False(order.IsPaid);
        Assert.Equal(0m, order.AmountPaid);
        Assert.Equal(OrderStatuses.Pending, order.Status);

        Assert.Contains(audit.Actions, a => a.Action == "PartialPaymentRequested");
    }

    [Fact]
    public async Task OnlyOneRequestMayBeLiveAtATime()
    {
        // Two live requests would let the same money be charged twice from two open tabs, and
        // nothing would tell the payment page which to charge first.
        var (_, service, _) = await ServiceWith(Order());
        await service.CreateRequestAsync(1, 1000m, null, adminUserId: 5);

        var ex = await Assert.ThrowsAsync<PartialPaymentException>(
            () => service.CreateRequestAsync(1, 500m, null, adminUserId: 5));
        Assert.Contains("already a payment request", ex.Message);
    }

    [Fact]
    public async Task MoreThanIsOwedIsRefused()
    {
        var (_, service, _) = await ServiceWith(Order(amountPaid: 1000m));

        var ex = await Assert.ThrowsAsync<PartialPaymentException>(
            () => service.CreateRequestAsync(1, 2000m, null, adminUserId: 5));
        Assert.Contains("1743.65", ex.Message);
    }

    [Fact]
    public async Task ASliceLeavingAnUncollectableStubIsRefused()
    {
        // Rounding the admin's figure up would charge something nobody discussed; leaving 30¢
        // owing would strand the order. So it is refused, naming the amount that does work.
        var (_, service, _) = await ServiceWith(Order(total: 100m));

        var ex = await Assert.ThrowsAsync<PartialPaymentException>(
            () => service.CreateRequestAsync(1, 99.70m, null, adminUserId: 5));
        Assert.Contains("0.30", ex.Message);
        Assert.Contains("100.00", ex.Message);
    }

    [Fact]
    public async Task BelowStripesMinimumIsRefused()
    {
        var (_, service, _) = await ServiceWith(Order());
        await Assert.ThrowsAsync<PartialPaymentException>(
            () => service.CreateRequestAsync(1, 0.25m, null, adminUserId: 5));
    }

    [Fact]
    public async Task ACancelledRequestStaysInTheHistory()
    {
        // An amount that was asked for and dropped is part of the conversation with the customer.
        var (_, service, audit) = await ServiceWith(Order());
        var row = await service.CreateRequestAsync(1, 1000m, null, adminUserId: 5);

        await service.CancelRequestAsync(1, row.Id, adminUserId: 5);

        var balance = await service.GetBalanceAsync(1);
        Assert.Null(balance.PendingRequest);
        Assert.Single(balance.History);
        Assert.Equal("Cancelled", balance.History[0].Status);
        Assert.Contains(audit.Actions, a => a.Action == "PartialPaymentCancelled");

        // And the order is askable again, which is the point of cancelling.
        Assert.True(balance.CanRequestPartialPayment);
    }

    [Fact]
    public async Task ALiveRequestBlocksTheNextOneAndSaysSo()
    {
        var (_, service, _) = await ServiceWith(Order());
        await service.CreateRequestAsync(1, 1000m, null, adminUserId: 5);

        var balance = await service.GetBalanceAsync(1);
        Assert.NotNull(balance.PendingRequest);
        Assert.Equal(1000m, balance.PendingRequest!.RequestedAmount);
        Assert.False(balance.CanRequestPartialPayment);
        Assert.Contains("Cancel it first", balance.CannotRequestReason!);
    }

    [Fact]
    public async Task TheBalanceReportsTheOrderAsTheCustomerSeesIt()
    {
        var (_, service, _) = await ServiceWith(Order(amountPaid: 1000m));

        var balance = await service.GetBalanceAsync(1);
        Assert.Equal(2743.65m, balance.Total);
        Assert.Equal(1000m, balance.AmountPaid);
        Assert.Equal(1743.65m, balance.AmountDue);
        Assert.True(balance.IsPartiallyPaid);
        Assert.Equal(0m, balance.OverpaidAmount);
    }

    // ── Wiring that would fail silently ───────────────────────────────────────────────────

    [Fact]
    public void ThePartPaymentDiscriminatorIsNotTheBookingOne()
    {
        // The webhook's booking handler marks the WHOLE order paid from the metadata type alone.
        // A deposit arriving under "booking" would settle an order that is nowhere near paid.
        Assert.Equal("order_partial", OrderPartialPaymentService.StripeMetadataType);
        Assert.NotEqual("booking", OrderPartialPaymentService.StripeMetadataType);
        Assert.NotEqual(RecurringCustomerPaymentService.StripeMetadataType,
            OrderPartialPaymentService.StripeMetadataType);
    }

    [Fact]
    public void TheWebhookHandlesPartPaymentsSeparatelyFromBookings()
    {
        var handler = typeof(StripeWebhookController).GetMethod(
            "HandlePartialOrderPayment", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(handler);
    }

    [Fact]
    public void TheCustomerFacingEndpointsExist()
    {
        // A missing route here is a payment link that opens on an error page, which is not
        // something a compiler or a unit test elsewhere would notice.
        var controller = typeof(BookingController);
        Assert.NotNull(controller.GetMethod("CreatePartialPaymentIntent"));
        Assert.NotNull(controller.GetMethod("ConfirmPartialPayment"));
    }

    [Fact]
    public void RefundsCanReachMoneyTakenInSlices()
    {
        // Each slice is its own Stripe charge, and Order.PaymentIntentId holds only the LAST of
        // them. Without the part-payment rows in the refundable set, a $1,000 deposit would be
        // unrefundable through the admin panel.
        var source = File.ReadAllText(SourceFile("Services/OrderRefundService.cs"));
        Assert.Contains("order.PartialPayments", source);
        Assert.Contains("OrderPartialPaymentStatus.Paid", source);
    }

    [Fact]
    public void AutoCancelLeavesPartPaidOrdersAlone()
    {
        // A part-paid order is not an abandoned checkout: money has arrived, and cancelling it
        // silently would leave that money owed back to a customer nobody has been told to refund.
        var source = File.ReadAllText(SourceFile("Services/OrderService.cs"));
        Assert.Contains("OrderBalance.IsPartiallyPaid(order)", source);
    }

    /// <summary>Resolves a path inside the API project from the test assembly's location.</summary>
    private static string SourceFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "DreamCleaningBackend")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "DreamCleaningBackend", relativePath.Replace('/', Path.DirectorySeparatorChar));
    }
}
