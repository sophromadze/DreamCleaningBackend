using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs.Commercial;
using DreamCleaningBackend.Helpers.Commercial;
using DreamCleaningBackend.Models.Commercial;
using DreamCleaningBackend.Models.Contracts;
using DreamCleaningBackend.Services.Commercial;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// Commercial invoices, 2026-10: the payment methods an invoice offers are chosen PER INVOICE
    /// (the Billing Settings switches are only the pre-ticked defaults), and every invoice carries
    /// its own recipient list that sending, reminders and receipts all use.
    ///
    /// The bug behind it: an invoice saved with "Card" offered no card, because the invoice's single
    /// payment-method field was never read by anything that decided what the customer could do.
    /// </summary>
    public class InvoicePaymentMethodsAndRecipientsTests
    {
        private const int AdminId = 7;

        private static ApplicationDbContext NewContext() =>
            new(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase($"invoice-methods-{Guid.NewGuid()}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options);

        private static InvoiceService NewInvoiceService(ApplicationDbContext context) =>
            new(context,
                new InvoiceNumberService(context, NullLogger<InvoiceNumberService>.Instance),
                new BillingSettingsService(context),
                new ConfigurationBuilder().Build(),
                NullLogger<InvoiceService>.Instance);

        private static async Task<ContractClient> SeedClientAsync(ApplicationDbContext context, string? noticeEmail = "notices@client.example")
        {
            var client = new ContractClient
            {
                LegalEntityName = "Acme Offices LLC",
                EntityType = "a limited liability company",
                PrincipalAddress = "1 Test Street",
                City = "Brooklyn",
                State = "NY",
                Zip = "11226",
                NoticeEmail = noticeEmail
            };
            context.ContractClients.Add(client);
            await context.SaveChangesAsync();
            return client;
        }

        private static SaveInvoiceDto Dto(int clientId,
            InvoicePaymentMethods methods = InvoicePaymentMethods.OnlineBank | InvoicePaymentMethods.Card,
            params string[] recipients) => new()
        {
            ContractClientId = clientId,
            InvoiceDate = DateTime.UtcNow.Date,
            DueTerms = InvoiceDueTerms.Net15,
            TaxType = InvoiceTaxType.Exempt,
            PaymentMethods = methods,
            Recipients = recipients.Length == 0 ? new List<string> { "ap@client.example" } : recipients.ToList(),
            Items = { new SaveInvoiceItemDto { Description = "Office cleaning", Quantity = 1m, UnitPrice = 925.43m } }
        };

        private static async Task CompleteBankDetailsAsync(ApplicationDbContext context)
        {
            var settings = await new BillingSettingsService(context).GetOrCreateAsync();
            settings.BankName = "Test Bank";
            settings.BankAccountHolder = "Test Holder INC";
            settings.BankRoutingNumber = "000000000";
            settings.BankAccountNumber = "0000000000";
            settings.BankAccountType = "Business Checking";
            await context.SaveChangesAsync();
        }

        // ── The rules, without a database ─────────────────────────────────────────────────────

        [Fact]
        public void AnInvoiceMustOfferAtLeastOneKnownMethod()
        {
            var settings = new BillingSettings();

            Assert.NotNull(InvoicePaymentMethodRules.DescribeProblem(InvoicePaymentMethods.None, null, settings));
            Assert.NotNull(InvoicePaymentMethodRules.DescribeProblem((InvoicePaymentMethods)64, null, settings));
            Assert.Null(InvoicePaymentMethodRules.DescribeProblem(InvoicePaymentMethods.Card, null, settings));
            Assert.Null(InvoicePaymentMethodRules.DescribeProblem(InvoicePaymentMethods.Other, null, settings));
        }

        [Fact]
        public void ManualTransferCanOnlyBeAddedWithCompleteBankDetails_ButAnExistingOneIsNeverBlocked()
        {
            var incomplete = new BillingSettings { BankName = "Test Bank" };

            var problem = InvoicePaymentMethodRules.DescribeProblem(
                InvoicePaymentMethods.ManualBankTransfer, null, incomplete);
            Assert.NotNull(problem);
            Assert.Contains("Account number", problem);

            // Already offered: an admin fixing a PO number must not be blocked by somebody else
            // clearing a bank field in settings.
            Assert.Null(InvoicePaymentMethodRules.DescribeProblem(
                InvoicePaymentMethods.ManualBankTransfer,
                InvoicePaymentMethods.ManualBankTransfer | InvoicePaymentMethods.Card,
                incomplete));
        }

        [Fact]
        public void TheSettingsSwitchesAreTheDefaults_AndAGeneratedInvoiceIsNeverLeftWithNone()
        {
            var settings = new BillingSettings
            {
                StripeAchEnabled = true,
                StripeCardEnabled = true,
                ManualAchEnabled = true            // but the bank details are missing
            };

            Assert.Equal(InvoicePaymentMethods.OnlineBank | InvoicePaymentMethods.Card,
                InvoicePaymentMethodRules.DefaultsFrom(settings));

            var allOff = new BillingSettings { StripeAchEnabled = false, StripeCardEnabled = false, ManualAchEnabled = false };
            Assert.Equal(InvoicePaymentMethods.None, InvoicePaymentMethodRules.DefaultsFrom(allOff));
            Assert.Equal(InvoicePaymentMethods.OnlineBank, InvoicePaymentMethodRules.DefaultsForGeneratedInvoice(allOff));
        }

        [Fact]
        public void RecipientsAreTrimmedAndDeduplicatedWithoutRewritingWhatWasTyped()
        {
            var list = InvoiceRecipientList.Normalize(new[] { " AP@Client.example ", "ap@client.example", "", null, "boss@client.example" });

            Assert.Equal(new[] { "AP@Client.example", "boss@client.example" }, list);
        }

        [Fact]
        public void ARecipientProblemNamesTheAddress()
        {
            Assert.Equal("Add at least one email address to send this invoice to.",
                InvoiceRecipientList.DescribeProblem(new List<string>()));

            var problem = InvoiceRecipientList.DescribeProblem(new List<string> { "ok@client.example", "no-at-sign.example" });
            Assert.NotNull(problem);
            Assert.StartsWith("\"no-at-sign.example\":", problem);
            Assert.Contains("@", problem);

            var tooMany = Enumerable.Range(1, InvoiceRecipientList.MaxRecipients + 1).Select(i => $"p{i}@client.example").ToList();
            Assert.NotNull(InvoiceRecipientList.DescribeProblem(tooMany));
        }

        [Fact]
        public void ANewInvoiceIsAddressedToTheSavedDefaults_ElseToTheBillingAddress()
        {
            Assert.Equal(new[] { "a@x.example", "b@x.example" },
                InvoiceRecipientList.DefaultsFor("[\"a@x.example\",\"b@x.example\"]", "contact@x.example", "notice@x.example"));
            Assert.Equal(new[] { "contact@x.example" },
                InvoiceRecipientList.DefaultsFor(null, "contact@x.example", "notice@x.example"));
            Assert.Equal(new[] { "notice@x.example" },
                InvoiceRecipientList.DefaultsFor(null, null, "notice@x.example"));
            Assert.Empty(InvoiceRecipientList.DefaultsFor("not json", null, null));
        }

        [Fact]
        public void AnAchFeeChangeIsSpelledOutInTheSettingsAudit()
        {
            var before = new BillingSettings { CompanyLegalName = "Co", AchCustomerFeeEnabled = true, AchCustomerFeeRatePercent = 0.8m, AchCustomerFeeCapAmount = 5m };
            var after = new SaveBillingSettingsDto { CompanyLegalName = "Co", AchCustomerFeeEnabled = true, AchCustomerFeeRatePercent = 1m, AchCustomerFeeCapAmount = 5m };

            var text = BillingSettingsService.DescribeChanges(before, after);

            Assert.Contains("ACH processing fee rate 0.8% → 1%", text);
            Assert.DoesNotContain("fee cap", text);
        }

        // ── Saving ────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task CreatingStoresTheChosenMethodsAndRecipients()
        {
            using var context = NewContext();
            var client = await SeedClientAsync(context);

            var invoice = await NewInvoiceService(context).CreateAsync(
                Dto(client.Id, InvoicePaymentMethods.Card | InvoicePaymentMethods.Other, "ap@client.example", "cfo@client.example"),
                AdminId);

            Assert.Equal(InvoicePaymentMethods.Card | InvoicePaymentMethods.Other, invoice.PaymentMethods);
            Assert.Equal(new[] { "ap@client.example", "cfo@client.example" },
                InvoiceRecipientList.Parse(invoice.RecipientEmailsJson));
        }

        [Fact]
        public async Task AnInvoiceWithNoMethodOrNoRecipientIsRefused_AndBurnsNoNumber()
        {
            using var context = NewContext();
            var client = await SeedClientAsync(context);
            var service = NewInvoiceService(context);

            await Assert.ThrowsAsync<InvoiceWorkflowException>(() =>
                service.CreateAsync(Dto(client.Id, InvoicePaymentMethods.None), AdminId));

            var noRecipients = Dto(client.Id);
            noRecipients.Recipients = new List<string> { "  " };
            await Assert.ThrowsAsync<InvoiceWorkflowException>(() => service.CreateAsync(noRecipients, AdminId));

            var badAddress = Dto(client.Id);
            badAddress.Recipients = new List<string> { "ap.client.example" };
            var ex = await Assert.ThrowsAsync<InvoiceWorkflowException>(() => service.CreateAsync(badAddress, AdminId));
            Assert.Contains("ap.client.example", ex.Message);

            Assert.Empty(await context.CommercialInvoices.ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task ManualTransferIsRefusedUntilTheBankDetailsAreComplete()
        {
            using var context = NewContext();
            var client = await SeedClientAsync(context);
            var service = NewInvoiceService(context);

            await Assert.ThrowsAsync<InvoiceWorkflowException>(() =>
                service.CreateAsync(Dto(client.Id, InvoicePaymentMethods.ManualBankTransfer), AdminId));

            await CompleteBankDetailsAsync(context);
            var invoice = await service.CreateAsync(Dto(client.Id, InvoicePaymentMethods.ManualBankTransfer), AdminId);
            Assert.Equal(InvoicePaymentMethods.ManualBankTransfer, invoice.PaymentMethods);
        }

        [Fact]
        public async Task SavingAsTheClientDefaultPrefillsTheNextInvoice()
        {
            using var context = NewContext();
            var client = await SeedClientAsync(context);

            var dto = Dto(client.Id, InvoicePaymentMethods.OnlineBank, "ap@client.example", "cfo@client.example");
            dto.SaveRecipientsAsClientDefault = true;
            await NewInvoiceService(context).CreateAsync(dto, AdminId);

            var saved = await context.ContractClients.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(new[] { "ap@client.example", "cfo@client.example" },
                InvoiceRecipientList.DefaultsFor(saved.DefaultInvoiceRecipientsJson, "someone@else.example", saved.NoticeEmail));
        }

        [Fact]
        public async Task OnAPaidInvoiceTheMethodsLockButTheRecipientsStayEditable()
        {
            using var context = NewContext();
            var client = await SeedClientAsync(context);
            var service = NewInvoiceService(context);
            var invoice = await service.CreateAsync(Dto(client.Id, InvoicePaymentMethods.OnlineBank), AdminId);

            invoice.Status = InvoiceStatus.Paid;
            invoice.AmountPaid = invoice.Total;
            invoice.BalanceDue = 0m;
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            var edit = Dto(client.Id, InvoicePaymentMethods.Card, "receipts@client.example");
            var updated = await service.UpdateAsync(invoice.Id, edit, AdminId);

            Assert.Equal(InvoicePaymentMethods.OnlineBank, updated.PaymentMethods);
            Assert.Equal(new[] { "receipts@client.example" }, InvoiceRecipientList.Parse(updated.RecipientEmailsJson));
            Assert.Contains(await context.CommercialInvoiceActivityLogs.ToListAsync(cancellationToken: TestContext.Current.CancellationToken), a => a.Action == "recipients_changed");
        }

        [Fact]
        public async Task ChangingTheMethodsIsRecordedInTheTrail()
        {
            using var context = NewContext();
            var client = await SeedClientAsync(context);
            var service = NewInvoiceService(context);
            var invoice = await service.CreateAsync(Dto(client.Id, InvoicePaymentMethods.OnlineBank), AdminId);

            await service.UpdateAsync(invoice.Id, Dto(client.Id, InvoicePaymentMethods.OnlineBank | InvoicePaymentMethods.Card), AdminId);

            var entry = Assert.Single(await context.CommercialInvoiceActivityLogs
                .Where(a => a.Action == "payment_methods_changed").ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
            Assert.Contains("Online bank payment (ACH), Card", entry.Description);
        }

        [Fact]
        public async Task AVoidInvoiceRefusesARecipientChange()
        {
            using var context = NewContext();
            var client = await SeedClientAsync(context);
            var service = NewInvoiceService(context);
            var invoice = await service.CreateAsync(Dto(client.Id), AdminId);
            invoice.Status = InvoiceStatus.Void;
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<InvoiceWorkflowException>(() =>
                service.UpdateRecipientsAsync(invoice.Id, new[] { "x@client.example" }, false, AdminId));
        }

        [Fact]
        public async Task AnInvoiceFromBeforeRecipientListsFallsBackToTheBillingAddress()
        {
            using var context = NewContext();
            var client = await SeedClientAsync(context, noticeEmail: "notices@client.example");
            var invoice = new CommercialInvoice
            {
                InvoiceNumber = "DCI-2026-11111111",
                PublicToken = new string('a', 48),
                ContractClientId = client.Id,
                Status = InvoiceStatus.Sent,
                RecipientEmailsJson = null,
                CreatedByUserId = AdminId
            };
            context.CommercialInvoices.Add(invoice);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            Assert.Equal(new[] { "notices@client.example" }, await NewInvoiceService(context).ResolveRecipientsAsync(invoice));
        }

        // ── The checkout endpoint is the control, not the button ──────────────────────────────

        [Fact]
        public async Task CheckoutRefusesAMethodTheInvoiceDoesNotOffer_EvenWithTheGlobalSwitchOn()
        {
            using var context = NewContext();
            var client = await SeedClientAsync(context);
            var invoices = NewInvoiceService(context);
            var invoice = await invoices.CreateAsync(Dto(client.Id, InvoicePaymentMethods.OnlineBank), AdminId);
            invoice.Status = InvoiceStatus.Sent;
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            var billing = new BillingSettingsService(context);
            (await billing.GetOrCreateAsync(TestContext.Current.CancellationToken)).StripeCardEnabled = true;
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            var checkout = new InvoiceCheckoutService(context, invoices, billing,
                new ConfigurationBuilder().Build(), NullLogger<InvoiceCheckoutService>.Instance);

            var ex = await Assert.ThrowsAsync<InvoiceCheckoutUnavailableException>(() =>
                checkout.CreateCheckoutAsync(invoice, InvoicePaymentRecordMethod.Card, null));
            Assert.Contains("Card payment is not available", ex.Message);

            // Refused before anything was written or sent to Stripe.
            Assert.Empty(await context.CommercialInvoicePaymentAttempts.ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        }

        // ── Sending goes to the invoice's own list ────────────────────────────────────────────

        private sealed class RecordingEmail
        {
            public List<string> To { get; } = new();
            public HashSet<string> Failing { get; } = new(StringComparer.OrdinalIgnoreCase);

            public IEmailService Service => RecurringDiscountRegressionTests.Stub<IEmailService>((method, args) =>
            {
                if (method.Name is "SendEmailAsync" or "SendEmailWithAttachmentAsync")
                {
                    var to = (string)args![0]!;
                    if (Failing.Contains(to)) return Task.FromException(new InvalidOperationException("Mailbox unavailable"));
                    To.Add(to);
                }
                return Task.CompletedTask;
            });
        }

        private static InvoiceEmailService NewEmailService(ApplicationDbContext context, RecordingEmail email) =>
            new(context, email.Service, NewInvoiceService(context), new InvoicePdfService(),
                NullLogger<InvoiceEmailService>.Instance);

        [Fact]
        public async Task SendingMailsEveryRecipientSeparately_UnderOneBatch()
        {
            using var context = NewContext();
            var client = await SeedClientAsync(context);
            var invoice = await NewInvoiceService(context).CreateAsync(
                Dto(client.Id, InvoicePaymentMethods.OnlineBank, "ap@client.example", "cfo@client.example"), AdminId);

            var email = new RecordingEmail();
            var outcome = await NewEmailService(context, email).SendInvoiceAsync(
                invoice.Id, new SendInvoiceDto { AttachPdf = false }, AdminId);

            Assert.Equal(new[] { "ap@client.example", "cfo@client.example" }, email.To);
            Assert.True(outcome.AnySent);

            var logs = await context.CommercialInvoiceEmailLogs.ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(2, logs.Count);
            Assert.Single(logs.Select(l => l.SendBatchId).Distinct());
            Assert.NotNull(logs[0].SendBatchId);

            // Never the client's notice address: the invoice's own list is the whole answer.
            Assert.DoesNotContain("notices@client.example", email.To);
            Assert.Equal(InvoiceStatus.Sent, (await context.CommercialInvoices.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).Status);
        }

        [Fact]
        public async Task APartlyFailedSendStillCountsAsSent_AndNamesWhoWasMissed()
        {
            using var context = NewContext();
            var client = await SeedClientAsync(context);
            var invoice = await NewInvoiceService(context).CreateAsync(
                Dto(client.Id, InvoicePaymentMethods.OnlineBank, "ap@client.example", "gone@client.example"), AdminId);

            var email = new RecordingEmail();
            email.Failing.Add("gone@client.example");
            var outcome = await NewEmailService(context, email).SendInvoiceAsync(
                invoice.Id, new SendInvoiceDto { AttachPdf = false }, AdminId);

            Assert.True(outcome.AnySent);
            Assert.False(outcome.AllFailed);
            Assert.Equal(new[] { "ap@client.example" }, outcome.SentTo);
            Assert.Contains("gone@client.example (Mailbox unavailable)", outcome.DescribeFailures());
        }

        [Fact]
        public async Task TheSendDialogsListReplacesTheStoredOneBeforeSending()
        {
            using var context = NewContext();
            var client = await SeedClientAsync(context);
            var invoice = await NewInvoiceService(context).CreateAsync(
                Dto(client.Id, InvoicePaymentMethods.OnlineBank, "ap@client.example"), AdminId);

            var email = new RecordingEmail();
            await NewEmailService(context, email).SendInvoiceAsync(invoice.Id,
                new SendInvoiceDto { AttachPdf = false, Recipients = new List<string> { "new-ap@client.example" } }, AdminId);

            Assert.Equal(new[] { "new-ap@client.example" }, email.To);
            Assert.Equal(new[] { "new-ap@client.example" },
                InvoiceRecipientList.Parse((await context.CommercialInvoices.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).RecipientEmailsJson));
        }

        [Fact]
        public async Task AReminderGoesToEveryRecipientAndIsRecordedPerAddress()
        {
            using var context = NewContext();
            var client = await SeedClientAsync(context);
            var invoice = await NewInvoiceService(context).CreateAsync(
                Dto(client.Id, InvoicePaymentMethods.OnlineBank, "ap@client.example", "cfo@client.example"), AdminId);
            invoice.Status = InvoiceStatus.Sent;
            invoice.FirstSentAt = DateTime.UtcNow;
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            var email = new RecordingEmail();
            await NewEmailService(context, email).SendReminderAsync(invoice.Id, new SendInvoiceReminderDto(), AdminId);

            Assert.Equal(2, email.To.Count);
            Assert.Equal(new[] { "ap@client.example", "cfo@client.example" },
                (await context.CommercialInvoiceReminders.OrderBy(r => r.Id).ToListAsync(cancellationToken: TestContext.Current.CancellationToken)).Select(r => r.Recipient));
        }
    }
}
