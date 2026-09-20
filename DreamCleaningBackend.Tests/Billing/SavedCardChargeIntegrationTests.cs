using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Models.Billing;
using DreamCleaningBackend.Models.Commercial;
using DreamCleaningBackend.Models.Contracts;
using DreamCleaningBackend.Services.Billing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamCleaningBackend.Tests.Billing;

/// <summary>
/// The saved-card money paths against a REAL MariaDB (throwaway database, every migration applied),
/// with a scripted Stripe. Real database because the guarantees under test ARE database guarantees:
/// the unique lock index, the CHECK constraints on card roles, the conditional UPDATEs that settle
/// an order exactly once. The in-memory provider has none of them.
///
/// Every test asserts the thing that matters most: at most ONE successful collection per
/// obligation.
/// </summary>
public class SavedCardChargeIntegrationTests : IClassFixture<MariaDbDatabase>
{
    private readonly MariaDbDatabase _db;

    public SavedCardChargeIntegrationTests(MariaDbDatabase db) => _db = db;

    private BillingHarness Harness(bool savedCards = true, bool autoPay = true) => new(_db, savedCards, autoPay);

    private async Task<Order> ReloadOrder(BillingHarness h, int orderId)
    {
        await using var db = h.NewContext();
        return await db.Orders.AsNoTracking().FirstAsync(o => o.Id == orderId);
    }

    private async Task<List<BillingPaymentAttempt>> Attempts(BillingHarness h, int orderId)
    {
        await using var db = h.NewContext();
        return await db.BillingPaymentAttempts.AsNoTracking().Where(a => a.OrderId == orderId).OrderBy(a => a.Id).ToListAsync();
    }

    private async Task<List<BillingNotification>> Notices(BillingHarness h, int userId)
    {
        await using var db = h.NewContext();
        return await db.BillingNotifications.AsNoTracking().Where(n => n.UserId == userId).ToListAsync();
    }

    // ═══ Saved cards: roles and their invariants ═══════════════════════════════════════════════

