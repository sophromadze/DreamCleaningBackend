using DreamCleaningBackend.Models;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models.Billing;
using DreamCleaningBackend.Models.Commercial;
using DreamCleaningBackend.Models.Contracts;
using DreamCleaningBackend.Services.Billing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamCleaningBackend.Tests.Billing;

/// <summary>
/// RECONCILING THE WALLET WITH STRIPE (2026-09).
///
/// The case that prompted it: a SetupIntent succeeded, Stripe attached the card — and the call
/// that would have recorded it here failed. The customer's card was then real at Stripe and
/// invisible in their Billing tab, and nothing would ever have adopted it.
///
/// What must hold while fixing that: a card is adopted only with EVIDENCE that this customer
/// authorised saving it, a card they REMOVED is finished off rather than resurrected, and a card
/// a payment still holds is left alone.
/// </summary>
public class SavedCardRecoveryTests : IClassFixture<MariaDbDatabase>
{
    private readonly MariaDbDatabase _db;
    public SavedCardRecoveryTests(MariaDbDatabase db) => _db = db;

    private static async Task<int> ReconcileAsync(BillingHarness h, int userId, bool force = true)
    {
        await using var scope = h.Scope();
        return await scope.ServiceProvider.GetRequiredService<IPaymentMethodService>()
            .ReconcileWithStripeAsync(userId, force);
    }

    private static async Task<List<CustomerPaymentMethod>> CardsAsync(BillingHarness h, int userId)
    {
        await using var db = h.NewContext();
        return await db.CustomerPaymentMethods.AsNoTracking().Where(c => c.UserId == userId).ToListAsync();
    }

