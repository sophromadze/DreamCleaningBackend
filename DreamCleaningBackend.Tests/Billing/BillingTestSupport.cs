using System.Collections.Concurrent;
using System.Reflection;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Models.Billing;
using DreamCleaningBackend.Services;
using DreamCleaningBackend.Services.Billing;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Stripe;
using PaymentMethod = Stripe.PaymentMethod;
using Xunit;

namespace DreamCleaningBackend.Tests.Billing;

/// <summary>
/// A [Fact] that runs only when <c>DC_TEST_MARIADB</c> names a LOCAL MariaDB server
/// (connection string WITHOUT a database). Each fixture creates its own throwaway database, runs
/// every migration into it, and drops it afterwards — it never touches an existing database.
/// </summary>
public sealed class MariaDbFactAttribute : FactAttribute
{
    public MariaDbFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DC_TEST_MARIADB")))
            Skip = "Set DC_TEST_MARIADB to a local MariaDB connection string (no Database=) to run the billing integration tests.";
    }
}

/// <summary>A throwaway, fully migrated MariaDB database for one test class.</summary>
public sealed class MariaDbDatabase : IAsyncLifetime
{
    public string ConnectionString { get; private set; } = string.Empty;
    public string DatabaseName { get; } = "dc_billing_it_" + Guid.NewGuid().ToString("N")[..10];
    public bool Available { get; private set; }

    public async Task InitializeAsync()
    {
        var server = Environment.GetEnvironmentVariable("DC_TEST_MARIADB");
        if (string.IsNullOrWhiteSpace(server)) return;

        var builder = new MySqlConnector.MySqlConnectionStringBuilder(server);
        // Safety: a local server only, and never a database somebody named.
        if (!(builder.Server is "localhost" or "127.0.0.1"))
            throw new InvalidOperationException("DC_TEST_MARIADB must point at localhost.");
        builder.Database = DatabaseName;
        ConnectionString = builder.ConnectionString;

        // Built from the CURRENT MODEL, not by replaying the migration chain: the chain cannot build
        // a database from nothing (20260208160000_AddAccountMergeRequestsAndUserSoftDelete has no
        // Designer file, so EF never applies it, and a later migration then reads the IsDeleted
        // column it would have added). The model IS what the new migration produces, including its
        // CHECK constraints and unique indexes, which are what these tests exercise.
        await using var db = CreateContext();
        await db.Database.EnsureCreatedAsync();
        Available = true;
    }

    public ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseMySql(ConnectionString, new MariaDbServerVersion(new Version(10, 9, 8)))
            .Options;
        return new ApplicationDbContext(options);
    }

    public async Task DisposeAsync()
    {
        if (string.IsNullOrEmpty(ConnectionString)) return;
        await using var db = CreateContext();
        await db.Database.EnsureDeletedAsync();
    }
}

/// <summary>Thread-safe audit recorder (the shared RecordingAuditService is not).</summary>
public sealed class ConcurrentAudit : IAuditService
{
    public ConcurrentQueue<(string EntityType, long EntityId, string Action, object? New)> Actions { get; } = new();

    public Task LogActionAsync(string entityType, long entityId, string action, object? oldValues, object? newValues,
        IEnumerable<string>? changedFields = null, int? actingUserId = null)
    {
        Actions.Enqueue((entityType, entityId, action, newValues));
        return Task.CompletedTask;
    }

    public Task LogCreateAsync<T>(T entity) where T : class => Task.CompletedTask;
    public Task LogUpdateAsync<T>(T originalEntity, T currentEntity) where T : class => Task.CompletedTask;
    public Task LogDeleteAsync<T>(T entity) where T : class => Task.CompletedTask;
    public Task<List<AuditLog>> GetEntityHistoryAsync(string entityType, long entityId) => Task.FromResult(new List<AuditLog>());
    public Task LogCleanerAssignmentAsync(int orderId, string cleanerEmail, string action, int adminId) => Task.CompletedTask;
    public Task LogOrderNotificationAsync(int orderId, string action, string details, int adminId) => Task.CompletedTask;
    public Task LogBubblePointsAdjustmentAsync(int targetUserId, string targetUserName, int points, string? reason, int adminId, string adminName) => Task.CompletedTask;
    public Task LogLoyaltyDiscountChangeAsync(int targetUserId, string action,
        decimal oldPercentage, bool oldIsManualOverride, DateTime? oldActivatedAt, DateTime? oldLastUsedAt,
        decimal newPercentage, bool newIsManualOverride, DateTime? newActivatedAt, DateTime? newLastUsedAt,
        int? adminId) => Task.CompletedTask;
    public Task UndoAsync(long auditLogId) => Task.CompletedTask;
    public Task RedoAsync(long auditLogId) => Task.CompletedTask;
}