    [MariaDbFact]
    public async Task FirstCardBecomesPrimary_LaterCardsStayOrdinary()
    {
        var h = Harness();
        var user = await h.AddUserAsync();
        var first = await h.AddCardAsync(user.Id, CardBehaviour.Succeed, "1111");
        var second = await h.AddCardAsync(user.Id, CardBehaviour.Succeed, "2222");
        var third = await h.AddCardAsync(user.Id, CardBehaviour.Succeed, "3333");

        await using var db = h.NewContext();
        var row = await db.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id);
        Assert.Equal(first, row.PrimaryPaymentMethodId);
        Assert.Null(row.BackupPaymentMethodId); // never auto-assigned
        Assert.Equal(3, await db.CustomerPaymentMethods.CountAsync(c => c.UserId == user.Id));
        Assert.False(row.AutoPayEnabled); // saving a card never turns AutoPay on
        // Legacy mirror follows the Primary.
        Assert.Equal("1111", row.SavedCardLast4);
        Assert.NotEqual(second, third);
    }

    [MariaDbFact]
    public async Task PrimaryAndBackup_MustBeDifferentCards()
    {
        var h = Harness();
        var user = await h.AddUserAsync();
        var a = await h.AddCardAsync(user.Id, CardBehaviour.Succeed);
        await using var scope = h.Scope();
        var cards = scope.ServiceProvider.GetRequiredService<IPaymentMethodService>();

        var ex = await Assert.ThrowsAsync<BillingRuleException>(() => cards.SetBackupAsync(user.Id, a));
        Assert.Equal("same_as_primary", ex.Code);
    }

    [MariaDbFact]
    public async Task PromotingTheBackup_EmptiesTheBackupSlot_AndSaysSo()
    {
        var h = Harness();
        var user = await h.AddUserAsync();
        var a = await h.AddCardAsync(user.Id, CardBehaviour.Succeed);
        var b = await h.AddCardAsync(user.Id, CardBehaviour.Succeed, "5555");
        await h.SetBackupAsync(user.Id, b);

        await using var scope = h.Scope();
        var message = await scope.ServiceProvider.GetRequiredService<IPaymentMethodService>().SetPrimaryAsync(user.Id, b);
        Assert.Contains("no longer have a Backup card", message);

        await using var db = h.NewContext();
        var row = await db.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id);
        Assert.Equal(b, row.PrimaryPaymentMethodId);
        Assert.Null(row.BackupPaymentMethodId);
        Assert.NotEqual(a, row.PrimaryPaymentMethodId);
    }

    [MariaDbFact]
    public async Task TheDatabaseRefusesACardThatIsBothPrimaryAndBackup()
    {
        var h = Harness();
        var user = await h.AddUserAsync();
        var a = await h.AddCardAsync(user.Id, CardBehaviour.Succeed);

        await using var db = h.NewContext();
        var row = await db.Users.FirstAsync(u => u.Id == user.Id);
        row.BackupPaymentMethodId = a; // same as the Primary, bypassing the service
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [MariaDbFact]
    public async Task ConcurrentRoleChanges_NeverBreakTheInvariants()
    {
        var h = Harness();
        var user = await h.AddUserAsync();
        var a = await h.AddCardAsync(user.Id, CardBehaviour.Succeed, "1111");
        var b = await h.AddCardAsync(user.Id, CardBehaviour.Succeed, "2222");
        var c = await h.AddCardAsync(user.Id, CardBehaviour.Succeed, "3333");

        async Task Try(Func<IPaymentMethodService, Task> op)
        {
            await using var scope = h.Scope();
            try { await op(scope.ServiceProvider.GetRequiredService<IPaymentMethodService>()); }
            catch (BillingRuleException) { /* a refused move is fine; a broken invariant is not */ }
        }

        var ops = new List<Task>();
        for (var i = 0; i < 6; i++)
        {
            ops.Add(Try(s => s.SetPrimaryAsync(user.Id, b)));
            ops.Add(Try(s => s.SetBackupAsync(user.Id, b)));
            ops.Add(Try(s => s.SetBackupAsync(user.Id, c)));
            ops.Add(Try(s => s.SetPrimaryAsync(user.Id, a)));
        }
        await Task.WhenAll(ops);

        await using var db = h.NewContext();
        var row = await db.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id);
        Assert.NotNull(row.PrimaryPaymentMethodId);
        Assert.NotEqual(row.PrimaryPaymentMethodId, row.BackupPaymentMethodId);
    }

    [MariaDbFact]
    public async Task RemovingThePrimary_RequiresChoosingItsSuccessor()
    {
        var h = Harness();
        var user = await h.AddUserAsync();
        var a = await h.AddCardAsync(user.Id, CardBehaviour.Succeed);
        var b = await h.AddCardAsync(user.Id, CardBehaviour.Succeed, "9999");
        await using var scope = h.Scope();
        var cards = scope.ServiceProvider.GetRequiredService<IPaymentMethodService>();

        var ex = await Assert.ThrowsAsync<BillingRuleException>(() => cards.RemoveAsync(user.Id, a, null));
        Assert.Equal("choose_new_primary", ex.Code);

        await cards.RemoveAsync(user.Id, a, b);
        await using var db = h.NewContext();
        Assert.Equal(b, (await db.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id)).PrimaryPaymentMethodId);
        Assert.Equal(CustomerPaymentMethodStatus.Removed, (await db.CustomerPaymentMethods.FirstAsync(c => c.Id == a)).Status);
        Assert.Contains(h.Stripe.PaymentMethods.Keys, k => h.Stripe.Detached.Contains(k)); // detached at Stripe
    }

    [MariaDbFact]
    public async Task RemovingTheLastCard_TurnsAutoPayOff()
    {
        var h = Harness();
        var user = await h.AddUserAsync();
        var a = await h.AddCardAsync(user.Id, CardBehaviour.Succeed);
        await h.EnableAutoPayAsync(user.Id);

        await using var scope = h.Scope();
        var message = await scope.ServiceProvider.GetRequiredService<IPaymentMethodService>().RemoveAsync(user.Id, a, null);
        Assert.Contains("Automatic Payments were turned off", message);

        await using var db = h.NewContext();
        var row = await db.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id);
        Assert.False(row.AutoPayEnabled);
        Assert.Null(row.PrimaryPaymentMethodId);
        Assert.False(await db.PaymentAuthorizations.AnyAsync(x => x.UserId == user.Id && x.Status == PaymentAuthorizationStatus.Active
                                                                  && x.Scope == PaymentAuthorizationScope.General));
    }

    [MariaDbFact]
    public async Task AnotherCustomersCard_CannotBeTouched()
    {
        var h = Harness();
        var owner = await h.AddUserAsync();
        var intruder = await h.AddUserAsync();
        var card = await h.AddCardAsync(owner.Id, CardBehaviour.Succeed);
        await h.AddCardAsync(intruder.Id, CardBehaviour.Succeed);

        await using var scope = h.Scope();
        var cards = scope.ServiceProvider.GetRequiredService<IPaymentMethodService>();
        await Assert.ThrowsAsync<BillingRuleException>(() => cards.SetPrimaryAsync(intruder.Id, card));
        await Assert.ThrowsAsync<BillingRuleException>(() => cards.RemoveAsync(intruder.Id, card, null));
        Assert.DoesNotContain(await cards.ListAsync(intruder.Id, true), c => c.Id == card);
    }

    [MariaDbFact]
    public async Task ASetupIntentFromAnotherCustomer_IsRefused()
    {
        var h = Harness();
        var owner = await h.AddUserAsync();
        var intruder = await h.AddUserAsync();
        await using var scope = h.Scope();
        var cards = scope.ServiceProvider.GetRequiredService<IPaymentMethodService>();
        var setup = await cards.CreateSetupIntentAsync(owner.Id);
        h.Stripe.CompleteSetup(setup.SetupIntentId, CardBehaviour.Succeed);

        await Assert.ThrowsAsync<BillingRuleException>(() => cards.CompleteSetupIntentAsync(intruder.Id, setup.SetupIntentId));
    }

    [MariaDbFact]
    public async Task ADetachAtStripe_PromotesTheBackup_AndTellsTheCustomer()
    {
        var h = Harness();
        var user = await h.AddUserAsync();
        var a = await h.AddCardAsync(user.Id, CardBehaviour.Succeed);
        var b = await h.AddCardAsync(user.Id, CardBehaviour.Succeed, "7777");
        await h.SetBackupAsync(user.Id, b);

        string pmA;
        await using (var db0 = h.NewContext()) pmA = (await db0.CustomerPaymentMethods.FirstAsync(c => c.Id == a)).StripePaymentMethodId;

        await using (var scope = h.Scope())
            await scope.ServiceProvider.GetRequiredService<IPaymentMethodService>().HandleDetachedAtStripeAsync(pmA);

        await using var db = h.NewContext();
        var row = await db.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id);
        Assert.Equal(b, row.PrimaryPaymentMethodId);
        Assert.Null(row.BackupPaymentMethodId);
        Assert.Contains(await Notices(h, user.Id), n => n.Type == BillingNotificationType.CardRemoved);
    }

    // ═══ Admin charge: the audit's findings ════════════════════════════════════════════════════

    [MariaDbFact]
    public async Task AdminCharge_WithoutTheCustomersOfficeAuthorisation_IsRefused_AndChargesNothing()
    {
        var h = Harness();
        var user = await h.AddUserAsync();
        await h.AddCardAsync(user.Id, CardBehaviour.Succeed);
        var order = await h.AddOrderAsync(user.Id, 200m);

        var result = await h.AdminChargeAsync(order.Id);
        Assert.Equal("not_authorized", result.Result);
        Assert.Contains("payment link", result.Message);
        Assert.Empty(h.Stripe.ChargeCalls);
    }

    [MariaDbFact]
    public async Task AdminCharge_TakesTheBalanceDue_NotTheOrderTotal()
    {
        // Audit finding #1: a $1,000 deposit was re-taken because the endpoint charged Total.
        var h = Harness();
        var user = await h.AddUserAsync();
        await h.AddCardAsync(user.Id, CardBehaviour.Succeed);
        await h.AuthorizeOfficeAsync(user.Id, allowBackup: false);
        var order = await h.AddOrderAsync(user.Id, 2743.65m, amountPaid: 1000m);

        var result = await h.AdminChargeAsync(order.Id);

        Assert.Equal("paid", result.Result);
        Assert.Equal(1743.65m, result.Amount);
        var intent = h.Stripe.Intents[Assert.Single(h.Stripe.SucceededCharges)];
        Assert.Equal(174365, intent.Amount);
        var saved = await ReloadOrder(h, order.Id);
        Assert.True(saved.IsPaid);
        Assert.Equal(intent.Id, saved.PaymentIntentId);
    }

    [MariaDbFact]
    public async Task AdminCharge_KeepsADoneCleaningDone()
    {
        // Audit finding #7.
        var h = Harness();
        var user = await h.AddUserAsync();
        await h.AddCardAsync(user.Id, CardBehaviour.Succeed);
        await h.AuthorizeOfficeAsync(user.Id, false);
        var order = await h.AddOrderAsync(user.Id, 150m, status: "Done");

        Assert.Equal("paid", (await h.AdminChargeAsync(order.Id)).Result);
        var saved = await ReloadOrder(h, order.Id);
        Assert.Equal("Done", saved.Status);
        Assert.True(saved.IsPaid);
        Assert.Equal(150m, saved.InitialTotal); // snapshot taken once
    }

    [MariaDbFact]
    public async Task TwoAdminsChargingAtOnce_ChargeTheCustomerOnce()
    {
        // Audit findings #3 and #4: the old key changed every minute and nothing locked the order.
        var h = Harness();
        h.Stripe.ChargeDelay = TimeSpan.FromMilliseconds(300);
        var user = await h.AddUserAsync();
        await h.AddCardAsync(user.Id, CardBehaviour.Succeed);
        await h.AuthorizeOfficeAsync(user.Id, false);
        var order = await h.AddOrderAsync(user.Id, 300m);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => h.AdminChargeAsync(order.Id, adminId: 100 + i)));

        Assert.Single(h.Stripe.SucceededCharges);
        Assert.Single(results, r => r.Charged);
        Assert.All(results.Where(r => !r.Charged), r => Assert.Contains(r.Result, new[] { "blocked", "nothing_due", "pending" }));
        Assert.True((await ReloadOrder(h, order.Id)).IsPaid);
    }

    [MariaDbFact]
    public async Task AnOpenCustomerIntent_IsCancelledBeforeTheCharge_SoItCanNeverAlsoBePaid()
    {
        // Audit finding #2.
        var h = Harness();
        var user = await h.AddUserAsync();
        await h.AddCardAsync(user.Id, CardBehaviour.Succeed);
        await h.AuthorizeOfficeAsync(user.Id, false);
        var order = await h.AddOrderAsync(user.Id, 120m);
        var browserIntent = h.Stripe.AddCustomerIntent(order.Id, 120m, "requires_payment_method");
        await using (var db = h.NewContext())
        {
            var row = await db.Orders.FirstAsync(o => o.Id == order.Id);
            row.PaymentIntentId = browserIntent.Id;
            await db.SaveChangesAsync();
        }

        Assert.Equal("paid", (await h.AdminChargeAsync(order.Id)).Result);
        Assert.Contains(browserIntent.Id, h.Stripe.Cancelled);
        Assert.False(h.Stripe.CustomerConfirms(browserIntent.Id)); // the old tab can no longer pay
        Assert.Single(h.Stripe.SucceededCharges);
    }

    [MariaDbFact]
    public async Task ACustomerPaymentAlreadyGoingThrough_StopsTheAdminCharge()
    {
        var h = Harness();
        var user = await h.AddUserAsync();
        await h.AddCardAsync(user.Id, CardBehaviour.Succeed);
        await h.AuthorizeOfficeAsync(user.Id, false);
        var order = await h.AddOrderAsync(user.Id, 120m);
        var browserIntent = h.Stripe.AddCustomerIntent(order.Id, 120m, "processing");
        await using (var db = h.NewContext())
        {
            var row = await db.Orders.FirstAsync(o => o.Id == order.Id);
            row.PaymentIntentId = browserIntent.Id;
            await db.SaveChangesAsync();
        }

        var result = await h.AdminChargeAsync(order.Id);
        Assert.Equal("blocked", result.Result);
        Assert.Empty(h.Stripe.ChargeCalls);
        // The lock was released: nothing is left holding the order.
        Assert.All(await Attempts(h, order.Id), a => Assert.Null(a.ActiveLockKey));
    }

    [MariaDbFact]
    public async Task AnOpenPartPaymentRequest_BlocksTheCharge()
    {
        var h = Harness();
        var user = await h.AddUserAsync();
        await h.AddCardAsync(user.Id, CardBehaviour.Succeed);
        await h.AuthorizeOfficeAsync(user.Id, false);
        var order = await h.AddOrderAsync(user.Id, 900m);
        await using (var db = h.NewContext())
        {
            db.OrderPartialPayments.Add(new OrderPartialPayment
            {
                OrderId = order.Id, RequestedAmount = 300m, Status = OrderPartialPaymentStatus.Pending, CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var result = await h.AdminChargeAsync(order.Id);
        Assert.Equal("blocked", result.Result);
        Assert.Contains("part-payment", result.Message);
        Assert.Empty(h.Stripe.ChargeCalls);
    }

    [MariaDbFact]
    public async Task AFailedAdminCharge_IsAudited_AndLeavesTheOrderUnpaid()
    {
        // Audit finding #8.
        var h = Harness();
        var user = await h.AddUserAsync();
        await h.AddCardAsync(user.Id, CardBehaviour.Decline);
        await h.AuthorizeOfficeAsync(user.Id, false);
        var order = await h.AddOrderAsync(user.Id, 80m);

        var result = await h.AdminChargeAsync(order.Id);
        Assert.Equal("failed", result.Result);
        Assert.False((await ReloadOrder(h, order.Id)).IsPaid);
        Assert.Contains(h.Audit.Actions, a => a.EntityType == Services.AuditEntityTypes.SavedCardChargeAction
                                             && a.EntityId == order.Id && a.Action == "AttemptFailed");
        var attempt = Assert.Single(await Attempts(h, order.Id));
        Assert.Null(attempt.ActiveLockKey);
        Assert.Equal("insufficient_funds", attempt.DeclineCode);
    }

    // ═══ Primary → Backup ═════════════════════════════════════════════════════════════════════

    [MariaDbFact]
    public async Task PrimaryDeclined_BackupPays_OnceForTheSameObligation_AndTheCustomerIsTold()
    {
        var h = Harness();
        var user = await h.AddUserAsync();
        await h.AddCardAsync(user.Id, CardBehaviour.Decline, "0002");
        var backup = await h.AddCardAsync(user.Id, CardBehaviour.Succeed, "4242");
        await h.SetBackupAsync(user.Id, backup);
        await h.AuthorizeOfficeAsync(user.Id, allowBackup: true);
        var order = await h.AddOrderAsync(user.Id, 250m);

        var result = await h.AdminChargeAsync(order.Id);

        Assert.Equal("paid_by_backup", result.Result);
        Assert.Single(h.Stripe.SucceededCharges);
        var attempts = await Attempts(h, order.Id);
        Assert.Equal(2, attempts.Count);
        Assert.Equal(attempts[0].RunKey, attempts[1].RunKey);
        Assert.Equal(attempts[0].ObligationKey, attempts[1].ObligationKey);
        Assert.Equal(BillingCardRole.Backup, attempts[1].CardRole);
        var notice = Assert.Single(await Notices(h, user.Id), n => n.Type == BillingNotificationType.PrimaryFailedBackupSucceeded);
        Assert.Contains("0002", notice.Message);
        Assert.Contains("nothing more is owed", notice.Message);
    }

    [MariaDbFact]
    public async Task BackupNotAuthorised_IsNeverTried()
    {
        var h = Harness();
        var user = await h.AddUserAsync();
        await h.AddCardAsync(user.Id, CardBehaviour.Decline);
        var backup = await h.AddCardAsync(user.Id, CardBehaviour.Succeed, "4242");
        await h.SetBackupAsync(user.Id, backup);
        await h.AuthorizeOfficeAsync(user.Id, allowBackup: false);
        var order = await h.AddOrderAsync(user.Id, 250m);

        Assert.Equal("failed", (await h.AdminChargeAsync(order.Id)).Result);
        Assert.Single(await Attempts(h, order.Id));
        Assert.Empty(h.Stripe.SucceededCharges);
    }

    [MariaDbFact]
    public async Task PrimaryRequiresAuthentication_ItsIntentIsCancelled_ThenBackupIsTried()
    {
        var h = Harness();
        var user = await h.AddUserAsync();
        await h.AddCardAsync(user.Id, CardBehaviour.RequireAction, "3155");
        var backup = await h.AddCardAsync(user.Id, CardBehaviour.Succeed);
        await h.SetBackupAsync(user.Id, backup);
        await h.AuthorizeOfficeAsync(user.Id, allowBackup: true);
        var order = await h.AddOrderAsync(user.Id, 99m);

        Assert.Equal("paid_by_backup", (await h.AdminChargeAsync(order.Id)).Result);
        var first = (await Attempts(h, order.Id))[0];
        Assert.Equal(BillingAttemptStatus.RequiresAction, first.Status);
        Assert.Contains(first.StripePaymentIntentId!, h.Stripe.Cancelled); // can never be completed later
        Assert.Single(h.Stripe.SucceededCharges);
    }

    [MariaDbFact]
    public async Task AnUnknownOutcome_NeverTriesTheBackup_AndHoldsTheLock()
    {
        var h = Harness();
        var user = await h.AddUserAsync();
        await h.AddCardAsync(user.Id, CardBehaviour.Unknown);
        var backup = await h.AddCardAsync(user.Id, CardBehaviour.Succeed);
        await h.SetBackupAsync(user.Id, backup);
        await h.AuthorizeOfficeAsync(user.Id, allowBackup: true);
        var order = await h.AddOrderAsync(user.Id, 99m);

        var result = await h.AdminChargeAsync(order.Id);

        Assert.Equal("unknown", result.Result);
        Assert.Contains("Do NOT charge again", result.Message);
        Assert.Empty(h.Stripe.SucceededCharges);
        var attempt = Assert.Single(await Attempts(h, order.Id));
        Assert.Equal(BillingAttemptStatus.Unknown, attempt.Status);
        Assert.Equal($"order:{order.Id}", attempt.ActiveLockKey);

        // And nothing else can start while it is unknown.
        var second = await h.AdminChargeAsync(order.Id);
        Assert.Equal("blocked", second.Result);
        await using var scope = h.Scope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<ISavedCardChargeService>().HasActiveAttemptAsync($"order:{order.Id}"));
    }

    [MariaDbFact]
    public async Task ATimeoutFollowedBySuccess_IsOneCharge_ViaTheSameIdempotencyKey()
    {
        var h = Harness();
        var user = await h.AddUserAsync();
        await h.AddCardAsync(user.Id, CardBehaviour.UnknownThenSucceed);
        await h.AuthorizeOfficeAsync(user.Id, false);
        var order = await h.AddOrderAsync(user.Id, 60m);

        var result = await h.AdminChargeAsync(order.Id);

        Assert.Equal("paid", result.Result);
        Assert.Single(h.Stripe.SucceededCharges);
        Assert.Equal(2, h.Stripe.ChargeCalls.Count);
        Assert.Single(h.Stripe.ChargeCalls.Distinct()); // the replay reused the key
    }

    [MariaDbFact]
    public async Task TheReconciler_ResolvesAnUnknownAttemptFromStripe_AndSettlesItOnce()
    {
        var h = Harness();
        var user = await h.AddUserAsync();
        var cardId = await h.AddCardAsync(user.Id, CardBehaviour.Unknown);
        await h.AuthorizeOfficeAsync(user.Id, false);
        var order = await h.AddOrderAsync(user.Id, 75m);
        Assert.Equal("unknown", (await h.AdminChargeAsync(order.Id)).Result);

        // Stripe did take the money after all: an intent carrying our attempt id exists.
        var attempt = Assert.Single(await Attempts(h, order.Id));
        var pm = h.Stripe.PaymentMethods.Keys.Single(k => h.Stripe.CardBehaviours[k] == CardBehaviour.Unknown);
        var intent = new Stripe.PaymentIntent
        {
            Id = "pi_late_success", Status = "succeeded", Amount = 7500, AmountReceived = 7500, Currency = "usd", PaymentMethodId = pm,
            Metadata = new() { [SavedCardChargeService.AttemptIdMetadataKey] = attempt.Id.ToString(), ["orderId"] = order.Id.ToString() }
        };
        h.Stripe.Intents[intent.Id] = intent;
        await using (var db = h.NewContext())
            await db.BillingPaymentAttempts.Where(a => a.Id == attempt.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.UpdatedAt, DateTime.UtcNow.AddMinutes(-10)));

        await using (var scope = h.Scope())
        {
            var charges = scope.ServiceProvider.GetRequiredService<ISavedCardChargeService>();
            await charges.ReconcileStaleAttemptsAsync(CancellationToken.None);
            await charges.ReconcileStaleAttemptsAsync(CancellationToken.None); // idempotent
            await charges.HandleStripeIntentEventAsync(intent, "payment_intent.succeeded"); // duplicate webhook
        }

        var saved = await ReloadOrder(h, order.Id);
        Assert.True(saved.IsPaid);
        Assert.Equal("pi_late_success", saved.PaymentIntentId);
        var resolved = Assert.Single(await Attempts(h, order.Id));
        Assert.Equal(BillingAttemptStatus.Succeeded, resolved.Status);
        Assert.Null(resolved.ActiveLockKey);
        Assert.Empty(h.Stripe.Refunds);
        Assert.Equal(cardId, resolved.CustomerPaymentMethodId);
    }

    [MariaDbFact]
    public async Task AChargeThatLandsOnAnAlreadyPaidOrder_IsRefundedAutomatically()
    {
        var h = Harness();
        var user = await h.AddUserAsync();
        await h.AddCardAsync(user.Id, CardBehaviour.Unknown);
        await h.AuthorizeOfficeAsync(user.Id, false);
        var order = await h.AddOrderAsync(user.Id, 75m);
        Assert.Equal("unknown", (await h.AdminChargeAsync(order.Id)).Result);
        var attempt = Assert.Single(await Attempts(h, order.Id));

        // Meanwhile the office recorded a cash payment for the same cleaning.
        await using (var db = h.NewContext())
        {
            var row = await db.Orders.FirstAsync(o => o.Id == order.Id);
            row.IsPaid = true;
            row.PaymentIntentId = "pi_other_payment";
            await db.SaveChangesAsync();
        }

        var late = new Stripe.PaymentIntent
        {
            Id = "pi_late_dup", Status = "succeeded", Amount = 7500, AmountReceived = 7500, Currency = "usd",
            Metadata = new() { [SavedCardChargeService.AttemptIdMetadataKey] = attempt.Id.ToString() }
        };
        h.Stripe.Intents[late.Id] = late;
        await using (var scope = h.Scope())
            await scope.ServiceProvider.GetRequiredService<ISavedCardChargeService>().HandleStripeIntentEventAsync(late, "payment_intent.succeeded");

        Assert.Contains("pi_late_dup", h.Stripe.Refunds);
        Assert.Equal("pi_other_payment", (await ReloadOrder(h, order.Id)).PaymentIntentId);
    }

    [MariaDbFact]
    public async Task AStolenCardDecline_BlocksTheCardFromFutureUse()
    {
        var h = Harness();
        var user = await h.AddUserAsync();
        var card = await h.AddCardAsync(user.Id, CardBehaviour.NeverRetryDecline);
        await h.AuthorizeOfficeAsync(user.Id, false);
        var order = await h.AddOrderAsync(user.Id, 50m);

        Assert.Equal("failed", (await h.AdminChargeAsync(order.Id)).Result);
        await using var db = h.NewContext();
        Assert.Equal(CustomerPaymentMethodStatus.Blocked, (await db.CustomerPaymentMethods.FirstAsync(c => c.Id == card)).Status);

        // A second press does not touch the card again.
        var calls = h.Stripe.ChargeCalls.Count;
        Assert.Equal("failed", (await h.AdminChargeAsync(order.Id)).Result);
        Assert.Equal(calls, h.Stripe.ChargeCalls.Count);
    }

    // ═══ Recurring AutoPay ═════════════════════════════════════════════════════════════════════

    private async Task<(User user, int seriesId, Order first, Order second)> RecurringSetup(BillingHarness h, CardBehaviour primary,
        bool authorize = true, bool allowBackup = false)
    {
        var user = await h.AddUserAsync();
        await h.AddCardAsync(user.Id, primary);
        var template = await h.AddOrderAsync(user.Id, 150m, serviceDate: DateTime.UtcNow.Date.AddDays(-30));
        int seriesId;
        await using (var db = h.NewContext())
        {
            var t = await db.Orders.FirstAsync(o => o.Id == template.Id);
            t.IsPaid = true;
            var series = new RecurringOrderSeries
            {
                UserId = user.Id, TemplateOrderId = template.Id, IntervalValue = 2, IntervalUnit = RecurrenceIntervalUnit.Weeks,
                AnchorDate = DateTime.UtcNow.Date, ServiceTime = TimeSpan.FromHours(10), IsActive = true,
                AutoRequestPayment = true, CreatedByUserId = user.Id
            };
            db.RecurringOrderSeries.Add(series);
            await db.SaveChangesAsync();
            seriesId = series.Id;
            t.RecurringSeriesId = seriesId;
            await db.SaveChangesAsync();
        }

        // Two upcoming, unpaid occurrences — the sweep may only ever touch the FIRST.
        var first = await h.AddOrderAsync(user.Id, 150m, seriesId: seriesId, serviceDate: DateTime.UtcNow.Date.AddDays(3), bookedByAdmin: false);
        var second = await h.AddOrderAsync(user.Id, 150m, seriesId: seriesId, serviceDate: DateTime.UtcNow.Date.AddDays(17), bookedByAdmin: false);

        if (authorize)
        {
            await h.EnableAutoPayAsync(user.Id);
            await using var scope = h.Scope();
            await scope.ServiceProvider.GetRequiredService<IPaymentAuthorizationService>().AuthorizeAsync(user.Id, new AuthorizeArrangementDto
            {
                Scope = "series", RecurringSeriesId = seriesId, AllowBackupFallback = allowBackup, AcceptTerms = true,
                TermsVersion = Helpers.Billing.AutoPayTerms.Version
            }, "127.0.0.1", "tests");
        }

        return (user, seriesId, first, second);
    }

    private static async Task Sweep(BillingHarness h)
    {
        // The real sweep, through its public entry point, on the harness's services.
        var services = new ServiceCollection();
        var worker = new Services.RecurringOrderGenerationService(h.Provider,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Services.RecurringOrderGenerationService>.Instance,
            h.Provider.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>());
        var method = typeof(Services.RecurringOrderGenerationService).GetMethod("SendDuePaymentRequestsAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await using var scope = h.Scope();
        await (Task)method.Invoke(worker, new object[] { scope.ServiceProvider, CancellationToken.None })!;
    }

    [MariaDbFact]
    public async Task RecurringAutoPay_ChargesOnlyTheNextCleaning_AndSendsNoRequestForIt()
    {
        var h = Harness();
        var (user, _, first, second) = await RecurringSetup(h, CardBehaviour.Succeed);

        await Sweep(h);
        await Sweep(h); // a second pass the same day changes nothing

        Assert.Single(h.Stripe.SucceededCharges);
        Assert.True((await ReloadOrder(h, first.Id)).IsPaid);
        Assert.False((await ReloadOrder(h, second.Id)).IsPaid); // never the whole horizon
        Assert.Empty(h.Messaging.Emails.Where(e => e.Subject.Contains("Confirm Your Payment")));
        Assert.Contains(await Notices(h, user.Id), n => n.Type == BillingNotificationType.AutoPaySucceeded);
    }

    [MariaDbFact]
    public async Task RecurringAutoPay_FailingCard_IsTriedOnce_NotifiesThreeWays_AndIsNeverRetriedAutomatically()
    {
        var h = Harness();
        var (user, _, first, _) = await RecurringSetup(h, CardBehaviour.Decline);

        await Sweep(h);
        await using (var scope = h.Scope())
            await scope.ServiceProvider.GetRequiredService<IBillingNotificationService>().DeliverDueAsync(CancellationToken.None);
        await Sweep(h);

        Assert.Single(h.Stripe.ChargeCalls);
        Assert.False((await ReloadOrder(h, first.Id)).IsPaid);
        var notice = Assert.Single(await Notices(h, user.Id), n => n.Type == BillingNotificationType.AutoPayFailed);
        Assert.Equal(BillingDeliveryStatus.Sent, notice.EmailStatus);
        Assert.Equal(BillingDeliveryStatus.Sent, notice.SmsStatus);
        Assert.True(notice.ShowInApp);
        Assert.Contains("still owed", notice.Message);
    }

    [MariaDbFact]
    public async Task RecurringWithoutAuthorisation_KeepsTheOrdinaryRequest_AndChargesNothing()
    {
        var h = Harness();
        await RecurringSetup(h, CardBehaviour.Succeed, authorize: false);

        await Sweep(h);

        Assert.Empty(h.Stripe.ChargeCalls);
    }

    [MariaDbFact]
    public async Task AutoPaySwitchedOffServerSide_ChargesNothing()
    {
        var on = Harness();
        var (_, _, first, _) = await RecurringSetup(on, CardBehaviour.Succeed);
        var off = new BillingHarness(_db, savedCards: true, autoPay: false);

        await using var scope = off.Scope();
        var result = await scope.ServiceProvider.GetRequiredService<ISavedCardChargeService>()
            .ChargeOrderAsync(first.Id, BillingAttemptTrigger.AutoPayRecurring, null);
        Assert.Equal("not_authorized", result.Result);
        Assert.Empty(off.Stripe.ChargeCalls);
    }

    // ═══ Notifications ═════════════════════════════════════════════════════════════════════════

    [MariaDbFact]
    public async Task AFailedEmail_IsRetriedLater_AndNeverTouchesThePayment()
    {
        var h = Harness();
        h.Messaging.FailEmail = true;
        var (user, _, first, _) = await RecurringSetup(h, CardBehaviour.Decline);
        await Sweep(h);

        await using (var scope = h.Scope())
            await scope.ServiceProvider.GetRequiredService<IBillingNotificationService>().DeliverDueAsync(CancellationToken.None);

        var notice = Assert.Single(await Notices(h, user.Id), n => n.Type == BillingNotificationType.AutoPayFailed);
        Assert.Equal(BillingDeliveryStatus.Pending, notice.EmailStatus);
        Assert.Equal(1, notice.EmailAttempts);
        Assert.NotNull(notice.NextDeliveryAttemptAt);
        Assert.Equal(BillingDeliveryStatus.Sent, notice.SmsStatus); // SMS unaffected
        Assert.False((await ReloadOrder(h, first.Id)).IsPaid); // state untouched either way
    }

    [MariaDbFact]
    public async Task AFailureNotice_ForAnObligationPaidMeanwhile_IsNotSent_AndReadsResolved()
    {
        var h = Harness();
        var (user, _, first, _) = await RecurringSetup(h, CardBehaviour.Decline);
        await Sweep(h);

        // The customer paid through the payment link before the notice went out.
        await using (var db = h.NewContext())
        {
            var row = await db.Orders.FirstAsync(o => o.Id == first.Id);
            row.IsPaid = true;
            await db.SaveChangesAsync();
        }

        await using var scope = h.Scope();
        var notices = scope.ServiceProvider.GetRequiredService<IBillingNotificationService>();
        await notices.DeliverDueAsync(CancellationToken.None);
        var dto = Assert.Single(await notices.ListForUserAsync(user.Id), n => n.Type == "AutoPayFailed");

        Assert.True(dto.IsResolved);
        Assert.Null(dto.ActionUrl); // no stale "Pay now"
        // (The outbox is shared by every test in this database, so only THIS customer's messages count.)
        Assert.DoesNotContain(h.Messaging.Emails, e => e.To == user.Email);
        Assert.DoesNotContain(h.Messaging.Sms, s => s.To == user.Phone);

        await using var db2 = h.NewContext();
        var row2 = await db2.BillingNotifications.AsNoTracking().SingleAsync(n => n.UserId == user.Id && n.Type == BillingNotificationType.AutoPayFailed);
        Assert.Equal(BillingDeliveryStatus.Skipped, row2.EmailStatus);
        Assert.Equal(BillingDeliveryStatus.Skipped, row2.SmsStatus);
    }

    // ═══ Commercial AutoPay ════════════════════════════════════════════════════════════════════

    private async Task<(User user, ContractClient client)> CommercialSetup(BillingHarness h, bool authorize = true)
    {
        var user = await h.AddUserAsync();
        await h.AddCardAsync(user.Id, CardBehaviour.Succeed);
        ContractClient client;
        await using (var db = h.NewContext())
        {
            client = new ContractClient
            {
                LegalEntityName = "Acme LLC " + Guid.NewGuid().ToString("N")[..6], EntityType = "LLC", PrincipalAddress = "1 Main St",
                City = "New York", State = "NY", Zip = "10001", SourceUserId = user.Id, IsActive = true
            };
            db.ContractClients.Add(client);
            await db.SaveChangesAsync();
        }

        await using (var settingsScope = h.Scope())
        {
            var settings = await settingsScope.ServiceProvider
                .GetRequiredService<Services.Commercial.BillingSettingsService>().GetOrCreateAsync();
            settings.StripeCardEnabled = true;
            await settingsScope.ServiceProvider.GetRequiredService<Data.ApplicationDbContext>().SaveChangesAsync();
        }

        if (authorize)
        {
            await h.EnableAutoPayAsync(user.Id);
            await using var scope = h.Scope();
            await scope.ServiceProvider.GetRequiredService<IPaymentAuthorizationService>().AuthorizeAsync(user.Id, new AuthorizeArrangementDto
            {
                Scope = "client", ContractClientId = client.Id, AcceptTerms = true, TermsVersion = Helpers.Billing.AutoPayTerms.Version
            }, "127.0.0.1", "tests");
        }
        return (user, client);
    }

    private async Task<CommercialInvoice> AddInvoice(BillingHarness h, int clientId, DateTime dueDate, decimal balance,
        InvoiceStatus status = InvoiceStatus.Sent)
    {
        await using var db = h.NewContext();
        var invoice = new CommercialInvoice
        {
            InvoiceNumber = "DCI-TEST-" + Guid.NewGuid().ToString("N")[..8],
            PublicToken = Guid.NewGuid().ToString("N"),
            ContractClientId = clientId,
            CreatedByUserId = (await db.ContractClients.AsNoTracking().Where(c => c.Id == clientId).Select(c => c.SourceUserId).FirstAsync())!.Value,
            Status = status,
            InvoiceDate = dueDate.AddDays(-15),
            DueDate = dueDate,
            SubTotal = balance,
            Total = balance,
            BalanceDue = balance,
            Currency = "USD",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.CommercialInvoices.Add(invoice);
        await db.SaveChangesAsync();
        return invoice;
    }

    private static async Task RunCommercialSweep(BillingHarness h)
    {
        await using var scope = h.Scope();
        await scope.ServiceProvider.GetRequiredService<CommercialAutoPaySweep>().RunAsync(CancellationToken.None);
    }

    [MariaDbFact]
    public async Task CommercialAutoPay_ChargesOnTheDueDate_OncePerInvoice_AndRecordsItInTheLedger()
    {
        var h = Harness();
        var (_, client) = await CommercialSetup(h);
        var due = await AddInvoice(h, client.Id, DateTime.UtcNow.Date, 925.43m);
        var notYet = await AddInvoice(h, client.Id, DateTime.UtcNow.Date.AddDays(10), 400m);

        await RunCommercialSweep(h);
        await RunCommercialSweep(h);

        Assert.Single(h.Stripe.SucceededCharges);
        await using var db = h.NewContext();
        var paid = await db.CommercialInvoices.AsNoTracking().FirstAsync(i => i.Id == due.Id);
        Assert.Equal(InvoiceStatus.Paid, paid.Status);
        var payment = Assert.Single(await db.CommercialInvoicePayments.AsNoTracking().Where(p => p.CommercialInvoiceId == due.Id).ToListAsync());
        Assert.Equal(925.43m, payment.Amount);
        Assert.Equal(InvoicePaymentRecordMethod.Card, payment.PaymentMethod);
        Assert.Equal(InvoiceStatus.Sent, (await db.CommercialInvoices.AsNoTracking().FirstAsync(i => i.Id == notYet.Id)).Status);
    }

    [MariaDbFact]
    public async Task CommercialAutoPay_NeverChargesAnInvoiceDueBeforeTheAuthorisation()
    {
        var h = Harness();
        var (_, client) = await CommercialSetup(h);
        await AddInvoice(h, client.Id, DateTime.UtcNow.Date.AddDays(-20), 300m, InvoiceStatus.Overdue);

        await RunCommercialSweep(h);
        Assert.Empty(h.Stripe.ChargeCalls);
    }

    [MariaDbFact]
    public async Task CommercialAutoPay_WaitsWhileABankPaymentIsSettling()
    {
        var h = Harness();
        var (_, client) = await CommercialSetup(h);
        var invoice = await AddInvoice(h, client.Id, DateTime.UtcNow.Date, 500m);
        await using (var db = h.NewContext())
        {
            db.CommercialInvoicePaymentAttempts.Add(new CommercialInvoicePaymentAttempt
            {
                CommercialInvoiceId = invoice.Id, Amount = 500m, TotalCharged = 505m, ProcessingFee = 5m,
                PaymentMethod = InvoicePaymentRecordMethod.AchBankTransfer, Status = InvoicePaymentAttemptStatus.Processing
            });
            await db.SaveChangesAsync();
        }

        await RunCommercialSweep(h);
        Assert.Empty(h.Stripe.ChargeCalls);
    }

    [MariaDbFact]
    public async Task AnUnlinkedAccount_CannotPayAnotherCompanysInvoice()
    {
        var h = Harness();
        var (_, client) = await CommercialSetup(h, authorize: false);
        var invoice = await AddInvoice(h, client.Id, DateTime.UtcNow.Date, 200m);
        var stranger = await h.AddUserAsync();
        var strangerCard = await h.AddCardAsync(stranger.Id, CardBehaviour.Succeed);

        await using var scope = h.Scope();
        var result = await scope.ServiceProvider.GetRequiredService<ISavedCardChargeService>().ChargeInvoiceAsync(
            invoice.Id, stranger.Id, BillingAttemptTrigger.CustomerSavedCard, strangerCard, stranger.Id);
        Assert.Equal("blocked", result.Result);
        Assert.Empty(h.Stripe.ChargeCalls);
    }

    [MariaDbFact]
    public async Task AnOpenCheckoutSession_IsExpiredBeforeASavedCardPaysTheInvoice()
    {
        var h = Harness();
        var (user, client) = await CommercialSetup(h, authorize: false);
        var invoice = await AddInvoice(h, client.Id, DateTime.UtcNow.Date.AddDays(5), 200m);
        await using (var db = h.NewContext())
        {
            db.CommercialInvoicePaymentAttempts.Add(new CommercialInvoicePaymentAttempt
            {
                CommercialInvoiceId = invoice.Id, Amount = 200m, TotalCharged = 200m, PaymentMethod = InvoicePaymentRecordMethod.Card,
                Status = InvoicePaymentAttemptStatus.CheckoutOpen, StripeCheckoutSessionId = "cs_open_1"
            });
            await db.SaveChangesAsync();
        }
        h.Stripe.Sessions["cs_open_1"] = new Services.Interfaces.CheckoutSessionState { Status = "open", PaymentStatus = "unpaid" };

        int cardId;
        await using (var db = h.NewContext())
            cardId = (await db.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id)).PrimaryPaymentMethodId!.Value;

        await using var scope = h.Scope();
        var result = await scope.ServiceProvider.GetRequiredService<ISavedCardChargeService>().ChargeInvoiceAsync(
            invoice.Id, user.Id, BillingAttemptTrigger.CustomerSavedCard, cardId, user.Id);

        Assert.Equal("paid", result.Result);
        Assert.Contains("cs_open_1", h.Stripe.ExpiredSessions);
    }

    // ═══ History ═══════════════════════════════════════════════════════════════════════════════

    [MariaDbFact]
    public async Task History_ListsEachPaymentOnce_WithFailedAttemptsAlongside()
    {
        var h = Harness();
        var user = await h.AddUserAsync();
        await h.AddCardAsync(user.Id, CardBehaviour.Decline, "0002");
        var backup = await h.AddCardAsync(user.Id, CardBehaviour.Succeed, "4242");
        await h.SetBackupAsync(user.Id, backup);
        await h.AuthorizeOfficeAsync(user.Id, allowBackup: true);
        var order = await h.AddOrderAsync(user.Id, 250m);
        await h.AdminChargeAsync(order.Id);

        await using var scope = h.Scope();
        var page = await scope.ServiceProvider.GetRequiredService<IBillingHistoryService>().GetHistoryAsync(user.Id, 1, 20);

        Assert.Single(page.Items, i => i.Status == "Paid" && i.OrderId == order.Id);
        Assert.Single(page.Items, i => i.Status == "Failed" && i.Kind == "attempt");
        Assert.Equal(250m, page.Items.Single(i => i.Status == "Paid").Amount);
    }
}
