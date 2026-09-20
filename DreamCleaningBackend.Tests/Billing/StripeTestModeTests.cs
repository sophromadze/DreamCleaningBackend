using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models.Billing;
using DreamCleaningBackend.Services;
using DreamCleaningBackend.Services.Billing;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Stripe;
using Xunit;

namespace DreamCleaningBackend.Tests.Billing;

/// <summary>Runs only with a Stripe TEST secret key in DC_STRIPE_TEST_KEY. Refuses anything else.</summary>
public sealed class StripeTestModeFactAttribute : FactAttribute
{
    public StripeTestModeFactAttribute()
    {
        var key = Environment.GetEnvironmentVariable("DC_STRIPE_TEST_KEY");
        if (string.IsNullOrWhiteSpace(key))
            Skip = "Set DC_STRIPE_TEST_KEY to a Stripe sk_test_ key to run the Stripe test-mode scenarios.";
        else if (!key.StartsWith("sk_test_", StringComparison.Ordinal))
            Skip = "DC_STRIPE_TEST_KEY is not a TEST key (sk_test_). Live keys are never used by tests.";
    }
}

/// <summary>Needs both a test-mode Stripe key and a local MariaDB.</summary>
public sealed class StripeAndMariaDbFactAttribute : FactAttribute
{
    public StripeAndMariaDbFactAttribute()
    {
        var key = Environment.GetEnvironmentVariable("DC_STRIPE_TEST_KEY");
        if (string.IsNullOrWhiteSpace(key) || !key.StartsWith("sk_test_", StringComparison.Ordinal))
            Skip = "Set DC_STRIPE_TEST_KEY (sk_test_ only) to run the Stripe test-mode scenarios.";
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DC_TEST_MARIADB")))
            Skip = "Set DC_TEST_MARIADB to run the end-to-end Stripe test-mode scenarios.";
    }
}

/// <summary>
/// REAL Stripe, TEST MODE, with Stripe's documented test payment methods. These verify what the
/// fakes cannot: that the request shape Stripe.net sends for an off-session, card-only charge is
/// accepted by the API version this project pins (the audit's open question), and that Stripe's
/// own answers map to the outcomes the billing code branches on.
///
/// Nothing here can move real money: the key is refused unless it starts with sk_test_.
/// </summary>
[Collection(StripeGlobalStateCollection.Name)]
public class StripeTestModeTests : IClassFixture<MariaDbDatabase>
{
    private readonly MariaDbDatabase _db;

    public StripeTestModeTests(MariaDbDatabase db) => _db = db;

    private static StripeService RealStripe()
    {
        var key = Environment.GetEnvironmentVariable("DC_STRIPE_TEST_KEY")!;
        if (!key.StartsWith("sk_test_", StringComparison.Ordinal)) throw new InvalidOperationException("Test keys only.");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Stripe:SecretKey"] = key }).Build();
        return new StripeService(config, NullLogger<StripeService>.Instance);
    }

    private static async Task<(string Customer, string PaymentMethod)> CustomerWithCard(string testPaymentMethod)
    {
        var customer = await new CustomerService().CreateAsync(new CustomerCreateOptions
        {
            Description = "Dream Cleaning billing test (automated)",
            Metadata = new Dictionary<string, string> { ["purpose"] = "billing-integration-test" }
        });
        var pm = await new Stripe.PaymentMethodService().AttachAsync(testPaymentMethod, new PaymentMethodAttachOptions { Customer = customer.Id });
        return (customer.Id, pm.Id);
    }

    private static SavedCardChargeRequest Request(string customer, string pm, decimal amount, string? key = null) => new()
    {
        Amount = amount,
        CustomerId = customer,
        PaymentMethodId = pm,
        IdempotencyKey = key ?? "dc-test-" + Guid.NewGuid().ToString("N"),
        Metadata = new Dictionary<string, string> { ["purpose"] = "billing-integration-test" },
        Description = "Dream Cleaning billing test",
        OffSession = true
    };

