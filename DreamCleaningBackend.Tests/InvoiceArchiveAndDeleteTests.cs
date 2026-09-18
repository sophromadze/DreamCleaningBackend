using System;
using System.Linq;
using System.Threading.Tasks;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models.Commercial;
using DreamCleaningBackend.Models.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// ARCHIVING AND PERMANENTLY DELETING AN INVOICE, at the data level.
    ///
    /// These exercise the QUERIES the admin list and the delete gate are built on, rather than
    /// constructing <c>InvoiceService</c> with its seven collaborators. What they pin is the part
    /// that would break silently: that the default list excludes archived rows and the Archived
    /// view shows only those, that the eligibility facts are gathered from the right tables, and
    /// that destroying an invoice leaves the shared client and the contract alone.
    ///
    /// The eligibility RULES themselves are asserted purely in
    /// <see cref="InvoiceHardDeletePolicyTests"/>.
    /// </summary>
    public class InvoiceArchiveAndDeleteTests
    {
        private static ApplicationDbContext NewContext() =>
            new(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase($"invoice-archive-{Guid.NewGuid()}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options);

        private static CommercialInvoice NewInvoice(int clientId, string number, int? contractId = null) =>
            new()
            {
                InvoiceNumber = number,
                PublicToken = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N").Substring(0, 16),
                ContractClientId = clientId,
                ContractId = contractId,
                CreatedByUserId = 1,
                InvoiceDate = DateTime.UtcNow.Date,
                DueDate = DateTime.UtcNow.Date.AddDays(15),
                Status = InvoiceStatus.Draft,
                Total = 500m
            };

        private static async Task<int> SeedClientAsync(ApplicationDbContext context)
        {
            var client = new ContractClient
            {
                LegalEntityName = "Northline Holdings Inc.",
                NoticeEmail = "ap@northline.example"
            };
            context.ContractClients.Add(client);
            await context.SaveChangesAsync();
            return client.Id;
        }

        /// <summary>The list query as the controller writes it: archived is its own axis.</summary>
        private static IQueryable<CommercialInvoice> ListQuery(
            ApplicationDbContext context, bool archived) =>
            context.CommercialInvoices.Where(i => i.IsArchived == archived);

        // ── archive ────────────────────────────────────────────────────────────

        /// <summary>
        /// ARCHIVED ROWS LEAVE THE DEFAULT LIST AND APPEAR UNDER THE ARCHIVED VIEW - and the two
        /// views PARTITION the invoices rather than one being a superset.
        ///
        /// A superset would leave no view that is just the live billing, which is the view an
        /// admin spends the day in.
        /// </summary>
        [Fact]
        public async Task ArchivingMovesAnInvoiceBetweenTheTwoViews()
        {
            using var context = NewContext();
            var clientId = await SeedClientAsync(context);

            var active = NewInvoice(clientId, "DCI-2026-00000001");
            var filed = NewInvoice(clientId, "DCI-2026-00000002");
            context.CommercialInvoices.AddRange(active, filed);
            await context.SaveChangesAsync();

            Assert.Equal(2, await ListQuery(context, archived: false).CountAsync());
            Assert.Empty(await ListQuery(context, archived: true).ToListAsync());

            filed.IsArchived = true;
            filed.ArchivedAt = DateTime.UtcNow;
            filed.ArchivedByUserId = 1;
            await context.SaveChangesAsync();

            var remaining = await ListQuery(context, archived: false).SingleAsync();
            Assert.Equal("DCI-2026-00000001", remaining.InvoiceNumber);

            var archived = await ListQuery(context, archived: true).SingleAsync();
            Assert.Equal("DCI-2026-00000002", archived.InvoiceNumber);
        }

        /// <summary>
        /// ARCHIVING IS NOT A STATUS CHANGE, and that separation is the whole reason it is its own
        /// column. A Paid invoice that has been filed away is still Paid - the status answers what
        /// happened to the money, and no filing decision may overwrite that answer.
        /// </summary>
        [Fact]
        public async Task ArchivingLeavesTheStatusAndTheFiguresAlone()
        {
            using var context = NewContext();
            var clientId = await SeedClientAsync(context);

            var invoice = NewInvoice(clientId, "DCI-2026-00000003");
            invoice.Status = InvoiceStatus.Paid;
            invoice.AmountPaid = 500m;
            invoice.PaidAt = DateTime.UtcNow;
            context.CommercialInvoices.Add(invoice);
            await context.SaveChangesAsync();

            invoice.IsArchived = true;
            invoice.ArchivedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();

            var reloaded = await context.CommercialInvoices.SingleAsync();
            Assert.True(reloaded.IsArchived);
            Assert.Equal(InvoiceStatus.Paid, reloaded.Status);
            Assert.Equal(500m, reloaded.AmountPaid);
            Assert.NotNull(reloaded.PaidAt);

            // And the public token is untouched — the client's copy keeps working, because
            // archiving is our filing decision and not a statement to them.
            Assert.False(string.IsNullOrWhiteSpace(reloaded.PublicToken));
        }

        /// <summary>Unarchiving puts it back and clears the bookkeeping columns with it.</summary>
        [Fact]
        public async Task UnarchivingRestoresItToTheDefaultList()
        {
            using var context = NewContext();
            var clientId = await SeedClientAsync(context);

            var invoice = NewInvoice(clientId, "DCI-2026-00000004");
            invoice.IsArchived = true;
            invoice.ArchivedAt = DateTime.UtcNow;
            invoice.ArchivedByUserId = 7;
            context.CommercialInvoices.Add(invoice);
            await context.SaveChangesAsync();

            invoice.IsArchived = false;
            invoice.ArchivedAt = null;
            invoice.ArchivedByUserId = null;
            await context.SaveChangesAsync();

            Assert.Single(await ListQuery(context, archived: false).ToListAsync());
            Assert.Empty(await ListQuery(context, archived: true).ToListAsync());

            var reloaded = await context.CommercialInvoices.SingleAsync();
            Assert.Null(reloaded.ArchivedAt);
            Assert.Null(reloaded.ArchivedByUserId);
        }

        // ── the eligibility facts ──────────────────────────────────────────────

        /// <summary>
        /// A PAYMENT ROW IS FOUND, which is what makes the invoice undeletable. This is the query
        /// half of the rule <see cref="InvoiceHardDeletePolicyTests"/> asserts purely.
        /// </summary>
        [Fact]
        public async Task APaymentRowIsCountedAgainstTheInvoice()
        {
            using var context = NewContext();
            var clientId = await SeedClientAsync(context);

            var invoice = NewInvoice(clientId, "DCI-2026-00000005");
            context.CommercialInvoices.Add(invoice);
            await context.SaveChangesAsync();

            context.CommercialInvoicePayments.Add(new CommercialInvoicePayment
            {
                CommercialInvoiceId = invoice.Id,
                Amount = 500m,
                PaymentDate = DateTime.UtcNow.Date,
                RecordedByUserId = 1
            });
            await context.SaveChangesAsync();

            Assert.Equal(1, await context.CommercialInvoicePayments
                .CountAsync(p => p.CommercialInvoiceId == invoice.Id));
        }

        /// <summary>
        /// ONLY ATTEMPTS THAT REACHED STRIPE COUNT.
        ///
        /// A row created and abandoned before the API call carries neither a session nor an intent
        /// id - it is ours alone, exists on nobody else's ledger, and must not permanently pin a
        /// test invoice in place.
        /// </summary>
        [Fact]
        public async Task OnlyAttemptsCarryingAStripeIdProtectTheInvoice()
        {
            using var context = NewContext();
            var clientId = await SeedClientAsync(context);

            var invoice = NewInvoice(clientId, "DCI-2026-00000006");
            context.CommercialInvoices.Add(invoice);
            await context.SaveChangesAsync();

            context.CommercialInvoicePaymentAttempts.Add(new CommercialInvoicePaymentAttempt
            {
                CommercialInvoiceId = invoice.Id,
                Amount = 500m,
                Status = InvoicePaymentAttemptStatus.Created
            });
            await context.SaveChangesAsync();

            var external = await context.CommercialInvoicePaymentAttempts
                .CountAsync(a => a.CommercialInvoiceId == invoice.Id
                                 && (a.StripeCheckoutSessionId != null || a.StripePaymentIntentId != null));
            Assert.Equal(0, external);

            var abandoned = await context.CommercialInvoicePaymentAttempts.SingleAsync();
            abandoned.StripeCheckoutSessionId = "cs_test_123";
            await context.SaveChangesAsync();

            external = await context.CommercialInvoicePaymentAttempts
                .CountAsync(a => a.CommercialInvoiceId == invoice.Id
                                 && (a.StripeCheckoutSessionId != null || a.StripePaymentIntentId != null));
            Assert.Equal(1, external);
        }

        // ── what a delete may not take with it ─────────────────────────────────

        /// <summary>
        /// DELETING AN INVOICE LEAVES THE SHARED CLIENT, THE CONTRACT AND THE OTHER INVOICES ALONE.
        ///
        /// Those FKs are Restrict and SetNull rather than Cascade precisely so that destroying one
        /// bill cannot take a company's commercial record — or its other billing — with it.
        /// </summary>
        [Fact]
        public async Task DeletingAnInvoiceLeavesTheClientContractAndOtherInvoices()
        {
            using var context = NewContext();
            var clientId = await SeedClientAsync(context);

            var contract = new Contract
            {
                ContractNumber = "DCC-2026-48392175",
                ContractClientId = clientId,
                ContractServiceLocationId = 1,
                ContractorProfileId = 1,
                ContractTemplateId = 1,
                CreatedByAdminId = 1
            };
            context.Contracts.Add(contract);
            await context.SaveChangesAsync();

            var doomed = NewInvoice(clientId, "DCI-2026-00000007", contract.Id);
            var keeper = NewInvoice(clientId, "DCI-2026-00000008", contract.Id);
            context.CommercialInvoices.AddRange(doomed, keeper);
            await context.SaveChangesAsync();

            context.CommercialInvoiceItems.Add(new CommercialInvoiceItem
            {
                CommercialInvoiceId = doomed.Id,
                Description = "Monthly cleaning",
                Quantity = 1m,
                UnitPrice = 500m,
                Amount = 500m
            });
            await context.SaveChangesAsync();

            // Loaded so the in-memory provider cascades to the items the way MySQL's FK does.
            var toDelete = await context.CommercialInvoices
                .Include(i => i.Items)
                .FirstAsync(i => i.Id == doomed.Id);
            context.CommercialInvoices.Remove(toDelete);
            await context.SaveChangesAsync();

            // The invoice and its OWN line items are gone.
            Assert.Null(await context.CommercialInvoices.FindAsync(doomed.Id));
            Assert.Empty(await context.CommercialInvoiceItems
                .Where(i => i.CommercialInvoiceId == doomed.Id).ToListAsync());

            // Everything shared is untouched.
            Assert.NotNull(await context.ContractClients.FindAsync(clientId));
            Assert.NotNull(await context.Contracts.FindAsync(contract.Id));

            var surviving = await context.CommercialInvoices.SingleAsync();
            Assert.Equal("DCI-2026-00000008", surviving.InvoiceNumber);
            Assert.Equal(contract.Id, surviving.ContractId);
        }

        // ── void is unchanged ──────────────────────────────────────────────────

        /// <summary>
        /// VOID STILL MEANS WHAT IT MEANT. The row stays, the number stays reserved, and the
        /// status is the permanent record that it was issued and cancelled — none of which the
        /// archive flag touches, and both can be true at once.
        /// </summary>
        [Fact]
        public async Task AVoidedInvoiceCanAlsoBeArchivedWithoutLosingItsVoidRecord()
        {
            using var context = NewContext();
            var clientId = await SeedClientAsync(context);

            var invoice = NewInvoice(clientId, "DCI-2026-00000009");
            invoice.Status = InvoiceStatus.Void;
            invoice.VoidedAt = DateTime.UtcNow;
            invoice.VoidReason = "Issued in error";
            invoice.VoidedByUserId = 1;
            context.CommercialInvoices.Add(invoice);
            await context.SaveChangesAsync();

            invoice.IsArchived = true;
            invoice.ArchivedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();

            var reloaded = await context.CommercialInvoices.SingleAsync();
            Assert.Equal(InvoiceStatus.Void, reloaded.Status);
            Assert.Equal("Issued in error", reloaded.VoidReason);
            Assert.NotNull(reloaded.VoidedAt);
            Assert.True(reloaded.IsArchived);
            Assert.Equal("DCI-2026-00000009", reloaded.InvoiceNumber);
        }
    }
}