/// <summary>What a scripted card does when charged.</summary>
public enum CardBehaviour { Succeed, Decline, RequireAction, Unknown, UnknownThenSucceed, Processing, NeverRetryDecline }

/// <summary>
/// A scripted Stripe that behaves like the real one where it matters for money:
///  • an idempotency key replays the stored response (and a network failure stores nothing);
///  • cancelling a succeeded intent throws, like Stripe;
///  • every charge that "moves money" is counted, so a test can assert "charged exactly once".
/// </summary>
public sealed class FakeStripe : IStripeService
{
    public ConcurrentDictionary<string, PaymentIntent> Intents { get; } = new();
    public ConcurrentDictionary<string, CardBehaviour> CardBehaviours { get; } = new();
    public ConcurrentDictionary<string, (string Customer, string Brand, string Last4)> PaymentMethods { get; } = new();
    public ConcurrentDictionary<string, SetupIntent> SetupIntents { get; } = new();
    public ConcurrentDictionary<string, CheckoutSessionState> Sessions { get; } = new();
    private readonly ConcurrentDictionary<string, SavedCardChargeResult> _idempotent = new();
    private readonly ConcurrentDictionary<string, int> _unknownCounts = new();

    public ConcurrentBag<string> SucceededCharges { get; } = new();
    public ConcurrentBag<string> ChargeCalls { get; } = new();
    public ConcurrentBag<string> Refunds { get; } = new();
    public ConcurrentBag<string> Detached { get; } = new();
    public ConcurrentBag<string> Cancelled { get; } = new();
    public ConcurrentBag<string> ExpiredSessions { get; } = new();

    /// <summary>Delay inside a charge, to widen race windows in concurrency tests.</summary>
    public TimeSpan ChargeDelay { get; set; } = TimeSpan.Zero;