    [StripeTestModeFact]
    public async Task AnOffSessionCardOnlyCharge_IsAccepted_AndSucceeds()
    {
        var stripe = RealStripe();
        var (customer, pm) = await CustomerWithCard("pm_card_visa");

        var result = await stripe.ChargeSavedCardAsync(Request(customer, pm, 123.45m));

        Assert.Equal(SavedCardChargeOutcome.Succeeded, result.Outcome);
        var intent = await stripe.GetPaymentIntentAsync(result.PaymentIntentId!);
        Assert.Equal("succeeded", intent.Status);
        Assert.Equal(12345, intent.AmountReceived);
        Assert.Equal(new[] { "card" }, intent.PaymentMethodTypes);
    }

    [StripeTestModeFact]
    public async Task ReplayingTheSameIdempotencyKey_ReturnsTheSameIntent_NotASecondCharge()
    {
        var stripe = RealStripe();
        var (customer, pm) = await CustomerWithCard("pm_card_visa");
        var request = Request(customer, pm, 20m);

        var first = await stripe.ChargeSavedCardAsync(request);
        var replay = await stripe.ChargeSavedCardAsync(request);

        Assert.Equal(SavedCardChargeOutcome.Succeeded, first.Outcome);
        Assert.Equal(first.PaymentIntentId, replay.PaymentIntentId);
        var charges = await new PaymentIntentService().ListAsync(new PaymentIntentListOptions { Customer = customer });
        Assert.Single(charges.Data);
    }

    [StripeTestModeFact]
    public async Task ADeclinedSavedCard_IsADecline_WithTheIssuersCode()
    {
        var stripe = RealStripe();
        var (customer, pm) = await CustomerWithCard("pm_card_chargeCustomerFail");

        var result = await stripe.ChargeSavedCardAsync(Request(customer, pm, 20m));

        Assert.Equal(SavedCardChargeOutcome.Declined, result.Outcome);
        Assert.Equal("card_declined", result.FailureCode);
        Assert.NotNull(result.PaymentIntentId);
    }

    [StripeTestModeFact]
    public async Task ACardThatNeedsAuthentication_IsRequiresAction_AndItsIntentCanBeCancelled()
    {
        var stripe = RealStripe();
        var (customer, pm) = await CustomerWithCard("pm_card_authenticationRequired");

        var result = await stripe.ChargeSavedCardAsync(Request(customer, pm, 20m));

        Assert.Equal(SavedCardChargeOutcome.RequiresAction, result.Outcome);
        Assert.NotNull(result.PaymentIntentId);
        var cancelled = await stripe.CancelPaymentIntentAsync(result.PaymentIntentId!);
        Assert.Equal("canceled", cancelled.Status);
    }

    [StripeTestModeFact]
    public async Task CancellingASucceededIntent_IsRefusedByStripe()
    {
        // The whole "cancel the customer's open intent before charging" guard relies on this.
        var stripe = RealStripe();
        var (customer, pm) = await CustomerWithCard("pm_card_visa");
        var paid = await stripe.ChargeSavedCardAsync(Request(customer, pm, 5m));

        await Assert.ThrowsAsync<StripeException>(() => stripe.CancelPaymentIntentAsync(paid.PaymentIntentId!));
    }

    [StripeTestModeFact]
    public async Task ASetupIntent_IsCardOnly_AndOffSession()
    {
        var stripe = RealStripe();
        var customer = await new CustomerService().CreateAsync(new CustomerCreateOptions { Description = "Dream Cleaning billing test (automated)" });

        var setup = await stripe.CreateSetupIntentAsync(customer.Id, new Dictionary<string, string> { ["purpose"] = "billing-integration-test" });
        Assert.Equal("off_session", setup.Usage);
        Assert.Equal(new[] { "card" }, setup.PaymentMethodTypes);

        var confirmed = await new SetupIntentService().ConfirmAsync(setup.Id, new SetupIntentConfirmOptions { PaymentMethod = "pm_card_visa" });
        Assert.Equal("succeeded", confirmed.Status);
        var fetched = await stripe.GetSetupIntentAsync(setup.Id);
        Assert.Equal(customer.Id, fetched.CustomerId);
        Assert.False(string.IsNullOrEmpty(fetched.PaymentMethodId));
    }