    [MariaDbFact]
    public async Task ACardStripeAttachedButWeNeverRecorded_IsRecovered_AndBecomesUsable()
    {
        var h = new BillingHarness(_db);
        var user = await h.AddUserAsync();

        // Exactly what a half-finished "add card" leaves behind: a succeeded SetupIntent and an
        // attached card, with nothing on our side.
        var setup = await h.Stripe.CreateSetupIntentAsync(user.StripeCustomerId!);
        var orphan = h.Stripe.CompleteSetup(setup.Id, CardBehaviour.Succeed, last4: "4242");
        Assert.Empty(await CardsAsync(h, user.Id));

        Assert.Equal(1, await ReconcileAsync(h, user.Id));

        var recovered = Assert.Single(await CardsAsync(h, user.Id));
        Assert.Equal(orphan, recovered.StripePaymentMethodId);
        Assert.Equal(CustomerPaymentMethodStatus.Active, recovered.Status);
        await using var db = h.NewContext();
        // The first usable card is the Primary — the same rule an ordinary save follows.
        Assert.Equal(recovered.Id, (await db.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id)).PrimaryPaymentMethodId);
    }

    [MariaDbFact]
    public async Task RunningItAgain_RecoversNothingFurther_AndLeavesOneCard()
    {
        var h = new BillingHarness(_db);
        var user = await h.AddUserAsync();
        var setup = await h.Stripe.CreateSetupIntentAsync(user.StripeCustomerId!);
        h.Stripe.CompleteSetup(setup.Id, CardBehaviour.Succeed);

        Assert.Equal(1, await ReconcileAsync(h, user.Id));
        Assert.Equal(0, await ReconcileAsync(h, user.Id));
        Assert.Equal(0, await ReconcileAsync(h, user.Id));

        Assert.Single(await CardsAsync(h, user.Id));
    }

    [MariaDbFact]
    public async Task ACardWithNoSaveAuthorisation_IsNeverAdoptedIntoTheWallet()
    {
        var h = new BillingHarness(_db);
        var user = await h.AddUserAsync();

        // Attached, but with no SetupIntent and no payment that asked to save it.
        h.Stripe.AddCard(user.StripeCustomerId!, CardBehaviour.Succeed, last4: "1881");

        Assert.Equal(0, await ReconcileAsync(h, user.Id));

        Assert.Empty(await CardsAsync(h, user.Id));
        Assert.Empty(h.Stripe.Detached);   // and it is not ours to detach either
    }

    [MariaDbFact]
    public async Task ACardSavedBYAPaymentIsRecovered_WhenItsSaveChoiceIsOnTheIntent()
    {
        var h = new BillingHarness(_db);
        var user = await h.AddUserAsync();
        var pm = h.Stripe.AddCard(user.StripeCustomerId!, CardBehaviour.Succeed, last4: "4242");

        // The pre-payment modal's answer, as Stripe records it on the charge itself.
        var intent = h.Stripe.AddCustomerIntent(orderId: 1, amount: 141.54m, status: "succeeded", customerId: user.StripeCustomerId);
        intent.PaymentMethodId = pm;
        intent.SetupFutureUsage = "off_session";

        Assert.Equal(1, await ReconcileAsync(h, user.Id));

        var card = Assert.Single(await CardsAsync(h, user.Id));
        Assert.Equal(pm, card.StripePaymentMethodId);
    }

    [MariaDbFact]
    public async Task APaymentWithoutThatChoice_SavesNothing()
    {
        var h = new BillingHarness(_db);
        var user = await h.AddUserAsync();
        var pm = h.Stripe.AddCard(user.StripeCustomerId!, CardBehaviour.Succeed);

        var intent = h.Stripe.AddCustomerIntent(orderId: 1, amount: 141.54m, status: "succeeded", customerId: user.StripeCustomerId);
        intent.PaymentMethodId = pm;   // paid with it, but "Pay Without Saving"

        Assert.Equal(0, await ReconcileAsync(h, user.Id));
        Assert.Empty(await CardsAsync(h, user.Id));
    }

    [MariaDbFact]
    public async Task ARemovedCardStillAttachedAtStripe_IsDetached_NotResurrected()
    {
        var h = new BillingHarness(_db);
        var user = await h.AddUserAsync();
        var cardId = await h.AddCardAsync(user.Id, CardBehaviour.Succeed);

        string pm;
        await using (var db = h.NewContext())
        {
            var card = await db.CustomerPaymentMethods.FirstAsync(c => c.Id == cardId);
            pm = card.StripePaymentMethodId;
            // The customer removed it; the detach at Stripe never landed.
            card.Status = CustomerPaymentMethodStatus.Removed;
            var owner = await db.Users.FirstAsync(u => u.Id == user.Id);
            owner.PrimaryPaymentMethodId = null;
            await db.SaveChangesAsync();
        }

        Assert.Equal(0, await ReconcileAsync(h, user.Id));

        Assert.Contains(pm, h.Stripe.Detached);
        var row = Assert.Single(await CardsAsync(h, user.Id));
        Assert.Equal(CustomerPaymentMethodStatus.Removed, row.Status);   // their decision stands
    }

    [MariaDbFact]
    public async Task ARemovedCardAPaymentStillHolds_IsLeftAloneUntilThatPaymentIsDone()
    {
        var h = new BillingHarness(_db);
        var user = await h.AddUserAsync();
        var cardId = await h.AddCardAsync(user.Id, CardBehaviour.Succeed);

        await using (var db = h.NewContext())
        {
            var card = await db.CustomerPaymentMethods.FirstAsync(c => c.Id == cardId);
            card.Status = CustomerPaymentMethodStatus.Removed;
            var owner = await db.Users.FirstAsync(u => u.Id == user.Id);
            owner.PrimaryPaymentMethodId = null;
            db.BillingPaymentAttempts.Add(new BillingPaymentAttempt
            {
                UserId = user.Id, ObligationKey = "order:1", ObligationType = BillingObligationType.Order,
                RunKey = Guid.NewGuid().ToString("N"), Sequence = 1, Trigger = BillingAttemptTrigger.CustomerSavedCard,
                CardRole = BillingCardRole.Primary, Amount = 10m, IdempotencyKey = Guid.NewGuid().ToString("N"),
                Status = BillingAttemptStatus.Pending, ActiveLockKey = "order:1",
                StripePaymentMethodId = card.StripePaymentMethodId
            });
            await db.SaveChangesAsync();
        }

        await ReconcileAsync(h, user.Id);

        Assert.Empty(h.Stripe.Detached);
    }

    [MariaDbFact]
    public async Task OneCustomersOrphan_IsNeverAdoptedByAnother()
    {
        var h = new BillingHarness(_db);
        var mine = await h.AddUserAsync();
        var theirs = await h.AddUserAsync();

        var setup = await h.Stripe.CreateSetupIntentAsync(theirs.StripeCustomerId!);
        h.Stripe.CompleteSetup(setup.Id, CardBehaviour.Succeed);

        Assert.Equal(0, await ReconcileAsync(h, mine.Id));
        Assert.Empty(await CardsAsync(h, mine.Id));
        Assert.Equal(1, await ReconcileAsync(h, theirs.Id));
    }

    // ── Recording the modal's answer after the charge (POST cards/from-payment) ──
    //
    // The client says "the customer chose Save", but what it is BELIEVED on is Stripe's own record
    // of the intent — so a tampered call, a replay, or a mistaken order id saves nothing.

    private static async Task<SavedCardDto?> SaveFromIntentAsync(BillingHarness h, int userId, string intentId)
    {
        await using var scope = h.Scope();
        return await scope.ServiceProvider.GetRequiredService<IPaymentMethodService>()
            .SaveFromPaymentIntentAsync(userId, intentId);
    }

    [MariaDbFact]
    public async Task TheCardIsSavedFromTheCHARGE_AndSavingItTwiceLeavesOneCard()
    {
        var h = new BillingHarness(_db);
        var user = await h.AddUserAsync();
        var pm = h.Stripe.AddCard(user.StripeCustomerId!, CardBehaviour.Succeed, last4: "4242");
        var intent = h.Stripe.AddCustomerIntent(orderId: 1, amount: 141.54m, status: "succeeded", customerId: user.StripeCustomerId);
        intent.PaymentMethodId = pm;
        intent.SetupFutureUsage = "off_session";

        Assert.NotNull(await SaveFromIntentAsync(h, user.Id, intent.Id));
        // The browser retried, or a second tab reported the same payment.
        Assert.NotNull(await SaveFromIntentAsync(h, user.Id, intent.Id));

        var card = Assert.Single(await CardsAsync(h, user.Id));
        Assert.Equal(pm, card.StripePaymentMethodId);
    }

    [MariaDbFact]
    public async Task APaymentTheCustomerChoseNOTToSave_SavesNothingHoweverItIsReported()
    {
        var h = new BillingHarness(_db);
        var user = await h.AddUserAsync();
        var pm = h.Stripe.AddCard(user.StripeCustomerId!, CardBehaviour.Succeed);
        var intent = h.Stripe.AddCustomerIntent(orderId: 1, amount: 141.54m, status: "succeeded", customerId: user.StripeCustomerId);
        intent.PaymentMethodId = pm;   // "Pay Without Saving": no setup_future_usage on the intent

        Assert.Null(await SaveFromIntentAsync(h, user.Id, intent.Id));
        Assert.Empty(await CardsAsync(h, user.Id));
    }

    [MariaDbFact]
    public async Task SomebodyElsesPayment_NeverPutsTheirCardInMyWallet()
    {
        var h = new BillingHarness(_db);
        var mine = await h.AddUserAsync();
        var theirs = await h.AddUserAsync();
        var pm = h.Stripe.AddCard(theirs.StripeCustomerId!, CardBehaviour.Succeed);
        var intent = h.Stripe.AddCustomerIntent(orderId: 1, amount: 141.54m, status: "succeeded", customerId: theirs.StripeCustomerId);
        intent.PaymentMethodId = pm;
        intent.SetupFutureUsage = "off_session";

        Assert.Null(await SaveFromIntentAsync(h, mine.Id, intent.Id));
        Assert.Empty(await CardsAsync(h, mine.Id));
    }

    [MariaDbFact]
    public async Task SavingACard_NeverSwitchesAutoPayOn()
    {
        var h = new BillingHarness(_db);
        var user = await h.AddUserAsync();
        var pm = h.Stripe.AddCard(user.StripeCustomerId!, CardBehaviour.Succeed);
        var intent = h.Stripe.AddCustomerIntent(orderId: 1, amount: 141.54m, status: "succeeded", customerId: user.StripeCustomerId);
        intent.PaymentMethodId = pm;
        intent.SetupFutureUsage = "off_session";

        await SaveFromIntentAsync(h, user.Id, intent.Id);

        await using var db = h.NewContext();
        var auths = await db.PaymentAuthorizations.AsNoTracking().Where(a => a.UserId == user.Id).ToListAsync();
        Assert.DoesNotContain(auths, a => a.IsActive);
    }

    [MariaDbFact]
    public async Task ItIsThrottled_SoOpeningTheBillingTabIsNotAStripeCallEveryTime()
    {
        var h = new BillingHarness(_db);
        var user = await h.AddUserAsync();
        await ReconcileAsync(h, user.Id, force: true);   // stamps the throttle

        var setup = await h.Stripe.CreateSetupIntentAsync(user.StripeCustomerId!);
        h.Stripe.CompleteSetup(setup.Id, CardBehaviour.Succeed);

        Assert.Equal(0, await ReconcileAsync(h, user.Id, force: false));   // within the interval
        Assert.Empty(await CardsAsync(h, user.Id));

        Assert.Equal(1, await ReconcileAsync(h, user.Id, force: true));
    }
}