    private int _seq;
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];
    private string NextId(string prefix) => $"{prefix}_fake_{_run}_{Interlocked.Increment(ref _seq)}";

    public string AddCard(string customerId, CardBehaviour behaviour, string brand = "visa", string last4 = "4242")
    {
        var id = NextId("pm");
        PaymentMethods[id] = (customerId, brand, last4);
        CardBehaviours[id] = behaviour;
        return id;
    }

    public async Task<SavedCardChargeResult> ChargeSavedCardAsync(SavedCardChargeRequest request)
    {
        ChargeCalls.Add(request.IdempotencyKey);
        if (ChargeDelay > TimeSpan.Zero) await Task.Delay(ChargeDelay);

        if (_idempotent.TryGetValue(request.IdempotencyKey, out var replay)) return replay;

        var behaviour = CardBehaviours.GetValueOrDefault(request.PaymentMethodId, CardBehaviour.Decline);
        if (behaviour == CardBehaviour.Unknown
            || (behaviour == CardBehaviour.UnknownThenSucceed && _unknownCounts.AddOrUpdate(request.IdempotencyKey, 1, (_, n) => n + 1) == 1))
        {
            // The request "never answered": nothing is stored against the key.
            return new SavedCardChargeResult { Outcome = SavedCardChargeOutcome.Unknown, FailureCode = "network_error" };
        }

        var intent = new PaymentIntent
        {
            Id = NextId("pi"),
            Amount = StripeService.ToCents(request.Amount),
            AmountReceived = 0,
            Currency = "usd",
            CustomerId = request.CustomerId,
            PaymentMethodId = request.PaymentMethodId,
            Metadata = new Dictionary<string, string>(request.Metadata),
            ClientSecret = "secret_" + Guid.NewGuid().ToString("N")
        };

        SavedCardChargeResult result;
        switch (behaviour)
        {
            case CardBehaviour.Succeed:
            case CardBehaviour.UnknownThenSucceed:
                intent.Status = "succeeded";
                intent.AmountReceived = intent.Amount;
                SucceededCharges.Add(intent.Id);
                result = new SavedCardChargeResult { Outcome = SavedCardChargeOutcome.Succeeded, PaymentIntentId = intent.Id };
                break;
            case CardBehaviour.Processing:
                intent.Status = "processing";
                result = new SavedCardChargeResult { Outcome = SavedCardChargeOutcome.Processing, PaymentIntentId = intent.Id };
                break;
            case CardBehaviour.RequireAction:
                intent.Status = "requires_action";
                result = new SavedCardChargeResult
                {
                    Outcome = SavedCardChargeOutcome.RequiresAction, PaymentIntentId = intent.Id,
                    ClientSecret = intent.ClientSecret, FailureCode = "authentication_required"
                };
                break;
            case CardBehaviour.NeverRetryDecline:
                intent.Status = "requires_payment_method";
                result = new SavedCardChargeResult
                {
                    Outcome = SavedCardChargeOutcome.Declined, PaymentIntentId = intent.Id,
                    FailureCode = "card_declined", DeclineCode = "stolen_card"
                };
                break;
            default:
                intent.Status = "requires_payment_method";
                result = new SavedCardChargeResult
                {
                    Outcome = SavedCardChargeOutcome.Declined, PaymentIntentId = intent.Id,
                    FailureCode = "card_declined", DeclineCode = "insufficient_funds"
                };
                break;
        }

        Intents[intent.Id] = intent;
        _idempotent[request.IdempotencyKey] = result;
        return result;
    }

    /// <summary>Seeds an intent a customer's browser would hold (payment page).</summary>
    public PaymentIntent AddCustomerIntent(int orderId, decimal amount, string status, string? customerId = null)
    {
        var intent = new PaymentIntent
        {
            Id = NextId("pi"),
            Amount = StripeService.ToCents(amount),
            AmountReceived = status == "succeeded" ? StripeService.ToCents(amount) : 0,
            Currency = "usd",
            Status = status,
            CustomerId = customerId,
            Metadata = new Dictionary<string, string> { ["type"] = "booking", ["orderId"] = orderId.ToString() },
            ClientSecret = "secret_" + Guid.NewGuid().ToString("N")
        };
        Intents[intent.Id] = intent;
        return intent;
    }

    public Task<PaymentIntent> GetPaymentIntentAsync(string paymentIntentId) =>
        Intents.TryGetValue(paymentIntentId, out var i)
            ? Task.FromResult(i)
            : throw new ApplicationException($"Payment retrieval error: No such payment_intent: '{paymentIntentId}'");

    public Task<PaymentIntent> CancelPaymentIntentAsync(string paymentIntentId)
    {
        var intent = Intents[paymentIntentId];
        lock (intent)
        {
            if (intent.Status is "succeeded" or "processing")
                throw new StripeException(System.Net.HttpStatusCode.BadRequest,
                    new StripeError { Type = "invalid_request_error", Code = "payment_intent_unexpected_state" },
                    "You cannot cancel this PaymentIntent because it has a status of " + intent.Status);
            intent.Status = "canceled";
        }
        Cancelled.Add(paymentIntentId);
        return Task.FromResult(intent);
    }

    /// <summary>The customer's browser confirming an intent (fails once it is canceled).</summary>
    public bool CustomerConfirms(string paymentIntentId)
    {
        var intent = Intents[paymentIntentId];
        lock (intent)
        {
            if (intent.Status is "canceled" or "succeeded") return false;
            intent.Status = "succeeded";
            intent.AmountReceived = intent.Amount;
            SucceededCharges.Add(intent.Id);
            return true;
        }
    }

    public Task<Refund> CreateRefundAsync(string paymentIntentId, decimal? amount = null, string idempotencyKey = null!,
        Dictionary<string, string> metadata = null!)
    {
        Refunds.Add(paymentIntentId);
        if (Intents.TryGetValue(paymentIntentId, out var intent))
            RefundedCents.AddOrUpdate(paymentIntentId, amount.HasValue ? StripeService.ToCents(amount.Value) : intent.AmountReceived,
                (_, cents) => cents + (amount.HasValue ? StripeService.ToCents(amount.Value) : intent.AmountReceived));
        return Task.FromResult(new Refund { Id = NextId("re"), PaymentIntentId = paymentIntentId, Status = "succeeded" });
    }

    /// <summary>Cents refunded per intent, for the refund-state lookup.</summary>
    public ConcurrentDictionary<string, long> RefundedCents { get; } = new();

    /// <summary>Every PaymentIntent the code under test created (the browser-confirmed kind).</summary>
    public ConcurrentBag<string> CreatedIntents { get; } = new();

    public Task<PaymentMethod> GetPaymentMethodAsync(string paymentMethodId)
    {
        if (!PaymentMethods.TryGetValue(paymentMethodId, out var pm))
            throw new ApplicationException("Payment method retrieval error: No such PaymentMethod");
        return Task.FromResult(new PaymentMethod
        {
            Id = paymentMethodId,
            Type = "card",
            CustomerId = Detached.Contains(paymentMethodId) ? null : pm.Customer,
            Card = new PaymentMethodCard { Brand = pm.Brand, Last4 = pm.Last4, ExpMonth = 12, ExpYear = DateTime.UtcNow.Year + 3, Funding = "credit" }
        });
    }

    public Task DetachPaymentMethodAsync(string paymentMethodId)
    {
        Detached.Add(paymentMethodId);
        return Task.CompletedTask;
    }

    /// <summary>Cards Stripe reports as attached: everything created for that customer and not
    /// detached since. Mirrors what the real customer-scoped listing returns.</summary>
    public Task<List<PaymentMethod>> ListCustomerCardsAsync(string stripeCustomerId) =>
        Task.FromResult(PaymentMethods
            .Where(kv => kv.Value.Customer == stripeCustomerId && !Detached.Contains(kv.Key))
            .Select(kv => new PaymentMethod
            {
                Id = kv.Key,
                Type = "card",
                CustomerId = kv.Value.Customer,
                Card = new PaymentMethodCard { Brand = kv.Value.Brand, Last4 = kv.Value.Last4, ExpMonth = 12, ExpYear = DateTime.UtcNow.Year + 3, Funding = "credit" }
            })
            .ToList());

    /// <summary>Consent evidence, the two shapes the real one looks for: a succeeded SetupIntent
    /// for the card, or a payment on it confirmed with setup_future_usage.</summary>
    public Task<string?> FindCardSaveConsentAsync(string stripeCustomerId, string paymentMethodId)
    {
        if (SetupIntents.Values.Any(si => si.CustomerId == stripeCustomerId
                                          && si.PaymentMethodId == paymentMethodId && si.Status == "succeeded"))
            return Task.FromResult<string?>("setup_intent");

        var paid = Intents.Values.Any(i => i.CustomerId == stripeCustomerId
                                           && i.PaymentMethodId == paymentMethodId
                                           && string.Equals(i.SetupFutureUsage, "off_session", StringComparison.OrdinalIgnoreCase)
                                           && i.Status is "succeeded" or "processing");
        return Task.FromResult<string?>(paid ? "payment_intent" : null);
    }

    public Task<string> CreateOrGetCustomerAsync(User user)
    {
        user.StripeCustomerId ??= $"cus_fake_{user.Id}";
        return Task.FromResult(user.StripeCustomerId);
    }

    public Task<SetupIntent> CreateSetupIntentAsync(string stripeCustomerId, Dictionary<string, string>? metadata = null)
    {
        var si = new SetupIntent { Id = NextId("seti"), CustomerId = stripeCustomerId, Status = "requires_payment_method", ClientSecret = "seti_secret" };
        SetupIntents[si.Id] = si;
        return Task.FromResult(si);
    }

    /// <summary>What the browser's confirmCardSetup does: attach a card to the SetupIntent.</summary>
    public string CompleteSetup(string setupIntentId, CardBehaviour behaviour, string last4 = "4242")
    {
        var si = SetupIntents[setupIntentId];
        var pm = AddCard(si.CustomerId, behaviour, last4: last4);
        si.PaymentMethodId = pm;
        si.Status = "succeeded";
        return pm;
    }

    public Task<SetupIntent> GetSetupIntentAsync(string setupIntentId) => Task.FromResult(SetupIntents[setupIntentId]);

    public Task<PaymentIntent?> FindPaymentIntentByMetadataAsync(string key, string value) =>
        Task.FromResult(Intents.Values.FirstOrDefault(i => i.Metadata != null && i.Metadata.GetValueOrDefault(key) == value));

    public Task<CheckoutSessionState?> GetCheckoutSessionStateAsync(string sessionId) =>
        Task.FromResult(Sessions.TryGetValue(sessionId, out var s) ? s : null);

    public Task<bool> ExpireCheckoutSessionAsync(string sessionId)
    {
        if (!Sessions.TryGetValue(sessionId, out var s) || s.Status != "open") return Task.FromResult(false);
        s.Status = "expired";
        ExpiredSessions.Add(sessionId);
        return Task.FromResult(true);
    }

    /// <summary>An intent the customer's browser will confirm ("Pay all upcoming", payment pages).</summary>
    public Task<PaymentIntent> CreatePaymentIntentAsync(decimal amount, Dictionary<string, string> metadata = null!, string receiptEmail = null!,
        string customerId = null!, bool saveCardForOffSession = false, string idempotencyKey = null!)
    {
        var intent = new PaymentIntent
        {
            Id = NextId("pi"),
            Amount = StripeService.ToCents(amount),
            Currency = "usd",
            Status = "requires_payment_method",
            CustomerId = customerId,
            Metadata = metadata == null ? new Dictionary<string, string>() : new Dictionary<string, string>(metadata),
            ClientSecret = "secret_" + Guid.NewGuid().ToString("N")
        };
        Intents[intent.Id] = intent;
        CreatedIntents.Add(intent.Id);
        return Task.FromResult(intent);
    }

    public Task<ChargeRefundState> GetChargeRefundStateAsync(string paymentIntentId)
    {
        if (!Intents.TryGetValue(paymentIntentId, out var intent) || intent.Status != "succeeded")
            return Task.FromResult(new ChargeRefundState { IsRefundable = false, UnavailableReason = "Not settled" });
        return Task.FromResult(new ChargeRefundState
        {
            IsRefundable = true,
            AmountReceived = intent.AmountReceived / 100m,
            AmountRefunded = RefundedCents.GetValueOrDefault(paymentIntentId) / 100m
        });
    }

    // ── Not used by the billing paths ──
    public Task<PaymentIntent> ConfirmPaymentIntentAsync(string paymentIntentId) => throw new NotSupportedException();
    public Task UpdatePaymentIntentMetadataAsync(string paymentIntentId, Dictionary<string, string> metadata) => Task.CompletedTask;
}