    // ── End to end: real Stripe + real database + the real billing services ─────────────────

    private async Task<int> SaveRealCardAsync(BillingHarness h, int userId, string testPaymentMethod)
    {
        await using var scope = h.Scope();
        var cards = scope.ServiceProvider.GetRequiredService<IPaymentMethodService>();
        var setup = await cards.CreateSetupIntentAsync(userId);
        await new SetupIntentService().ConfirmAsync(setup.SetupIntentId, new SetupIntentConfirmOptions { PaymentMethod = testPaymentMethod });
        return (await cards.CompleteSetupIntentAsync(userId, setup.SetupIntentId)).Id;
    }

    private async Task<Models.User> RealStripeUser(BillingHarness h)
    {
        var user = await h.AddUserAsync();
        await using var db = h.NewContext();
        var row = await db.Users.FirstAsync(u => u.Id == user.Id);
        row.StripeCustomerId = null; // the real service creates a real test-mode Customer
        await db.SaveChangesAsync();
        return row;
    }

    [StripeAndMariaDbFact]
    public async Task EndToEnd_AdminChargeOfTheBalanceDue_OnARealTestCard()
    {
        var h = new BillingHarness(_db, realStripe: RealStripe());
        var user = await RealStripeUser(h);
        await SaveRealCardAsync(h, user.Id, "pm_card_visa");
        await h.AuthorizeOfficeAsync(user.Id, allowBackup: false);
        var order = await h.AddOrderAsync(user.Id, 386.16m, amountPaid: 100m);

        var result = await h.AdminChargeAsync(order.Id);

        Assert.True(result.Result == "paid", await LedgerAsync(h, order.Id, result));
        var intent = await h.StripeApi.GetPaymentIntentAsync(result.PaymentIntentId!);
        Assert.Equal(28616, intent.AmountReceived); // the balance due, not the total
        Assert.Equal(SavedCardChargeService.OrderMetadataType, intent.Metadata["type"]);
        await using var db = h.NewContext();
        Assert.True((await db.Orders.AsNoTracking().FirstAsync(o => o.Id == order.Id)).IsPaid);
    }

