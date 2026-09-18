using System;
using System.Linq;
using System.Threading.Tasks;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models.Commercial;
using DreamCleaningBackend.Models.Contracts;
using DreamCleaningBackend.Services.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// PERMANENT DELETION, END TO END: what goes, what stays, and what refuses.
    ///
    /// <see cref="ContractPurgeService"/> is the ONE place a contract is destroyed, shared by the
    /// admin's Full delete button and the retention sweep. Both being the same code is the point -
    /// before it existed the sweep hard-deleted anything archived for six months, executed
    /// agreements included, unattended and with no decision behind it.
    ///
    /// A NOTE ON THE PROVIDER: MySQL performs the child deletes through the FK cascades configured
    /// in <c>ApplicationDbContext</c>. The in-memory provider only cascades to entities the change
    /// tracker has loaded, so the tests below load the graph first - which proves the cascade
    /// WIRING is right without pretending the provider is the real one.
    /// </summary>
    public class ContractPurgeTests
    {
        private static ApplicationDbContext NewContext(string name) =>
            new(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(name)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options);

        /// <summary>
        /// A storage root under the temp directory. <see cref="ContractStorage"/> creates its root
        /// on construction, so the test gets its own rather than touching the configured one.
        /// </summary>
        private static ContractStorage TestStorage()
        {
            var root = Path.Combine(Path.GetTempPath(), "dc-contract-purge-tests", Guid.NewGuid().ToString("N"));
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Contracts:StoragePath"] = root })
                .Build();
            return new ContractStorage(configuration, new PurgeTestEnvironment(root));
        }

        private static ContractPurgeService Purge(ApplicationDbContext context) =>
            new(context, TestStorage(), NullLogger<ContractPurgeService>.Instance);

        /// <summary>
        /// A contract with a full graph under it, plus a SHARED client that other records also
        /// point at. The shared half is what the "nothing else follows it out" assertions need.
        /// </summary>
        private static async Task<(int ContractId, int ClientId)> SeedAsync(
            ApplicationDbContext context, bool signed = false, bool withInvoice = false)
        {
            var client = new ContractClient
            {
                LegalEntityName = "Northline Holdings Inc.",
                NoticeEmail = "ap@northline.example"
            };
            context.ContractClients.Add(client);
            await context.SaveChangesAsync();

            var contract = new Contract
            {
                ContractNumber = "DCC-2026-48392175",
                ContractClientId = client.Id,
                ContractServiceLocationId = 1,
                ContractorProfileId = 1,
                ContractTemplateId = 1,
                CreatedByAdminId = 1,
                Status = signed ? ContractStatus.FullySigned : ContractStatus.PreviewGenerated,
                IsHidden = true,
                HiddenAt = DateTime.UtcNow.AddMonths(-7)
            };
            context.Contracts.Add(contract);
            await context.SaveChangesAsync();

            var version = new ContractVersion
            {
                ContractId = contract.Id,
                VersionNumber = 1,
                DocumentHashSha256 = new string('a', 64),
                GeneratedByAdminId = 1
            };
            context.ContractVersions.Add(version);
            await context.SaveChangesAsync();

            contract.CurrentVersionId = version.Id;

            var signer = new ContractSigner
            {
                ContractVersionId = version.Id,
                Role = ContractSignerRole.ClientSigner,
                SigningToken = new string('b', 48),
                TokenExpiresAt = DateTime.UtcNow.AddDays(30),
                InvitedName = "Dana Okafor"
            };
            context.ContractSigners.Add(signer);

            context.ContractFiles.Add(new ContractFile
            {
                ContractVersionId = version.Id,
                FileType = ContractFileType.Preview,
                FilePath = "contracts/preview-1.pdf",
                FileName = "preview-1.pdf"
            });

            context.ContractAuditLogs.Add(new ContractAuditLog
            {
                ContractId = contract.Id,
                EventType = "Created",
                EventDescription = "Contract created",
                ActorType = ContractActorType.Admin
            });

            await context.SaveChangesAsync();

            if (signed)
            {
                context.ContractSignatures.Add(new ContractSignature
                {
                    ContractSignerId = signer.Id,
                    SignerNameAtSigning = "Dana Okafor",
                    SignatureImageOrTypedText = "Dana Okafor",
                    DocumentHashAtSigning = new string('a', 64)
                });
            }

            if (withInvoice)
            {
                context.CommercialInvoices.Add(new CommercialInvoice
                {
                    InvoiceNumber = "DCI-2026-11112222",
                    PublicToken = new string('c', 48),
                    ContractClientId = client.Id,
                    ContractId = contract.Id,
                    CreatedByUserId = 1,
                    InvoiceDate = DateTime.UtcNow.Date,
                    DueDate = DateTime.UtcNow.Date.AddDays(15)
                });
            }

            // An UNRELATED invoice for the same client. It must survive, because deleting a
            // contract is not a reason to touch that client's other billing.
            context.CommercialInvoices.Add(new CommercialInvoice
            {
                InvoiceNumber = "DCI-2026-99998888",
                PublicToken = new string('d', 48),
                ContractClientId = client.Id,
                ContractId = null,
                CreatedByUserId = 1,
                InvoiceDate = DateTime.UtcNow.Date,
                DueDate = DateTime.UtcNow.Date.AddDays(15)
            });

            await context.SaveChangesAsync();
            return (contract.Id, client.Id);
        }

        /// <summary>Loads the whole owned graph so the in-memory provider cascades like MySQL.</summary>
        private static Task<Contract> LoadGraphAsync(ApplicationDbContext context, int id) =>
            context.Contracts
                .Include(c => c.ContractClient)
                .Include(c => c.HiddenByUser)
                .Include(c => c.Versions).ThenInclude(v => v.Signers).ThenInclude(s => s.Signature)
                .Include(c => c.Versions).ThenInclude(v => v.Files)
                .FirstAsync(c => c.Id == id);

        // ── the happy path ─────────────────────────────────────────────────────

        /// <summary>
        /// A TEST CONTRACT IS DESTROYED COMPLETELY, and takes every row that existed only because
        /// of it: versions, signers, files and its own audit trail.
        /// </summary>
        [Fact]
        public async Task AnUnsignedContractIsDestroyedWithEverythingItOwns()
        {
            using var context = NewContext($"purge-happy-{Guid.NewGuid()}");
            var (contractId, _) = await SeedAsync(context);

            await Purge(context).PurgeAsync(await LoadGraphAsync(context, contractId), "Nodar Alania");

            Assert.Empty(await context.Contracts.Where(c => c.Id == contractId).ToListAsync());
            Assert.Empty(await context.ContractVersions.Where(v => v.ContractId == contractId).ToListAsync());
            Assert.Empty(await context.ContractSigners.ToListAsync());
            Assert.Empty(await context.ContractFiles.ToListAsync());
            Assert.Empty(await context.ContractAuditLogs.Where(a => a.ContractId == contractId).ToListAsync());
        }

        /// <summary>
        /// THE DELETION LOG SURVIVES, because it is the only thing that does.
        ///
        /// <c>ContractAuditLog</c> rows cascade away with the contract they describe, so the audit
        /// trail of a permanent deletion would be destroyed by the very act it records. That is
        /// why <c>ContractDeletionLog</c> has no foreign key - and why it is written inside the
        /// same transaction as the delete rather than before it.
        /// </summary>
        [Fact]
        public async Task ADeletionLogRowOutlivesTheContract()
        {
            using var context = NewContext($"purge-log-{Guid.NewGuid()}");
            var (contractId, _) = await SeedAsync(context);

            await Purge(context).PurgeAsync(await LoadGraphAsync(context, contractId), "Nodar Alania");

            var log = await context.ContractDeletionLogs.SingleAsync();
            Assert.Equal("DCC-2026-48392175", log.ContractNumber);
            Assert.Equal(contractId, log.ContractId);
            Assert.Equal("Northline Holdings Inc.", log.ClientLegalName);
            Assert.Equal("PreviewGenerated", log.StatusAtDeletion);
            Assert.Equal(1, log.VersionCount);
            Assert.Equal(1, log.FileCount);

            // The actor column distinguishes a manual purge from the timer, which is exactly what
            // it was put there for.
            Assert.Equal("Nodar Alania", log.DeletedBy);
        }

        // ── what must NOT follow it out ────────────────────────────────────────

        /// <summary>
        /// THE SHARED CLIENT SURVIVES, and so does that client's unrelated billing.
        ///
        /// A ContractClient is shared with other contracts, invoices and a customer account.
        /// Deleting one contract is not a statement about the company it was with, which is why
        /// the FK is Restrict rather than Cascade.
        /// </summary>
        [Fact]
        public async Task TheSharedClientAndItsUnrelatedInvoiceBothSurvive()
        {
            using var context = NewContext($"purge-shared-{Guid.NewGuid()}");
            var (contractId, clientId) = await SeedAsync(context);

            await Purge(context).PurgeAsync(await LoadGraphAsync(context, contractId), "Nodar Alania");

            Assert.NotNull(await context.ContractClients.FindAsync(clientId));

            var invoice = await context.CommercialInvoices.SingleAsync();
            Assert.Equal("DCI-2026-99998888", invoice.InvoiceNumber);
            Assert.Equal(clientId, invoice.ContractClientId);
        }

        // ── what refuses ───────────────────────────────────────────────────────

        /// <summary>
        /// A SIGNED CONTRACT REFUSES, and nothing is removed on the way to finding out.
        ///
        /// This is the case the retention sweep used to destroy silently. The refusal comes from
        /// the shared policy, so the button and the timer answer identically.
        /// </summary>
        [Fact]
        public async Task ASignedContractIsRefusedAndNothingIsTouched()
        {
            using var context = NewContext($"purge-signed-{Guid.NewGuid()}");
            var (contractId, _) = await SeedAsync(context, signed: true);

            var contract = await LoadGraphAsync(context, contractId);
            var ex = await Assert.ThrowsAsync<ContractPurgeRefusedException>(
                () => Purge(context).PurgeAsync(contract, "Nodar Alania"));

            Assert.Contains("signature", ex.Message, StringComparison.OrdinalIgnoreCase);

            // Still entirely there, and no deletion log was written for something that did not
            // happen.
            Assert.NotNull(await context.Contracts.FindAsync(contractId));
            Assert.Single(await context.ContractVersions.ToListAsync());
            Assert.Empty(await context.ContractDeletionLogs.ToListAsync());
        }

        /// <summary>
        /// AN INVOICE RAISED AGAINST THE CONTRACT REFUSES.
        ///
        /// That FK is SetNull, so the delete would NOT fail - it would quietly detach a real
        /// financial document from the agreement it was raised under. Refusing is the point.
        /// </summary>
        [Fact]
        public async Task AContractWithAnInvoiceAgainstItIsRefused()
        {
            using var context = NewContext($"purge-invoiced-{Guid.NewGuid()}");
            var (contractId, _) = await SeedAsync(context, withInvoice: true);
            var contract = await LoadGraphAsync(context, contractId);
            var ex = await Assert.ThrowsAsync<ContractPurgeRefusedException>(
                () => Purge(context).PurgeAsync(contract, "Nodar Alania"));

            Assert.Contains("invoice", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(await context.Contracts.FindAsync(contractId));
        }

        /// <summary>
        /// The same question the detail endpoint asks to decide whether to OFFER the option, so
        /// the dialog and the endpoint cannot disagree about what will happen.
        /// </summary>
        [Fact]
        public async Task DescribeBlockerAnswersTheSameQuestionTheDialogAsks()
        {
            using var context = NewContext($"purge-blocker-{Guid.NewGuid()}");
            var (cleanId, _) = await SeedAsync(context);

            Assert.Null(await Purge(context).DescribeBlockerAsync(
                await context.Contracts.FirstAsync(c => c.Id == cleanId)));

            using var signedContext = NewContext($"purge-blocker-signed-{Guid.NewGuid()}");
            var (signedId, _) = await SeedAsync(signedContext, signed: true);

            Assert.NotNull(await Purge(signedContext).DescribeBlockerAsync(
                await signedContext.Contracts.FirstAsync(c => c.Id == signedId)));
        }
    }

    /// <summary>
    /// The minimum <see cref="IWebHostEnvironment"/> <see cref="ContractStorage"/> needs. It only
    /// reads <c>ContentRootPath</c>, and only when no storage path is configured — which these
    /// tests always configure, so this exists to satisfy the constructor rather than to be used.
    /// </summary>
    internal sealed class PurgeTestEnvironment : IWebHostEnvironment
    {
        public PurgeTestEnvironment(string root)
        {
            ContentRootPath = root;
            WebRootPath = root;
        }

        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Tests";
        public string WebRootPath { get; set; }
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