/// <summary>Records every email/SMS; can be told to fail.</summary>
public sealed class FakeMessaging
{
    public ConcurrentBag<(string To, string Subject)> Emails { get; } = new();
    public ConcurrentBag<(string To, string Body)> Sms { get; } = new();
    public bool FailEmail { get; set; }
    public bool FailSms { get; set; }

    public IEmailService Email => RecurringDiscountRegressionTests.Stub<IEmailService>((method, args) =>
    {
        if (FailEmail) return Task.FromException(new InvalidOperationException("SMTP down"));
        Emails.Add(((string)args![0]!, args.Length > 1 ? args[1]?.ToString() ?? "" : ""));
        return Task.CompletedTask;
    });

    public ISmsService SmsService => RecurringDiscountRegressionTests.Stub<ISmsService>((method, args) =>
    {
        if (method.Name == nameof(ISmsService.IsSmsEnabled)) return true;
        if (FailSms) return Task.FromException(new InvalidOperationException("SMS provider down"));
        Sms.Add(((string)args![0]!, args.Length > 1 ? args[1]?.ToString() ?? "" : ""));
        return Task.CompletedTask;
    });
}

/// <summary>Builds the real billing services on a real database with the fakes above.</summary>
public sealed class BillingHarness
{
    public MariaDbDatabase Db { get; }
    public FakeStripe Stripe { get; } = new();
    public FakeMessaging Messaging { get; } = new();
    public ConcurrentAudit Audit { get; } = new();
    public ServiceProvider Provider { get; }