    [StripeAndMariaDbFact]
    public async Task EndToEnd_PrimaryDeclined_BackupPays_ExactlyOnce()
    {
        var h = new BillingHarness(_db, realStripe: RealStripe());
        var user = await RealStripeUser(h);
        await SaveRealCardAsync(h, user.Id, "pm_card_chargeCustomerFail");
        var backup = await SaveRealCardAsync(h, user.Id, "pm_card_visa");
        await h.SetBackupAsync(user.Id, backup);
        await h.AuthorizeOfficeAsync(user.Id, allowBackup: true);
        var order = await h.AddOrderAsync(user.Id, 99.99m);

        var result = await h.AdminChargeAsync(order.Id);

        Assert.True(result.Result == "paid_by_backup", await LedgerAsync(h, order.Id, result));
        string customer;
        await using (var db = h.NewContext())
            customer = (await db.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id)).StripeCustomerId!;
        var intents = await new PaymentIntentService().ListAsync(new PaymentIntentListOptions { Customer = customer });
        Assert.Single(intents.Data, i => i.Status == "succeeded");
    }

    [StripeAndMariaDbFact]
    public async Task EndToEnd_PayAllUpcoming_OneRealCharge_SettlesEveryOrder_AndRefundsOnlyOneShare()
    {
        // The bug this pins: ONE charge covering two cleanings used to be written onto both
        // orders' UNIQUE PaymentIntentId, so the real card was charged and neither was recorded.
        var h = new BillingHarness(_db, realStripe: RealStripe());
        var user = await h.AddUserAsync();
        var template = await h.AddOrderAsync(user.Id, 150m, serviceDate: DateTime.UtcNow.Date.AddDays(-30), bookedByAdmin: false);
        int seriesId;
        await using (var db = h.NewContext())
        {
            var t = await db.Orders.FirstAsync(o => o.Id == template.Id);
            t.IsPaid = true;
            db.Users.First(u => u.Id == user.Id).StripeCustomerId = null;
            var series = new DreamCleaningBackend.Models.RecurringOrderSeries
            {
                UserId = user.Id, TemplateOrderId = template.Id, IntervalValue = 1,
                IntervalUnit = DreamCleaningBackend.Models.RecurrenceIntervalUnit.Weeks,
                AnchorDate = DateTime.UtcNow.Date, ServiceTime = TimeSpan.FromHours(10), IsActive = true, CreatedByUserId = user.Id
            };
            db.RecurringOrderSeries.Add(series);
            await db.SaveChangesAsync();
            seriesId = series.Id;
        }
        var first = await h.AddOrderAsync(user.Id, 150m, seriesId: seriesId, serviceDate: DateTime.UtcNow.Date.AddDays(3), bookedByAdmin: false);
        var second = await h.AddOrderAsync(user.Id, 99.99m, amountPaid: 20m, seriesId: seriesId, serviceDate: DateTime.UtcNow.Date.AddDays(10), bookedByAdmin: false);

        RecurringCustomerPaymentService Service(DreamCleaningBackend.Data.ApplicationDbContext db) =>
            new(db, h.StripeApi, h.Audit, NullLogger<RecurringCustomerPaymentService>.Instance);

        CombinedPaymentDto batch;
        await using (var db = h.NewContext())
            batch = await Service(db).StartCombinedPaymentAsync(user.Id, new StartCombinedPaymentDto());
        Assert.Equal(229.99m, batch.Amount);                              // 150 + (99.99 − 20 deposit)

        // What Stripe.js does in the browser, with Stripe's documented test card.
        var confirmed = await new PaymentIntentService().ConfirmAsync(batch.PaymentIntentId,
            new PaymentIntentConfirmOptions { PaymentMethod = "pm_card_visa" });
        Assert.Equal("succeeded", confirmed.Status);
        Assert.Equal(22999, confirmed.AmountReceived);

        await using (var db = h.NewContext())
        {
            Assert.True(await Service(db).SettleBatchAsync(batch.PaymentIntentId!, batch.BatchId));
            Assert.False(await Service(db).SettleBatchAsync(batch.PaymentIntentId!, batch.BatchId));   // a redelivered webhook
        }

        await using (var db = h.NewContext())
        {
            var orders = await db.Orders.AsNoTracking().Where(o => o.Id == first.Id || o.Id == second.Id).ToListAsync();
            Assert.All(orders, o => Assert.True(o.IsPaid));
            Assert.All(orders, o => Assert.Null(o.PaymentIntentId));

            var refunds = new OrderRefundService(db, h.StripeApi, h.Messaging.Email, h.Audit, NullLogger<OrderRefundService>.Instance);
            var summary = await refunds.GetRefundSummaryAsync(second.Id);
            Assert.Equal(79.99m, summary.RemainingRefundable);             // its share, not the whole 229.99
            var result = await refunds.IssueRefundAsync(second.Id, null, "e2e", adminUserId: user.Id, sendEmail: false);
            Assert.True(result.Success, result.Message);
            Assert.Equal(79.99m, result.AmountRefunded);
        }

        var after = await new PaymentIntentService().GetAsync(batch.PaymentIntentId, new PaymentIntentGetOptions { Expand = new List<string> { "latest_charge" } });
        Assert.Equal(7999, after.LatestCharge.AmountRefunded);           // the first order's 150.00 is untouched
    }

    /// <summary>
    /// A failed end-to-end run is only useful if it says WHY: the response message is written for
    /// a customer, so the ledger's own failure codes are what tell a decline from a transport
    /// failure or a rate limit.
    /// </summary>
    private static async Task<string> LedgerAsync(BillingHarness h, int orderId, SavedCardChargeResponseDto result)
    {
        await using var db = h.NewContext();
        var key = "order:" + orderId;
        var rows = await db.BillingPaymentAttempts.AsNoTracking()
            .Where(a => a.ObligationKey == key).OrderBy(a => a.Id).ToListAsync();
        return $"result={result.Result} message={result.Message} ledger=[" + string.Join("; ", rows.Select(a =>
            $"{a.CardRole}/{a.Status} code={a.FailureCode} msg={a.FailureMessage}")) + "]";
    }
}
