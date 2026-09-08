using System.Reflection;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs.Commercial;
using DreamCleaningBackend.Helpers.Commercial;
using DreamCleaningBackend.Models.Commercial;
using DreamCleaningBackend.Models.Contracts;
using DreamCleaningBackend.Services.Commercial;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.InMemory.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// ONLINE PAYMENT OF A COMMERCIAL INVOICE, BY STRIPE ACH.
    ///
    /// The rules here exist because ACH is ASYNCHRONOUS. A card charge either works or does not,
    /// within a second. An ACH debit is authorized on Monday and settles on Thursday, and for
    /// those three days the invoice is genuinely unpaid while the customer believes they have
    /// paid. Nearly every test below is about that gap.
    ///
    /// These run against the EF in-memory provider, the same approach the input-builder tests use.
    /// NO TEST TOUCHES STRIPE — creating a Checkout Session needs a live API key, so the tests
    /// cover the settlement half (webhook → ledger), which is where money is actually decided, and
    /// the validation half is asserted through the pure guards. No real bank details appear
    /// anywhere; every fixture is invented.
    /// </summary>
    public class CommercialStripePaymentTests
    {
        // ── Fixtures ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// An in-memory context with the transaction warning suppressed.
        ///
        /// <c>RecordSucceededAsync</c> wraps its work in a real transaction, which is correct and
        /// deliberate — payment row, totals, status and activity must commit together or not at
        /// all. The in-memory provider cannot do transactions and throws on
        /// <c>BeginTransactionAsync</c> unless told to ignore it.
        ///
        /// BE HONEST ABOUT WHAT THIS COSTS: suppressing the warning means these tests exercise the
        /// LOGIC inside the transaction but do NOT prove atomicity — a rollback is a no-op here.
        /// Atomicity is guaranteed by MariaDB in production and would need an integration test
        /// against a real database to assert. What these tests do prove is the part that actually
        /// goes wrong in practice: the arithmetic, the idempotency and the status transitions.
        /// </summary>
        private static ApplicationDbContext NewContext()
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase($"stripe-{Guid.NewGuid()}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            return new ApplicationDbContext(options);
        }

        private static InvoiceStripePaymentService NewService(ApplicationDbContext context)
        {
            var billing = new BillingSettingsService(context);
            var invoices = new InvoiceService(
                context,
                new InvoiceNumberService(context, NullLogger<InvoiceNumberService>.Instance),
                billing,
                new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
                NullLogger<InvoiceService>.Instance);

            return new InvoiceStripePaymentService(
                context, invoices, NullLogger<InvoiceStripePaymentService>.Instance);
        }

        /// <summary>An issued, unpaid invoice for $925.43. Fictional client; no real bank data.</summary>
        private static async Task<CommercialInvoice> SeedInvoiceAsync(
            ApplicationDbContext context, decimal total = 925.43m, decimal alreadyPaid = 0m)
        {
            var client = new ContractClient
            {
                LegalEntityName = "Test Commercial Client LLC",
                EntityType = "a limited liability company",
                PrincipalAddress = "1 Test Street",
                City = "Brooklyn",
                State = "NY",
                Zip = "11226"
            };
            context.ContractClients.Add(client);
            await context.SaveChangesAsync();

            var invoice = new CommercialInvoice
            {
                InvoiceNumber = "DCI-2026-10713354",
                PublicToken = "aaaabbbbccccddddeeeeffff0000111122223333",
                ContractClientId = client.Id,
                Status = InvoiceStatus.Sent,
                InvoiceDate = new DateTime(2026, 9, 7),
                DueDate = new DateTime(2026, 9, 22),
                SubTotal = total,
                Total = total,
                AmountPaid = alreadyPaid,
                BalanceDue = total - alreadyPaid,
                Currency = "USD",
                FirstSentAt = new DateTime(2026, 9, 7),
                CreatedByUserId = 1
            };
            context.CommercialInvoices.Add(invoice);
            await context.SaveChangesAsync();
            return invoice;
        }

        private static async Task<CommercialInvoicePaymentAttempt> SeedAttemptAsync(
            ApplicationDbContext context, CommercialInvoice invoice, string paymentIntentId,
            decimal amount, InvoicePaymentAttemptStatus status = InvoicePaymentAttemptStatus.Processing)
        {
            var attempt = new CommercialInvoicePaymentAttempt
            {
                CommercialInvoiceId = invoice.Id,
                Provider = InvoicePaymentProvider.Stripe,
                PaymentMethod = InvoicePaymentRecordMethod.AchBankTransfer,
                Amount = amount,
                Currency = "USD",
                StripeCheckoutSessionId = "cs_test_" + Guid.NewGuid().ToString("N")[..12],
                StripePaymentIntentId = paymentIntentId,
                Status = status
            };
            context.CommercialInvoicePaymentAttempts.Add(attempt);
            await context.SaveChangesAsync();
            return attempt;
        }

        private static Dictionary<string, string> Metadata(CommercialInvoice invoice) => new()
        {
            ["type"] = StripeCommercialInvoiceMetadata.TypeValue,
            [StripeCommercialInvoiceMetadata.InvoiceIdKey] = invoice.Id.ToString(),
            [StripeCommercialInvoiceMetadata.InvoiceNumberKey] = invoice.InvoiceNumber,
            [StripeCommercialInvoiceMetadata.ClientIdKey] = invoice.ContractClientId.ToString()
        };

        // ── Amount conversion ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// $925.43 must reach Stripe as 92543. Via decimal, because `(long)(925.43d * 100)` is
        /// 92542 in binary floating point — an undercharge of a cent on a real invoice.
        /// </summary>
        [Fact]
        public void DollarsConvertToIntegerCentsWithoutFloatingPointDrift()
        {
            Assert.Equal(92543L, InvoiceCheckoutService.ToCents(925.43m));
            Assert.Equal(300000L, InvoiceCheckoutService.ToCents(3000.00m));
            Assert.Equal(1L, InvoiceCheckoutService.ToCents(0.01m));
            Assert.Equal(115L, InvoiceCheckoutService.ToCents(1.15m));
            Assert.Equal(2029L, InvoiceCheckoutService.ToCents(20.29m));
        }

        // ── The success path ──────────────────────────────────────────────────────────────────

        [Fact]
        public async Task ASucceededPaymentIntentRecordsExactlyOnePaymentAndPaysTheInvoice()
        {
            using var context = NewContext();
            var invoice = await SeedInvoiceAsync(context);
            await SeedAttemptAsync(context, invoice, "pi_test_success", 925.43m);

            var service = NewService(context);
            var result = await service.RecordSucceededAsync(
                "pi_test_success", 92543, "usd", "ch_test_1", Metadata(invoice), "Test Bank ••••6789");

            Assert.True(result.Recorded);
            Assert.True(result.InvoiceBecamePaid);
            Assert.False(result.Overpaid);

            var payments = await context.CommercialInvoicePayments.ToListAsync();
            Assert.Single(payments);
            Assert.Equal(925.43m, payments[0].Amount);
            Assert.Equal(InvoicePaymentProvider.Stripe, payments[0].Provider);
            Assert.Equal("pi_test_success", payments[0].StripePaymentIntentId);

            // No admin behind a webhook payment; it names itself instead.
            Assert.Null(payments[0].RecordedByUserId);
            Assert.Equal("Stripe webhook", payments[0].RecordedByLabel);

            var reloaded = await context.CommercialInvoices.FirstAsync(i => i.Id == invoice.Id);
            Assert.Equal(InvoiceStatus.Paid, reloaded.Status);
            Assert.Equal(0m, reloaded.BalanceDue);
            Assert.NotNull(reloaded.PaidAt);

            var attempt = await context.CommercialInvoicePaymentAttempts.FirstAsync();
            Assert.Equal(InvoicePaymentAttemptStatus.Succeeded, attempt.Status);
            Assert.NotNull(attempt.CompletedAt);
        }

        /// <summary>
        /// IDEMPOTENCY. Stripe retries webhook deliveries; a second delivery of the same event must
        /// not bank the money twice. This is the single most expensive bug this feature could have.
        /// </summary>
        [Fact]
        public async Task ARetriedWebhookDeliveryNeverCreatesASecondPayment()
        {
            using var context = NewContext();
            var invoice = await SeedInvoiceAsync(context);
            await SeedAttemptAsync(context, invoice, "pi_test_retry", 925.43m);
            var service = NewService(context);

            var first = await service.RecordSucceededAsync(
                "pi_test_retry", 92543, "usd", "ch_1", Metadata(invoice), null);
            var second = await service.RecordSucceededAsync(
                "pi_test_retry", 92543, "usd", "ch_1", Metadata(invoice), null);
            var third = await service.RecordSucceededAsync(
                "pi_test_retry", 92543, "usd", "ch_1", Metadata(invoice), null);

            Assert.True(first.Recorded);
            Assert.True(second.AlreadyProcessed);
            Assert.False(second.Recorded);
            Assert.True(third.AlreadyProcessed);

            Assert.Equal(1, await context.CommercialInvoicePayments.CountAsync());

            var reloaded = await context.CommercialInvoices.FirstAsync(i => i.Id == invoice.Id);
            Assert.Equal(925.43m, reloaded.AmountPaid);
            Assert.Equal(0m, reloaded.BalanceDue);
        }

        /// <summary>
        /// A receipt is only triggered by the delivery that ACTUALLY recorded the payment, so a
        /// retry cannot email the customer a second confirmation.
        /// </summary>
        [Fact]
        public async Task OnlyTheRecordingDeliveryReportsThatTheInvoiceBecamePaid()
        {
            using var context = NewContext();
            var invoice = await SeedInvoiceAsync(context);
            await SeedAttemptAsync(context, invoice, "pi_test_once", 925.43m);
            var service = NewService(context);

            var first = await service.RecordSucceededAsync(
                "pi_test_once", 92543, "usd", null, Metadata(invoice), null);
            var retry = await service.RecordSucceededAsync(
                "pi_test_once", 92543, "usd", null, Metadata(invoice), null);

            Assert.True(first.InvoiceBecamePaid);
            Assert.False(retry.InvoiceBecamePaid);
        }

        // ── Partial payments ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// An invoice part-paid by hand is charged only its REMAINING balance online, and settling
        /// that leaves it exactly paid — not overpaid, and not still owing.
        /// </summary>
        [Fact]
        public async Task StripeSettlesOnlyTheRemainingBalanceOnAPartlyPaidInvoice()
        {
            using var context = NewContext();

            // $5,000 invoice with $2,000 already recorded manually.
            var invoice = await SeedInvoiceAsync(context, total: 5000m, alreadyPaid: 2000m);
            context.CommercialInvoicePayments.Add(new CommercialInvoicePayment
            {
                CommercialInvoiceId = invoice.Id,
                Amount = 2000m,
                PaymentDate = new DateTime(2026, 9, 8),
                PaymentMethod = InvoicePaymentRecordMethod.Check,
                Provider = InvoicePaymentProvider.Manual,
                RecordedByUserId = 1,
                CreatedAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();

            Assert.Equal(3000m, invoice.BalanceDue);

            await SeedAttemptAsync(context, invoice, "pi_test_partial", 3000m);
            var service = NewService(context);

            var result = await service.RecordSucceededAsync(
                "pi_test_partial", 300000, "usd", null, Metadata(invoice), null);

            Assert.True(result.Recorded);
            Assert.False(result.Overpaid);

            var reloaded = await context.CommercialInvoices.FirstAsync(i => i.Id == invoice.Id);
            Assert.Equal(5000m, reloaded.AmountPaid);
            Assert.Equal(0m, reloaded.BalanceDue);
            Assert.Equal(InvoiceStatus.Paid, reloaded.Status);

            // Both payments survive: the manual one is not replaced by the online one.
            Assert.Equal(2, await context.CommercialInvoicePayments.CountAsync());
        }

        /// <summary>
        /// A settlement that only covers part of the balance leaves the invoice Partially Paid —
        /// the arithmetic decides, exactly as it does for a manual payment.
        /// </summary>
        [Fact]
        public async Task APartialSettlementLeavesTheInvoicePartiallyPaid()
        {
            using var context = NewContext();
            var invoice = await SeedInvoiceAsync(context, total: 1000m);
            await SeedAttemptAsync(context, invoice, "pi_test_half", 400m);
            var service = NewService(context);

            await service.RecordSucceededAsync(
                "pi_test_half", 40000, "usd", null, Metadata(invoice), null);

            var reloaded = await context.CommercialInvoices.FirstAsync(i => i.Id == invoice.Id);
            Assert.Equal(400m, reloaded.AmountPaid);
            Assert.Equal(600m, reloaded.BalanceDue);
            Assert.Equal(InvoiceStatus.PartiallyPaid, reloaded.Status);
            Assert.Null(reloaded.PaidAt);
        }

        // ── Concurrency ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// CONCURRENCY: an admin records a manual payment while an ACH settlement is landing.
        ///
        /// The Stripe money genuinely arrived, so it is RECORDED rather than discarded, and the
        /// resulting overpayment is FLAGGED for a human to refund. Throwing the settlement away to
        /// keep the arithmetic tidy would lose real money; silently accepting it would hide a
        /// double payment the customer will notice before we do.
        /// </summary>
        [Fact]
        public async Task AConcurrentManualPaymentProducesAFlaggedOverpaymentRatherThanLostMoney()
        {
            using var context = NewContext();
            var invoice = await SeedInvoiceAsync(context);
            await SeedAttemptAsync(context, invoice, "pi_test_race", 925.43m);

            // The admin records the same money by hand while the ACH debit is in flight.
            context.CommercialInvoicePayments.Add(new CommercialInvoicePayment
            {
                CommercialInvoiceId = invoice.Id,
                Amount = 925.43m,
                PaymentDate = DateTime.UtcNow.Date,
                PaymentMethod = InvoicePaymentRecordMethod.AchBankTransfer,
                Provider = InvoicePaymentProvider.Manual,
                RecordedByUserId = 1,
                CreatedAt = DateTime.UtcNow
            });
            invoice.AmountPaid = 925.43m;
            invoice.BalanceDue = 0m;
            invoice.Status = InvoiceStatus.Paid;
            await context.SaveChangesAsync();

            var service = NewService(context);
            var result = await service.RecordSucceededAsync(
                "pi_test_race", 92543, "usd", null, Metadata(invoice), null);

            Assert.True(result.Recorded);
            Assert.True(result.Overpaid);

            // Both rows exist — the Stripe settlement was not discarded.
            Assert.Equal(2, await context.CommercialInvoicePayments.CountAsync());

            var reloaded = await context.CommercialInvoices.FirstAsync(i => i.Id == invoice.Id);
            Assert.Equal(1850.86m, reloaded.AmountPaid);
            // Balance is floored at zero rather than going negative.
            Assert.Equal(0m, reloaded.BalanceDue);

            // And a human is told, in the invoice's own timeline.
            var flagged = await context.CommercialInvoiceActivityLogs
                .AnyAsync(a => a.Action == "payment_overpaid");
            Assert.True(flagged, "An overpayment must be flagged for admin review.");
        }

        // ── Failure and retry ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// A failed ACH debit marks the attempt and writes NO payment. The invoice's own status is
        /// untouched, so normal Overdue rules simply resume.
        /// </summary>
        [Fact]
        public async Task AFailedPaymentRecordsNoMoneyAndLeavesTheInvoiceAlone()
        {
            using var context = NewContext();
            var invoice = await SeedInvoiceAsync(context);
            await SeedAttemptAsync(context, invoice, "pi_test_fail", 925.43m);
            var service = NewService(context);

            await service.HandleFailedAsync(
                "pi_test_fail", "account_closed", "The bank account has been closed.", Metadata(invoice));

            Assert.Empty(await context.CommercialInvoicePayments.ToListAsync());

            var reloaded = await context.CommercialInvoices.FirstAsync(i => i.Id == invoice.Id);
            Assert.Equal(InvoiceStatus.Sent, reloaded.Status);
            Assert.Equal(925.43m, reloaded.BalanceDue);

            var attempt = await context.CommercialInvoicePaymentAttempts.FirstAsync();
            Assert.Equal(InvoicePaymentAttemptStatus.Failed, attempt.Status);
            Assert.Equal("account_closed", attempt.FailureCode);
        }

        /// <summary>
        /// A failed attempt is no longer in flight, so the customer can pay again. This is what
        /// re-enables "Pay from Bank" — the guard tests for in-flight, not for "has ever tried".
        /// </summary>
        [Fact]
        public async Task AFailedAttemptIsNotInFlightSoTheCustomerCanRetry()
        {
            using var context = NewContext();
            var invoice = await SeedInvoiceAsync(context);
            var attempt = await SeedAttemptAsync(
                context, invoice, "pi_test_retryable", 925.43m, InvoicePaymentAttemptStatus.Failed);

            Assert.False(attempt.IsInFlight);

            var billing = new BillingSettingsService(context);
            var settings = await billing.GetOrCreateAsync();
            settings.StripeAchEnabled = true;
            await context.SaveChangesAsync();

            var invoices = NewInvoiceService(context, billing);
            var options = await invoices.BuildPaymentOptionsAsync(invoice, settings);

            Assert.False(options.PaymentInProgress);
            Assert.True(options.StripeAchAvailable);
            Assert.True(options.LastAttemptFailed);

            // Customer-safe wording, never Stripe's own message.
            Assert.Equal(
                "Bank payment was unsuccessful. Please try again or use another payment method.",
                options.LastFailureMessage);
        }

        // ── The duplicate-payment guard ───────────────────────────────────────────────────────

        /// <summary>
        /// THE DUPLICATE-PAYMENT GUARD. While an ACH debit is processing the invoice still reads
        /// unpaid for days, so both pay buttons are switched off and the page says so instead.
        /// </summary>
        [Fact]
        public async Task AProcessingPaymentDisablesBothPayButtons()
        {
            using var context = NewContext();
            var invoice = await SeedInvoiceAsync(context);
            await SeedAttemptAsync(context, invoice, "pi_test_inflight", 925.43m);

            var billing = new BillingSettingsService(context);
            var settings = await billing.GetOrCreateAsync();
            settings.StripeAchEnabled = true;
            settings.StripeCardEnabled = true;
            await context.SaveChangesAsync();

            var invoices = NewInvoiceService(context, billing);
            var options = await invoices.BuildPaymentOptionsAsync(invoice, settings);

            Assert.True(options.PaymentInProgress);
            Assert.Equal(925.43m, options.ProcessingAmount);
            Assert.False(options.StripeAchAvailable);
            Assert.False(options.StripeCardAvailable);
        }

        /// <summary>
        /// A settled invoice offers no payment route at all — every flag goes off together rather
        /// than each surface having to remember to check.
        /// </summary>
        [Fact]
        public async Task APaidInvoiceOffersNoPaymentRoutes()
        {
            using var context = NewContext();
            var invoice = await SeedInvoiceAsync(context, total: 925.43m, alreadyPaid: 925.43m);
            invoice.Status = InvoiceStatus.Paid;
            await context.SaveChangesAsync();

            var billing = new BillingSettingsService(context);
            var settings = await billing.GetOrCreateAsync();
            settings.StripeAchEnabled = true;
            settings.StripeCardEnabled = true;
            settings.ManualAchEnabled = true;
            settings.BankName = "Test Bank";
            settings.BankAccountHolder = "Test Holder INC";
            settings.BankRoutingNumber = "000000000";
            settings.BankAccountNumber = "0000000000";
            settings.BankAccountType = "Business Checking";
            await context.SaveChangesAsync();

            var invoices = NewInvoiceService(context, billing);
            var options = await invoices.BuildPaymentOptionsAsync(invoice, settings);

            Assert.False(options.StripeAchAvailable);
            Assert.False(options.StripeCardAvailable);
            Assert.False(options.ManualAchAvailable);
            Assert.False(options.PaymentInProgress);
        }

        /// <summary>A void invoice likewise offers nothing, and prints no bank details.</summary>
        [Fact]
        public async Task AVoidInvoiceOffersNoPaymentRoutes()
        {
            using var context = NewContext();
            var invoice = await SeedInvoiceAsync(context);
            invoice.Status = InvoiceStatus.Void;
            await context.SaveChangesAsync();

            var billing = new BillingSettingsService(context);
            var settings = await billing.GetOrCreateAsync();
            settings.StripeAchEnabled = true;
            await context.SaveChangesAsync();

            var invoices = NewInvoiceService(context, billing);
            var options = await invoices.BuildPaymentOptionsAsync(invoice, settings);

            Assert.False(options.StripeAchAvailable);
            Assert.False(options.ManualAchAvailable);
        }

        // ── Card is off unless switched on ────────────────────────────────────────────────────

        [Fact]
        public async Task CardIsHiddenUntilTheOwnerEnablesIt()
        {
            using var context = NewContext();
            var invoice = await SeedInvoiceAsync(context);

            var billing = new BillingSettingsService(context);
            var settings = await billing.GetOrCreateAsync();

            // The shipped default: ACH on, card off.
            Assert.True(settings.StripeAchEnabled);
            Assert.False(settings.StripeCardEnabled);
            Assert.True(settings.ManualAchEnabled);

            var invoices = NewInvoiceService(context, billing);

            var before = await invoices.BuildPaymentOptionsAsync(invoice, settings);
            Assert.False(before.StripeCardAvailable);

            settings.StripeCardEnabled = true;
            await context.SaveChangesAsync();

            var after = await invoices.BuildPaymentOptionsAsync(invoice, settings);
            Assert.True(after.StripeCardAvailable);
        }

        // ── Manual ACH completeness ───────────────────────────────────────────────────────────

        /// <summary>
        /// Enabled but half-configured manual ACH must NOT render. Instructions naming an account
        /// holder with no routing or account number are instructions nobody can act on — the
        /// customer only discovers that at their bank.
        /// </summary>
        [Fact]
        public async Task IncompleteManualAchIsNeverShownToTheCustomer()
        {
            using var context = NewContext();
            var invoice = await SeedInvoiceAsync(context);

            var billing = new BillingSettingsService(context);
            var settings = await billing.GetOrCreateAsync();
            settings.ManualAchEnabled = true;
            settings.BankAccountHolder = "Test Holder INC";
            settings.BankRoutingNumber = null;   // the fields that matter are missing
            settings.BankAccountNumber = null;
            settings.BankName = null;
            await context.SaveChangesAsync();

            Assert.False(BillingSettingsService.IsManualAchComplete(settings));
            Assert.False(BillingSettingsService.CanOfferManualAch(settings));

            var missing = BillingSettingsService.MissingManualAchFields(settings);
            Assert.Contains("ACH routing number", missing);
            Assert.Contains("Account number", missing);
            Assert.Contains("Bank name", missing);

            var invoices = NewInvoiceService(context, billing);
            var options = await invoices.BuildPaymentOptionsAsync(invoice, settings);
            Assert.False(options.ManualAchAvailable);
        }

        [Fact]
        public async Task CompleteManualAchIsOffered()
        {
            using var context = NewContext();
            var invoice = await SeedInvoiceAsync(context);

            var billing = new BillingSettingsService(context);
            var settings = await billing.GetOrCreateAsync();
            settings.ManualAchEnabled = true;
            settings.BankName = "Test Bank";
            settings.BankAccountHolder = "Test Holder INC";
            settings.BankRoutingNumber = "000000000";
            settings.BankAccountNumber = "0000000000";
            settings.BankAccountType = "Business Checking";
            await context.SaveChangesAsync();

            Assert.True(BillingSettingsService.IsManualAchComplete(settings));
            Assert.Empty(BillingSettingsService.MissingManualAchFields(settings));

            var invoices = NewInvoiceService(context, billing);
            var options = await invoices.BuildPaymentOptionsAsync(invoice, settings);
            Assert.True(options.ManualAchAvailable);
        }

        /// <summary>
        /// Wire routing is optional and must NOT gate ACH — it is a convenience most clients never
        /// use, and requiring it would block the primary manual method.
        /// </summary>
        [Fact]
        public async Task WireRoutingIsNotRequiredForManualAch()
        {
            using var context = NewContext();
            var billing = new BillingSettingsService(context);
            var settings = await billing.GetOrCreateAsync();

            settings.BankName = "Test Bank";
            settings.BankAccountHolder = "Test Holder INC";
            settings.BankRoutingNumber = "000000000";
            settings.BankAccountNumber = "0000000000";
            settings.BankAccountType = "Business Checking";
            settings.BankWireRoutingNumber = null;
            await context.SaveChangesAsync();

            Assert.True(BillingSettingsService.IsManualAchComplete(settings));
        }

        // ── Currency ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A settlement in the wrong currency is refused rather than converted. Recording 92543
        /// euro-cents as $925.43 would silently misstate what the client paid.
        /// </summary>
        [Fact]
        public async Task ASettlementInTheWrongCurrencyIsRefused()
        {
            using var context = NewContext();
            var invoice = await SeedInvoiceAsync(context);
            await SeedAttemptAsync(context, invoice, "pi_test_eur", 925.43m);
            var service = NewService(context);

            var result = await service.RecordSucceededAsync(
                "pi_test_eur", 92543, "eur", null, Metadata(invoice), null);

            Assert.False(result.Recorded);
            Assert.Empty(await context.CommercialInvoicePayments.ToListAsync());
        }

        // ── checkout.session.completed does not pay the invoice ───────────────────────────────

        /// <summary>
        /// THE MOST IMPORTANT ACH RULE. Finishing Stripe's hosted flow is an AUTHORIZATION, not a
        /// settlement — the money has not moved. Marking the invoice paid here would tell the
        /// business it had been paid days before it had, and would be wrong permanently if the
        /// debit later failed.
        /// </summary>
        [Fact]
        public async Task CheckoutCompletionDoesNotPayTheInvoice()
        {
            using var context = NewContext();
            var invoice = await SeedInvoiceAsync(context);
            var attempt = await SeedAttemptAsync(
                context, invoice, "pi_test_auth", 925.43m, InvoicePaymentAttemptStatus.CheckoutOpen);

            var service = NewService(context);
            await service.HandleCheckoutCompletedAsync(
                attempt.StripeCheckoutSessionId!, "pi_test_auth", Metadata(invoice));

            // No money recorded.
            Assert.Empty(await context.CommercialInvoicePayments.ToListAsync());

            var reloaded = await context.CommercialInvoices.FirstAsync(i => i.Id == invoice.Id);
            Assert.Equal(InvoiceStatus.Sent, reloaded.Status);
            Assert.Equal(925.43m, reloaded.BalanceDue);
            Assert.Null(reloaded.PaidAt);

            // But the attempt now knows its PaymentIntent, which is what settlement resolves by.
            var reloadedAttempt = await context.CommercialInvoicePaymentAttempts.FirstAsync();
            Assert.Equal(InvoicePaymentAttemptStatus.Processing, reloadedAttempt.Status);
            Assert.Equal("pi_test_auth", reloadedAttempt.StripePaymentIntentId);
        }

        /// <summary>An expired session frees the invoice for another attempt.</summary>
        [Fact]
        public async Task AnExpiredCheckoutSessionFreesTheInvoice()
        {
            using var context = NewContext();
            var invoice = await SeedInvoiceAsync(context);
            var attempt = await SeedAttemptAsync(
                context, invoice, "pi_test_expire", 925.43m, InvoicePaymentAttemptStatus.CheckoutOpen);

            var service = NewService(context);
            await service.HandleCheckoutExpiredAsync(
                attempt.StripeCheckoutSessionId!, Metadata(invoice));

            var reloaded = await context.CommercialInvoicePaymentAttempts.FirstAsync();
            Assert.Equal(InvoicePaymentAttemptStatus.Expired, reloaded.Status);
            Assert.False(reloaded.IsInFlight);
        }

        /// <summary>
        /// Expiry must never touch a SETTLED attempt. Stripe can deliver events out of order, and
        /// an expiry arriving after a success must not undo a recorded payment.
        /// </summary>
        [Fact]
        public async Task ExpiryNeverOverwritesASucceededAttempt()
        {
            using var context = NewContext();
            var invoice = await SeedInvoiceAsync(context);
            var attempt = await SeedAttemptAsync(
                context, invoice, "pi_test_settled", 925.43m, InvoicePaymentAttemptStatus.Succeeded);

            var service = NewService(context);
            await service.HandleCheckoutExpiredAsync(
                attempt.StripeCheckoutSessionId!, Metadata(invoice));

            var reloaded = await context.CommercialInvoicePaymentAttempts.FirstAsync();
            Assert.Equal(InvoicePaymentAttemptStatus.Succeeded, reloaded.Status);
        }

        // ── Metadata routing ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// The discriminator is what stops a commercial payment falling into the residential
        /// handler on the shared webhook.
        /// </summary>
        [Fact]
        public void OnlyCommercialMetadataIsRecognisedAsCommercial()
        {
            Assert.True(StripeCommercialInvoiceMetadata.IsCommercialInvoice(
                new Dictionary<string, string> { ["type"] = "commercial_invoice" }));

            Assert.False(StripeCommercialInvoiceMetadata.IsCommercialInvoice(
                new Dictionary<string, string> { ["type"] = "booking" }));
            Assert.False(StripeCommercialInvoiceMetadata.IsCommercialInvoice(
                new Dictionary<string, string> { ["type"] = "order_update" }));
            Assert.False(StripeCommercialInvoiceMetadata.IsCommercialInvoice(
                new Dictionary<string, string> { ["type"] = "gift_card" }));
            Assert.False(StripeCommercialInvoiceMetadata.IsCommercialInvoice(
                new Dictionary<string, string>()));
            Assert.False(StripeCommercialInvoiceMetadata.IsCommercialInvoice(null));
        }

        /// <summary>
        /// Metadata never carries anything sensitive. Internal ids are fine — Stripe metadata is
        /// not the public invoice URL — but bank details are not.
        /// </summary>
        [Fact]
        public void MetadataKeysCarryNoBankingInformation()
        {
            var keys = new[]
            {
                StripeCommercialInvoiceMetadata.InvoiceIdKey,
                StripeCommercialInvoiceMetadata.InvoiceNumberKey,
                StripeCommercialInvoiceMetadata.ClientIdKey,
                StripeCommercialInvoiceMetadata.AttemptIdKey
            };

            foreach (var key in keys)
            {
                foreach (var forbidden in new[] { "account", "routing", "bank", "secret", "token" })
                    Assert.DoesNotContain(forbidden, key.ToLowerInvariant());
            }
        }

        // ── The attempt table's shape ─────────────────────────────────────────────────────────

        /// <summary>
        /// Attempts must never store raw bank credentials. Stripe's hosted flow is what keeps them
        /// off this server, and there is no column here they could be written into.
        /// </summary>
        [Fact]
        public void ThePaymentAttemptStoresNoBankCredentials()
        {
            var names = typeof(CommercialInvoicePaymentAttempt)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name.ToLowerInvariant())
                .ToList();

            foreach (var forbidden in new[]
                     { "accountnumber", "routingnumber", "cardnumber", "cvv", "password", "pin" })
            {
                Assert.DoesNotContain(names, n => n.Contains(forbidden));
            }
        }

        private static InvoiceService NewInvoiceService(
            ApplicationDbContext context, BillingSettingsService billing) =>
            new(context,
                new InvoiceNumberService(context, NullLogger<InvoiceNumberService>.Instance),
                billing,
                new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
                NullLogger<InvoiceService>.Instance);
    }
}