/// <summary>
/// Billing history ordering (2026-09). An invoice payment carries a DATE with no time, so it used
/// to sort at midnight — below every card payment made the same day, however much later it was
/// actually recorded.
/// </summary>
public class BillingHistoryOrderingTests : IClassFixture<MariaDbDatabase>
{
    private readonly MariaDbDatabase _db;
    public BillingHistoryOrderingTests(MariaDbDatabase db) => _db = db;

    [MariaDbFact]
    public async Task AnInvoicePaymentRecordedToday_SortsByWhenItWasRecorded_NotMidnight()
        => await AssertOrderingAsync(backDated: false);

    /// <summary>
    /// The other direction: an ACH credited last week and entered today keeps ITS OWN date, which
    /// is where the customer looks for it. Sorting everything by the recorded timestamp would drag
    /// a week-old payment to the top of the list.
    /// </summary>
    [MariaDbFact]
    public async Task ABackDatedPayment_KeepsTheDateItActuallyHappenedOn()
        => await AssertOrderingAsync(backDated: true);

    private async Task AssertOrderingAsync(bool backDated)
    {
        var h = new BillingHarness(_db);
        var user = await h.AddUserAsync();
        var order = await h.AddOrderAsync(user.Id, 150m);

        await using (var db = h.NewContext())
        {
            // An order paid earlier today.
            var paid = await db.Orders.FirstAsync(o => o.Id == order.Id);
            paid.IsPaid = true;
            paid.PaidAt = DateTime.UtcNow.AddHours(-3);
            paid.PaymentIntentId = "pi_history_" + Guid.NewGuid().ToString("N")[..10];
            paid.InitialTotal = 150m;

            var client = new ContractClient
            {
                LegalEntityName = "History Test LLC", SourceUserId = user.Id, IsActive = true,
                NoticeEmail = "history@example.com"
            };
            db.ContractClients.Add(client);
            await db.SaveChangesAsync();

            var invoice = new CommercialInvoice
            {
                InvoiceNumber = "DCI-TEST-" + Guid.NewGuid().ToString("N")[..8],
                PublicToken = Guid.NewGuid().ToString("N"),
                ContractClientId = client.Id,
                CreatedByUserId = user.Id,
                Status = InvoiceStatus.Paid,
                InvoiceDate = DateTime.UtcNow.Date.AddDays(-7),
                DueDate = DateTime.UtcNow.Date,
                SubTotal = 925.43m, Total = 925.43m, AmountPaid = 925.43m, BalanceDue = 0m,
                Currency = "usd"
            };
            db.CommercialInvoices.Add(invoice);
            await db.SaveChangesAsync();

            db.CommercialInvoicePayments.Add(new CommercialInvoicePayment
            {
                CommercialInvoiceId = invoice.Id,
                Amount = 925.43m,
                // The date alone, exactly as the invoice ledger stores it…
                PaymentDate = backDated
                    ? DreamCleaningBackend.Helpers.NyTimeHelper.NowNy.Date.AddDays(-7)
                    : DreamCleaningBackend.Helpers.NyTimeHelper.NowNy.Date,
                // …recorded just now, either way.
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        await using var scope = h.Scope();
        var history = await scope.ServiceProvider.GetRequiredService<IBillingHistoryService>()
            .GetHistoryAsync(user.Id, 1, 20);

        var invoiceRow = Assert.Single(history.Items.Where(i => i.Kind == "invoice"));
        var orderRow = Assert.Single(history.Items.Where(i => i.Kind == "order"));
        Assert.Equal(925.43m, invoiceRow.Amount);
        Assert.Equal(150m, orderRow.Amount);

        if (backDated)
            Assert.Equal(history.Items.First(), orderRow);       // last week's payment stays below
        else
            Assert.Equal(history.Items.First(), invoiceRow);     // entered after the order was paid
    }
}