    /// <summary>The Stripe the services actually use — the fake, or a real test-mode one.</summary>
    public IStripeService StripeApi { get; }

    public BillingHarness(MariaDbDatabase db, bool savedCards = true, bool autoPay = true, IStripeService? realStripe = null)
    {
        StripeApi = realStripe ?? Stripe;
        Db = db;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Billing:SavedCardsEnabled"] = savedCards ? "true" : "false",
            ["Billing:AutoPayEnabled"] = autoPay ? "true" : "false",
            ["Frontend:Url"] = "https://test.local"
        }).Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddDbContext<ApplicationDbContext>(o =>
            o.UseMySql(db.ConnectionString, new MariaDbServerVersion(new Version(10, 9, 8))));
        services.AddSingleton<IStripeService>(StripeApi);
        services.AddSingleton<IAuditService>(Audit);
        services.AddSingleton(Messaging.Email);
        services.AddSingleton(Messaging.SmsService);
        services.AddSingleton<BillingFeatures>();
        services.AddScoped<IBillingNotificationService, BillingNotificationService>();
        services.AddScoped<IPaymentMethodService, DreamCleaningBackend.Services.Billing.PaymentMethodService>();
        services.AddScoped<IPaymentAuthorizationService, PaymentAuthorizationService>();
        services.AddScoped<ISavedCardChargeService, SavedCardChargeService>();
        services.AddScoped<IBillingHistoryService, BillingHistoryService>();
        services.AddScoped<CommercialAutoPaySweep>();
        services.AddSingleton(RecurringDiscountRegressionTests.Stub<ILoyaltyDiscountService>((m, a) => Task.CompletedTask));
        services.AddSingleton(RecurringDiscountRegressionTests.Stub<ISubscriptionService>((m, a) => Task.FromResult(true)));

        // The commercial ledger, real, so a saved-card invoice payment is recorded by the SAME
        // code a Stripe webhook uses.
        services.AddScoped<DreamCleaningBackend.Services.Commercial.BillingSettingsService>();
        services.AddScoped<DreamCleaningBackend.Services.Commercial.InvoiceStripePaymentService>();
        services.AddScoped<DreamCleaningBackend.Services.Commercial.InvoiceService>();
        services.AddScoped<DreamCleaningBackend.Services.Commercial.InvoiceOrderLinkService>();
        services.AddScoped<DreamCleaningBackend.Services.Commercial.InvoiceNumberService>();
        services.AddSingleton(RecurringDiscountRegressionTests.Stub<DreamCleaningBackend.Services.Interfaces.IOrderInvoiceAllocationService>(
            (m, a) => m.ReturnType == typeof(Task) ? Task.CompletedTask : null));

        Provider = services.BuildServiceProvider();
    }

    public AsyncServiceScope Scope() => Provider.CreateAsyncScope();

    public ApplicationDbContext NewContext() => Db.CreateContext();

    // ── Seeding ──────────────────────────────────────────────────────────────────────────────

    public async Task<User> AddUserAsync(string? phone = null)
    {
        await using var db = NewContext();
        var user = new User
        {
            FirstName = "Test",
            LastName = "Customer",
            Email = $"billing.{Guid.NewGuid():N}@example.com",
            Phone = phone ?? "212" + Random.Shared.Next(1000000, 9999999),
            Role = UserRole.Customer,
            IsActive = true,
            CanReceiveEmails = true,
            CanReceiveMessages = true,
            CreatedAt = DateTime.UtcNow
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        user.StripeCustomerId = $"cus_fake_{user.Id}";
        await db.SaveChangesAsync();
        return user;
    }

    /// <summary>Adds a card through the REAL SetupIntent flow; returns the card id.</summary>
    public async Task<int> AddCardAsync(int userId, CardBehaviour behaviour, string last4 = "4242")
    {
        await using var scope = Scope();
        var cards = scope.ServiceProvider.GetRequiredService<IPaymentMethodService>();
        var setup = await cards.CreateSetupIntentAsync(userId);
        Stripe.CompleteSetup(setup.SetupIntentId, behaviour, last4);
        var card = await cards.CompleteSetupIntentAsync(userId, setup.SetupIntentId);
        return card.Id;
    }

    public async Task<Order> AddOrderAsync(int userId, decimal total, string status = "Pending", decimal amountPaid = 0m,
        int? seriesId = null, DateTime? serviceDate = null, bool bookedByAdmin = true)
    {
        await using var db = NewContext();
        var order = new Order
        {
            UserId = userId,
            ServiceTypeId = await db.ServiceTypes.Select(s => s.Id).FirstAsync(),
            OrderDate = DateTime.UtcNow,
            ServiceDate = serviceDate ?? DateTime.UtcNow.Date.AddDays(3),
            ServiceTime = TimeSpan.FromHours(10),
            ContactFirstName = "Test",
            ContactLastName = "Customer",
            ContactEmail = "contact@example.com",
            ContactPhone = "2125550100",
            ServiceAddress = "1 Test St",
            City = "Manhattan",
            State = "New York",
            ZipCode = "10001",
            SubTotal = Math.Round(total / 1.08875m, 2),
            Tax = total - Math.Round(total / 1.08875m, 2),
            Total = total,
            AmountPaid = amountPaid,
            Status = status,
            PaymentMethod = DreamCleaningBackend.Models.PaymentMethod.Normal,
            RecurringSeriesId = seriesId,
            BookedByAdminUserId = bookedByAdmin ? userId : null,
            TotalDuration = 180,
            CreatedAt = DateTime.UtcNow
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    /// <summary>Authorises office-booked charges with the three consents, through the real service.</summary>
    public async Task AuthorizeOfficeAsync(int userId, bool allowBackup)
    {
        await using var scope = Scope();
        var auth = scope.ServiceProvider.GetRequiredService<IPaymentAuthorizationService>();
        await auth.AuthorizeAsync(userId, new DTOs.AuthorizeArrangementDto
        {
            Scope = "office", AllowBackupFallback = allowBackup, AcceptTerms = true,
            TermsVersion = DreamCleaningBackend.Helpers.Billing.AutoPayTerms.Version,
            SmsConsent = true, CancellationFeeConsent = true, TermsOfServiceConsent = true
        }, "127.0.0.1", "tests");
    }

    public async Task EnableAutoPayAsync(int userId)
    {
        await using var scope = Scope();
        await scope.ServiceProvider.GetRequiredService<IPaymentAuthorizationService>().EnableAutoPayAsync(userId,
            new DTOs.EnableAutoPayDto { AcceptTerms = true, TermsVersion = DreamCleaningBackend.Helpers.Billing.AutoPayTerms.Version },
            "127.0.0.1", "tests");
    }

    public async Task SetBackupAsync(int userId, int cardId)
    {
        await using var scope = Scope();
        await scope.ServiceProvider.GetRequiredService<IPaymentMethodService>().SetBackupAsync(userId, cardId);
    }

    public async Task<DTOs.SavedCardChargeResponseDto> AdminChargeAsync(int orderId, int adminId = 1)
    {
        await using var scope = Scope();
        return await scope.ServiceProvider.GetRequiredService<ISavedCardChargeService>()
            .ChargeOrderAsync(orderId, BillingAttemptTrigger.AdminCharge, adminId);
    }
}

/// <summary>
/// Stripe.net keeps its API key in ONE process-global (<c>StripeConfiguration.ApiKey</c>), and
/// <see cref="StripeService"/>'s constructor sets it. Every test class that constructs a real
/// StripeService joins this collection so none runs while another is mid-call — otherwise a test
/// building one with an empty config blanks the key under a live test-mode charge.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class StripeGlobalStateCollection
{
    public const string Name = "Stripe global state";
}
