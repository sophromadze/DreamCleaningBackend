using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
using UglyToad.PdfPig;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// THE SIGN OF MONEY RECEIVED.
    ///
    /// One rule, and every surface obeys it: <b>a payment is a POSITIVE amount, and only a
    /// reversal is negative.</b> That is what <c>CommercialInvoicePayment.Amount</c> stores, what
    /// <c>InvoiceCalculator.ResolveAmountPaid</c> signs-sums, and therefore what every screen,
    /// document and email must print.
    ///
    /// WHAT WENT WRONG (2026-09): a real Stripe ACH settlement of $925.43 was stored correctly as
    /// +925.43 — the invoice went Paid and the balance went to $0.00, which is only reachable from
    /// a positive row — but three surfaces hand-wrote a "−" in front of the Amount Paid figure, so
    /// the customer's own invoice read "−$925.43". Two other surfaces (admin detail, receipt
    /// email) printed it positive. The presentation had drifted apart per surface because each one
    /// decided the sign for itself.
    ///
    /// The tests below therefore assert the WHOLE chain rather than one label: the ledger row, the
    /// recalculated invoice, both DTOs, the admin Payment History projection and the rendered PDF.
    /// A future edit that reintroduces a decorative minus on any of them fails here.
    ///
    /// The Discount line is a different question and deliberately untouched: that IS a reduction of
    /// what is billed, so it keeps its sign. Money received is not a reduction of the bill — it is
    /// the bill being met.
    /// </summary>
    public class InvoicePaymentSignTests
    {
        /// <summary>The invoice from the end-to-end ACH test, kept as the fixture throughout.</summary>
        private const decimal InvoiceTotal = 925.43m;
        private const long InvoiceTotalCents = 92543;

        // ── Fixtures ──────────────────────────────────────────────────────────────────────────

        private static ApplicationDbContext NewContext()
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase($"invoice-sign-{Guid.NewGuid()}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            return new ApplicationDbContext(options);
        }

        private static BillingSettingsService NewBilling(ApplicationDbContext context) => new(context);

        private static InvoiceService NewInvoiceService(
            ApplicationDbContext context, BillingSettingsService billing) =>
            new(context,
                new InvoiceNumberService(context, NullLogger<InvoiceNumberService>.Instance),
                billing,
                new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
                NullLogger<InvoiceService>.Instance);

        private static InvoiceStripePaymentService NewStripeService(
            ApplicationDbContext context, InvoiceService invoices) =>
            new(context, invoices, NullLogger<InvoiceStripePaymentService>.Instance);

        /// <summary>The admin who raised the invoice, and the one who reverses a payment below.</summary>
        private const int CreatingAdminId = 1;
        private const int ReversingAdminId = 7;

        /// <summary>
        /// The staff rows the admin-detail query joins through. NOTHING HERE IS UNDER TEST — they
        /// exist because <c>CommercialInvoice.CreatedByUserId</c> is a REQUIRED relationship
        /// (<c>OnDelete(Restrict)</c>, so in production the row can never be missing), which makes
        /// <c>GetDetailAsync</c>'s <c>Include(i => i.CreatedByUser)</c> an inner join. Without the
        /// principal in the in-memory graph the invoice itself is filtered out and the whole detail
        /// DTO comes back null — the same reason <c>AuditCoverageTests</c> seeds its include chain.
        /// </summary>
        private static async Task SeedStaffAsync(ApplicationDbContext context)
        {
            context.Users.AddRange(
                new DreamCleaningBackend.Models.User
                {
                    Id = CreatingAdminId, Email = "admin@example.invalid",
                    FirstName = "Ada", LastName = "Admin",
                    PasswordHash = "x", PasswordSalt = "x"
                },
                new DreamCleaningBackend.Models.User
                {
                    Id = ReversingAdminId, Email = "reverser@example.invalid",
                    FirstName = "Rey", LastName = "Reverser",
                    PasswordHash = "x", PasswordSalt = "x"
                });
            await context.SaveChangesAsync();
        }

        /// <summary>An issued, unpaid invoice for $925.43. Fictional client; no real bank data.</summary>
        private static async Task<CommercialInvoice> SeedInvoiceAsync(ApplicationDbContext context)
        {
            await SeedStaffAsync(context);

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
                SubTotal = InvoiceTotal,
                Total = InvoiceTotal,
                AmountPaid = 0m,
                BalanceDue = InvoiceTotal,
                Currency = "USD",
                FirstSentAt = new DateTime(2026, 9, 7),
                CreatedByUserId = CreatingAdminId,
                Items =
                {
                    new CommercialInvoiceItem
                    {
                        Description = "Janitorial services — September 2026",
                        Quantity = 1m,
                        UnitPrice = InvoiceTotal,
                        Amount = InvoiceTotal,
                        SortOrder = 0
                    }
                }
            };
            context.CommercialInvoices.Add(invoice);
            await context.SaveChangesAsync();
            return invoice;
        }

        private static async Task SeedAttemptAsync(
            ApplicationDbContext context, CommercialInvoice invoice, string paymentIntentId)
        {
            context.CommercialInvoicePaymentAttempts.Add(new CommercialInvoicePaymentAttempt
            {
                CommercialInvoiceId = invoice.Id,
                Provider = InvoicePaymentProvider.Stripe,
                PaymentMethod = InvoicePaymentRecordMethod.AchBankTransfer,
                Amount = InvoiceTotal,
                Currency = "USD",
                StripeCheckoutSessionId = "cs_test_" + Guid.NewGuid().ToString("N")[..12],
                StripePaymentIntentId = paymentIntentId,
                Status = InvoicePaymentAttemptStatus.Processing
            });
            await context.SaveChangesAsync();
        }

        private static Dictionary<string, string> Metadata(CommercialInvoice invoice) => new()
        {
            ["type"] = StripeCommercialInvoiceMetadata.TypeValue,
            [StripeCommercialInvoiceMetadata.InvoiceIdKey] = invoice.Id.ToString(),
            [StripeCommercialInvoiceMetadata.InvoiceNumberKey] = invoice.InvoiceNumber,
            [StripeCommercialInvoiceMetadata.ClientIdKey] = invoice.ContractClientId.ToString()
        };

        /// <summary>Settles the full $925.43 by ACH, exactly as the webhook path does.</summary>
        private static async Task<CommercialInvoice> SettleByAchAsync(
            ApplicationDbContext context, InvoiceService invoices, string paymentIntentId = "pi_test_sign")
        {
            var invoice = await SeedInvoiceAsync(context);
            await SeedAttemptAsync(context, invoice, paymentIntentId);

            var result = await NewStripeService(context, invoices).RecordSucceededAsync(
                paymentIntentId, InvoiceTotalCents, "usd", "ch_test_sign",
                Metadata(invoice), "Test Bank ••••6789");

            Assert.True(result.Recorded);
            return invoice;
        }

        // ── The ledger row ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The row itself. If this is ever negative, every surface below is downstream of a real
        /// data bug and no amount of formatting will save it.
        /// </summary>
        [Fact]
        public async Task AStripeAchSettlementIsStoredAsAPositivePaymentRow()
        {
            using var context = NewContext();
            var invoices = NewInvoiceService(context, NewBilling(context));
            await SettleByAchAsync(context, invoices);

            var payment = await context.CommercialInvoicePayments.SingleAsync();

            Assert.Equal(925.43m, payment.Amount);
            Assert.True(payment.Amount > 0m, "A received payment must never be a negative ledger entry.");
            Assert.False(payment.IsReversal);
            Assert.Null(payment.ReversesPaymentId);
            Assert.Equal(InvoicePaymentProvider.Stripe, payment.Provider);
        }

        // ── Total / Amount paid / Balance due ─────────────────────────────────────────────────

        /// <summary>
        /// The three figures the customer reads, from the invoice the recalculation produced:
        /// Total $925.43, Amount paid $925.43, Balance due $0.00.
        /// </summary>
        [Fact]
        public async Task ASettledInvoiceReadsTotal925Paid925Balance0()
        {
            using var context = NewContext();
            var invoices = NewInvoiceService(context, NewBilling(context));
            var seeded = await SettleByAchAsync(context, invoices);

            var invoice = await context.CommercialInvoices.FirstAsync(i => i.Id == seeded.Id);

            Assert.Equal(925.43m, invoice.Total);
            Assert.Equal(925.43m, invoice.AmountPaid);
            Assert.Equal(0m, invoice.BalanceDue);
            Assert.Equal(InvoiceStatus.Paid, invoice.Status);

            // No credit is owed back — the money exactly met the bill.
            Assert.Equal(0m, InvoiceCalculator.ResolveOverpayment(invoice.Total, invoice.AmountPaid));
        }

        /// <summary>The public (customer-facing) DTO — the source the paid invoice page renders from.</summary>
        [Fact]
        public async Task ThePublicInvoiceDtoReportsAPositiveAmountPaid()
        {
            using var context = NewContext();
            var invoices = NewInvoiceService(context, NewBilling(context));
            var seeded = await SettleByAchAsync(context, invoices);

            var invoice = await context.CommercialInvoices
                .Include(i => i.Items)
                .Include(i => i.Client)
                .FirstAsync(i => i.Id == seeded.Id);

            var dto = await invoices.ToPublicDtoAsync(invoice);

            Assert.Equal(925.43m, dto.Total);
            Assert.Equal(925.43m, dto.AmountPaid);
            Assert.Equal(0m, dto.BalanceDue);
        }

        /// <summary>The admin detail DTO. Same numbers, same signs — the two views cannot disagree.</summary>
        [Fact]
        public async Task TheAdminInvoiceDtoReportsAPositiveAmountPaid()
        {
            using var context = NewContext();
            var invoices = NewInvoiceService(context, NewBilling(context));
            var seeded = await SettleByAchAsync(context, invoices);

            var dto = await invoices.GetDetailAsync(seeded.Id);

            Assert.NotNull(dto);
            Assert.Equal(925.43m, dto!.Total);
            Assert.Equal(925.43m, dto.AmountPaid);
            Assert.Equal(0m, dto.BalanceDue);
            Assert.Equal(0m, dto.Overpayment);
        }

        // ── Admin Payment History ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Payment History renders <c>p.amount</c> straight through the currency pipe, so the DTO
        /// sign IS what the admin sees. A Stripe ACH settlement must appear there as received
        /// money, not as a deduction.
        /// </summary>
        [Fact]
        public async Task AdminPaymentHistoryShowsTheStripeAchPaymentAsPositiveReceivedMoney()
        {
            using var context = NewContext();
            var invoices = NewInvoiceService(context, NewBilling(context));
            var seeded = await SettleByAchAsync(context, invoices);

            var dto = await invoices.GetDetailAsync(seeded.Id);
            var row = Assert.Single(dto!.Payments);

            Assert.Equal(925.43m, row.Amount);
            Assert.True(row.Amount > 0m);
            Assert.False(row.IsReversal);
            Assert.Equal("ACH Bank Transfer", row.PaymentMethodLabel);
        }

        // ── The PDF ───────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The document the customer downloads and files. This is where "−$925.43" was most
        /// damaging, because a PDF is what gets forwarded to their accounts department.
        /// </summary>
        [Fact]
        public async Task TheInvoicePdfPrintsAmountPaidWithoutAMinusSign()
        {
            using var context = NewContext();
            var invoices = NewInvoiceService(context, NewBilling(context));
            var seeded = await SettleByAchAsync(context, invoices);

            var invoice = await context.CommercialInvoices
                .Include(i => i.Items)
                .Include(i => i.Client)
                .FirstAsync(i => i.Id == seeded.Id);

            var bytes = new InvoicePdfService().Render(await invoices.ToPublicDtoAsync(invoice));

            using var pdf = PdfDocument.Open(bytes);
            var text = string.Join(" ", pdf.GetPages().Select(p => p.Text));

            Assert.Contains("$925.43", text);
            Assert.DoesNotContain("-$925.43", text);
            Assert.DoesNotContain("−$925.43", text); // U+2212, the character the web pages used
        }

        // ── Reversals keep their negative semantics ───────────────────────────────────────────

        /// <summary>
        /// The other half of the rule. Removing the decorative minus must NOT have removed the
        /// meaningful one: a reversal is still a negative row, it still subtracts from Amount Paid,
        /// and it still takes the invoice back out of Paid.
        /// </summary>
        [Fact]
        public async Task AReversalIsNegativeAndReducesAmountPaid()
        {
            using var context = NewContext();
            var invoices = NewInvoiceService(context, NewBilling(context));
            var seeded = await SettleByAchAsync(context, invoices);

            var original = await context.CommercialInvoicePayments.SingleAsync();

            await new InvoicePaymentService(context).ReverseAsync(
                seeded.Id, original.Id, "Bank returned the debit — insufficient funds.", userId: ReversingAdminId);

            var invoice = await context.CommercialInvoices.FirstAsync(i => i.Id == seeded.Id);

            Assert.Equal(0m, invoice.AmountPaid);
            Assert.Equal(925.43m, invoice.BalanceDue);
            Assert.NotEqual(InvoiceStatus.Paid, invoice.Status);
            Assert.Null(invoice.PaidAt);
        }

        /// <summary>
        /// AUDITABILITY. The reversal is an entry of its own; the original row is neither deleted
        /// nor rewritten, so Payment History still shows the money arriving and then going back.
        /// </summary>
        [Fact]
        public async Task AReversalStaysAuditableAndLeavesTheOriginalPaymentPositive()
        {
            using var context = NewContext();
            var invoices = NewInvoiceService(context, NewBilling(context));
            var seeded = await SettleByAchAsync(context, invoices);

            var original = await context.CommercialInvoicePayments.SingleAsync();

            await new InvoicePaymentService(context).ReverseAsync(
                seeded.Id, original.Id, "Bank returned the debit — insufficient funds.", userId: ReversingAdminId);

            var dto = await invoices.GetDetailAsync(seeded.Id);
            Assert.Equal(2, dto!.Payments.Count);

            var received = Assert.Single(dto.Payments.Where(p => !p.IsReversal));
            var reversal = Assert.Single(dto.Payments.Where(p => p.IsReversal));

            // The original is untouched: history says money arrived, because it did.
            Assert.Equal(925.43m, received.Amount);
            Assert.Equal(-925.43m, reversal.Amount);
            Assert.Equal("Bank returned the debit — insufficient funds.", reversal.InternalNote);

            // An audit trail names people: the reversal carries the admin who made it, while the
            // settlement has no admin behind it because a webhook wrote it.
            Assert.Equal("Rey Reverser", reversal.RecordedByName);
            Assert.Null(received.RecordedByName);

            // And the two still reconcile to the invoice.
            Assert.Equal(
                dto.AmountPaid,
                InvoiceCalculator.ResolveAmountPaid(dto.Payments.Select(p => p.Amount)));

            var activity = await context.CommercialInvoiceActivityLogs
                .Where(a => a.CommercialInvoiceId == seeded.Id)
                .ToListAsync();
            Assert.Contains(activity, a => a.Action == "payment_reversed");
        }

        /// <summary>
        /// A reversal is the ONLY thing allowed below zero, and it is the row that carries the
        /// meaning — not a minus painted on by whichever surface happens to be drawing.
        /// </summary>
        [Fact]
        public async Task OnlyAReversalRowIsEverNegative()
        {
            using var context = NewContext();
            var invoices = NewInvoiceService(context, NewBilling(context));
            var seeded = await SettleByAchAsync(context, invoices);

            var original = await context.CommercialInvoicePayments.SingleAsync();
            await new InvoicePaymentService(context).ReverseAsync(
                seeded.Id, original.Id, "Returned debit.", userId: ReversingAdminId);

            var rows = await context.CommercialInvoicePayments.ToListAsync();

            Assert.All(rows, r => Assert.True(
                r.IsReversal || r.Amount > 0m,
                $"Payment {r.Id} is {r.Amount} but is not a reversal."));
        }
    }
}
