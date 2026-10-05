using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Models.Commercial;
using DreamCleaningBackend.Services;
using DreamCleaningBackend.Services.Commercial;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DreamCleaningBackend.Tests;

/// <summary>
/// Online bank (Stripe ACH) payment of REGULAR invoices (2026-09) — the settlement half, which is
/// where money is decided. Creating a Checkout Session needs a live Stripe key and is not tested,
/// the same arrangement as the commercial ACH tests.
///
/// <c>OrderPartialPaymentService.SettleAsync</c> uses ExecuteUpdate, which the in-memory provider
/// cannot run, so it is replaced by a recorder: these tests pin down WHAT the ACH service asks it
/// to credit (the bill, never the fee) and HOW OFTEN (once, whatever Stripe redelivers).
/// </summary>
public class CustomerInvoiceAchTests
{
    private static ApplicationDbContext Db() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString(), b => b.EnableNullChecks(false))
        .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);

    private sealed class RecordingPartialPayments : IOrderPartialPaymentService
    {
        public readonly List<(int OrderId, string IntentId, decimal Amount)> Settled = new();

        public Task<PartialPaymentSettlement> SettleAsync(int orderId, string paymentIntentId, decimal amountReceived, CancellationToken ct = default)
        {
            Settled.Add((orderId, paymentIntentId, amountReceived));
            return Task.FromResult(new PartialPaymentSettlement(true, 1, amountReceived, amountReceived, 0m, true));
        }

        public Task<OrderPaymentBalanceDto> GetBalanceAsync(int orderId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OrderPartialPayment?> GetPendingRequestAsync(int orderId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OrderPartialPayment> CreateRequestAsync(int orderId, decimal amount, string? note, int adminUserId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OrderPartialPayment> CreateInvoiceRequestAsync(int orderId, decimal amount, string? note, int adminUserId, bool allowAlongsideOpenRequests, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OrderPartialPayment> CancelRequestAsync(int orderId, int requestId, int adminUserId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task MarkNotificationSentAsync(int requestId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PartialPaymentSettlement> RecordManualPaymentAsync(int orderId, int requestId, PaymentMethod method, string? paymentReference, string? paymentNotes, int adminUserId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task EditManualPaymentDetailsAsync(int orderId, int requestId, PaymentMethod method, string? paymentReference, string? paymentNotes, int adminUserId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> RecordCombinedPaymentSliceAsync(int orderId, decimal amount, decimal expectedTotal, decimal expectedAmountPaid, string paymentIntentId, int batchId, CancellationToken ct = default) => throw new NotSupportedException();
        public OrderPartialPaymentDto ToDto(OrderPartialPayment row) => throw new NotSupportedException();
    }

    private static async Task<(ApplicationDbContext Db, CustomerInvoiceAchService Ach, RecordingPartialPayments Partial, CustomerInvoice Invoice)>
        Setup(CustomerInvoiceKind kind = CustomerInvoiceKind.Full)
    {
        var db = Db();
        db.OrderPartialPayments.Add(new OrderPartialPayment
        {
            Id = 7, OrderId = 1, RequestedAmount = 925.43m, Status = OrderPartialPaymentStatus.Pending,
            PaymentIntentId = "pi_abandoned_card", CreatedAt = DateTime.UtcNow
        });
        var invoice = new CustomerInvoice
        {
            Id = 3, InvoiceNumber = "DCR-2026-00000001", PublicToken = "tok", OrderId = 1,
            OrderPartialPaymentId = kind == CustomerInvoiceKind.Additional ? null : 7,
            Kind = kind, Amount = 925.43m
        };
        db.CustomerInvoices.Add(invoice);
        await db.SaveChangesAsync();

        var partial = new RecordingPartialPayments();
        var ach = new CustomerInvoiceAchService(db, new BillingSettingsService(db), partial,
            new ConfigurationBuilder().Build(), NullLogger<CustomerInvoiceAchService>.Instance);
        return (db, ach, partial, invoice);
    }

    private static CustomerInvoicePaymentAttempt Attempt(CustomerInvoicePaymentAttemptStatus status) => new()
    {
        Id = 11, CustomerInvoiceId = 3, OrderId = 1, OrderPartialPaymentId = 7,
        Amount = 925.43m, ProcessingFee = 5.00m, TotalCharged = 930.43m,
        Status = status, StripeCheckoutSessionId = "cs_1"
    };

    private static Dictionary<string, string> Metadata() => new()
    {
        ["type"] = CustomerInvoiceAchService.StripeMetadataType,
        [CustomerInvoiceAchService.AttemptIdKey] = "11"
    };

    [Fact]
    public async Task ASettledDebitCreditsTheBillNeverTheAchFee()
    {
        var (db, ach, partial, _) = await Setup();
        db.CustomerInvoicePaymentAttempts.Add(Attempt(CustomerInvoicePaymentAttemptStatus.Processing));
        await db.SaveChangesAsync();

        var result = await ach.RecordSucceededAsync("pi_ach", Metadata());

        Assert.NotNull(result);
        Assert.True(result!.Applied);
        var (orderId, intentId, amount) = Assert.Single(partial.Settled);
        Assert.Equal(1, orderId);
        Assert.Equal("pi_ach", intentId);
        Assert.Equal(925.43m, amount); // not the $930.43 debited
        // The request now names the ACH intent, so SettleAsync claims exactly this row.
        Assert.Equal("pi_ach", (await db.OrderPartialPayments.SingleAsync()).PaymentIntentId);
        Assert.Equal(CustomerInvoicePaymentAttemptStatus.Succeeded, (await db.CustomerInvoicePaymentAttempts.SingleAsync()).Status);
    }

    [Fact]
    public async Task ARedeliveredSettlementMovesNothingTwice()
    {
        var (db, ach, partial, _) = await Setup();
        db.CustomerInvoicePaymentAttempts.Add(Attempt(CustomerInvoicePaymentAttemptStatus.Processing));
        await db.SaveChangesAsync();

        await ach.RecordSucceededAsync("pi_ach", Metadata());
        var second = await ach.RecordSucceededAsync("pi_ach", Metadata());

        Assert.False(second!.Applied);
        Assert.Single(partial.Settled);
    }

    [Fact]
    public async Task AFailureArrivingLateNeverUndoesASettledPayment()
    {
        var (db, ach, _, _) = await Setup();
        db.CustomerInvoicePaymentAttempts.Add(Attempt(CustomerInvoicePaymentAttemptStatus.Succeeded));
        await db.SaveChangesAsync();

        await ach.HandleFailedAsync("cs_1", "pi_ach", "x", "late", Metadata());
        await ach.HandleAuthorizedAsync("cs_1", "pi_ach", Metadata());

        Assert.Equal(CustomerInvoicePaymentAttemptStatus.Succeeded, (await db.CustomerInvoicePaymentAttempts.SingleAsync()).Status);
    }

    [Fact]
    public async Task AuthorizationMarksProcessingAndCreditsNothing()
    {
        var (db, ach, partial, _) = await Setup();
        db.CustomerInvoicePaymentAttempts.Add(Attempt(CustomerInvoicePaymentAttemptStatus.CheckoutOpen));
        await db.SaveChangesAsync();

        await ach.HandleAuthorizedAsync("cs_1", "pi_ach", Metadata());

        var attempt = await db.CustomerInvoicePaymentAttempts.SingleAsync();
        Assert.Equal(CustomerInvoicePaymentAttemptStatus.Processing, attempt.Status);
        Assert.Equal("pi_ach", attempt.StripePaymentIntentId);
        Assert.Empty(partial.Settled);
        Assert.True(await ach.IsProcessingForRequestAsync(7));
    }

    [Fact]
    public void BankTransferIsASettledManualMethodLikeZelle()
    {
        // Recording it means the money has arrived — the same answer Cash/Zelle/Check/Other give.
        Assert.True(PaymentMethodRules.IsSettledOnRecord(PaymentMethod.BankTransfer));
        Assert.True(PaymentMethodRules.IsOutsideStripe(PaymentMethod.BankTransfer));
        Assert.Equal(PaymentMethod.BankTransfer, PaymentMethodRules.Parse("BankTransfer"));
        Assert.Equal(6, (int)PaymentMethod.BankTransfer); // appended; the column is an int
    }

    [Fact]
    public async Task RegularInvoicesCarryNoCustomerAchFee()
    {
        // The ACH Processing Fee is a COMMERCIAL-client charge only (2026-09-29). The customer is
        // debited exactly the amount due, even with the commercial fee switched on.
        var (db, _, _, invoice) = await Setup();
        var settings = new BillingSettings { AchCustomerFeeEnabled = true };

        var state = await CustomerInvoiceAchService.ComputeStateAsync(db, invoice, 925.43m, payable: true, settings);
        Assert.True(state.Available);
        Assert.Equal(0m, state.ProcessingFee);
        Assert.Equal(925.43m, state.TotalCharge);
    }

    [Fact]
    public async Task NotOfferedWhileSettlingWhenSwitchedOffOrForAnAdditionalInvoice()
    {
        var (db, _, _, invoice) = await Setup();
        var settings = new BillingSettings();

        settings.StripeAchEnabled = false;
        Assert.False((await CustomerInvoiceAchService.ComputeStateAsync(db, invoice, 100m, true, settings)).Available);
        settings.StripeAchEnabled = true;

        Assert.False((await CustomerInvoiceAchService.ComputeStateAsync(db, invoice, 100m, payable: false, settings)).Available);

        db.CustomerInvoicePaymentAttempts.Add(Attempt(CustomerInvoicePaymentAttemptStatus.Processing));
        await db.SaveChangesAsync();
        var settling = await CustomerInvoiceAchService.ComputeStateAsync(db, invoice, 100m, true, settings);
        Assert.True(settling.Processing);
        Assert.False(settling.Available);

        var (db2, _, _, additional) = await Setup(CustomerInvoiceKind.Additional);
        Assert.False((await CustomerInvoiceAchService.ComputeStateAsync(db2, additional, 100m, true, settings)).Available);
    }
}
