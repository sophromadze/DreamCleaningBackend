using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using DreamCleaningBackend.Controllers.Admin;
using DreamCleaningBackend.Controllers.Crm;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.DTOs.Commercial;
using DreamCleaningBackend.Helpers.Contracts;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Models.Commercial;
using DreamCleaningBackend.Models.Contracts;
using DreamCleaningBackend.Services;
using DreamCleaningBackend.Services.Commercial;
using DreamCleaningBackend.Services.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// A BUSINESS-FLAGGED CUSTOMER *IS* A COMMERCIAL CLIENT (2026-09).
    ///
    /// Staff should say it once. Ticking "business" on a customer account is the whole gesture:
    /// the matching <c>ContractClient</c> appears, and it is an ordinary commercial client from
    /// then on — invoiceable, contractable, editable, with or without an agreement.
    ///
    /// <b>ContractClient is not replaced and nothing is re-architected around User.</b> It stays
    /// the commercial legal/billing entity that contracts, invoices, billing contacts and service
    /// locations hang off, and a STANDALONE client (no website account at all) stays a first-class
    /// citizen. The link is one nullable column.
    ///
    /// The three properties this file exists to pin:
    ///
    ///  1. <b>Exactly one client per account, forever.</b> Off-and-on-again reactivates the SAME
    ///     row — with its contracts, its invoices and any name staff corrected — rather than
    ///     creating a second one. The unique index is the final guard, not the code path.
    ///  2. <b>Nothing is ever hard-deleted.</b> Delete deactivates. Every contract, invoice,
    ///     payment and reference number survives and still resolves the client.
    ///  3. <b>The link is deliberate, never guessed.</b> No email, name or address match creates
    ///     one — including in the backfill, which is the easiest place to have got that wrong.
    /// </summary>
    public class BusinessCommercialClientTests
    {
        // ── Fixtures ──────────────────────────────────────────────────────────────────────────

        private static ApplicationDbContext NewContext() =>
            new(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase($"business-client-{Guid.NewGuid()}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options);

        private const int AdminId = 1;
        private const int BusinessUserId = 55;
        private const int SecondBusinessUserId = 56;
        private const int ResidentialUserId = 57;

        private static AuditService NewAudit(ApplicationDbContext context) =>
            new(context, new HttpContextAccessor(), NullLogger<AuditService>.Instance);

        private static BusinessClientService NewService(ApplicationDbContext context) =>
            new(context, NewAudit(context), NullLogger<BusinessClientService>.Instance);

        /// <summary>
        /// The signed-in admin, as the controller reads it. Needed because every write here is
        /// audited, and an audit row that cannot say WHO did it is the one thing worse than none.
        /// </summary>
        private static ControllerContext AsAdmin() => new()
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("UserId", AdminId.ToString()),
                    new Claim("Role", nameof(UserRole.SuperAdmin))
                }, "Test"))
            }
        };

        private static CrmContractDirectoryController NewDirectory(ApplicationDbContext context) =>
            new(context, NewService(context), NewAudit(context)) { ControllerContext = AsAdmin() };

        private static AdminCommercialInvoicesController NewInvoicesController(
            ApplicationDbContext context) =>
            new(context, null!, null!, null!, null!, null!, null!);

        private static InvoiceService NewInvoiceService(ApplicationDbContext context) =>
            new(context,
                new InvoiceNumberService(context, NullLogger<InvoiceNumberService>.Instance),
                new BillingSettingsService(context),
                new ConfigurationBuilder().Build(),
                NullLogger<InvoiceService>.Instance);

        private static User Customer(
            int id, string first, string last, string email,
            bool isBusiness = false, bool isActive = true) => new()
            {
                Id = id,
                FirstName = first,
                LastName = last,
                Email = email,
                Phone = "7185550123",
                PasswordHash = "x",
                PasswordSalt = "x",
                Role = UserRole.Customer,
                IsBusiness = isBusiness,
                IsActive = isActive
            };

        private static async Task<User> SeedBusinessAccountAsync(
            ApplicationDbContext context, int id = BusinessUserId, bool withAddress = true)
        {
            context.Users.Add(Customer(AdminId, "Ada", "Admin", "admin@example.invalid"));
            var user = Customer(id, "Casey", "Client", $"casey{id}@chicktastic.invalid", isBusiness: true);
            context.Users.Add(user);

            if (withAddress)
            {
                context.Apartments.AddRange(
                    new Apartment
                    {
                        Id = 900 + id, UserId = id, Name = "Shop",
                        Address = "1569 Flatbush Ave.", City = "Brooklyn", State = "NY",
                        PostalCode = "11210", IsActive = true
                    },
                    // Newer, and deliberately different: the OLDEST active one is the account's
                    // address, so picking this would be the bug.
                    new Apartment
                    {
                        Id = 9500 + id, UserId = id, Name = "Second",
                        Address = "9 Later Street", City = "Queens", State = "NY",
                        PostalCode = "11375", IsActive = true
                    });
            }

            await context.SaveChangesAsync();
            return user;
        }

        private static T Body<T>(ActionResult<T> result) where T : class
        {
            var ok = Assert.IsType<OkObjectResult>(result.Result);
            return Assert.IsType<T>(ok.Value);
        }

        private static CreateCommercialClientDto StandaloneDto(string name = "No Account Ltd") => new()
        {
            LegalEntityName = name,
            EntityType = "a limited liability company",
            PrincipalAddress = "1 Standalone Way",
            City = "Brooklyn",
            State = "NY",
            Zip = "11226"
        };

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  1-2. Backfill, and the automatic link
        // ══════════════════════════════════════════════════════════════════════════════════════

        [Fact]
        public async Task AnExistingBusinessAccountIsBackfilledIntoCommercialClients()
        {
            using var context = NewContext();
            var user = await SeedBusinessAccountAsync(context);

            var created = await NewService(context).BackfillAsync();

            Assert.Equal(1, created);

            var client = await context.ContractClients.SingleAsync();
            Assert.Equal(user.Id, client.SourceUserId);
            Assert.True(client.IsActive);

            // Mapped from fields that exist, nothing invented.
            Assert.Equal("Casey Client", client.LegalEntityName);
            Assert.Equal("casey55@chicktastic.invalid", client.NoticeEmail);
            Assert.Equal("7185550123", client.Phone);

            // The OLDEST active apartment, not the newest.
            Assert.Equal("1569 Flatbush Ave.", client.PrincipalAddress);
            Assert.Equal("Brooklyn", client.City);
            Assert.Equal("11210", client.Zip);

            // Deliberately blank: EntityType is printed on a contract as a legal characterisation
            // of the counterparty, so a default would be a statement nobody checked.
            Assert.Equal(string.Empty, client.EntityType);
        }

        [Fact]
        public async Task TheBackfillIsIdempotent()
        {
            using var context = NewContext();
            await SeedBusinessAccountAsync(context);
            var service = NewService(context);

            Assert.Equal(1, await service.BackfillAsync());
            Assert.Equal(0, await service.BackfillAsync());
            Assert.Equal(0, await service.BackfillAsync());

            Assert.Single(context.ContractClients);
        }

        [Fact]
        public async Task TheBackfillIgnoresNonBusinessAndInactiveAccounts()
        {
            using var context = NewContext();
            context.Users.AddRange(
                Customer(ResidentialUserId, "Robin", "Resident", "robin@example.invalid"),
                Customer(58, "Gone", "Away", "gone@example.invalid", isBusiness: true, isActive: false),
                new User
                {
                    Id = 59, FirstName = "Staff", LastName = "Member",
                    Email = "staff@example.invalid", PasswordHash = "x", PasswordSalt = "x",
                    Role = UserRole.Admin, IsBusiness = true, IsActive = true
                });
            await context.SaveChangesAsync();

            Assert.Equal(0, await NewService(context).BackfillAsync());
            Assert.Empty(context.ContractClients);
        }

        [Fact]
        public async Task TheBackfillNeverAdoptsAnExistingStandaloneClientByEmail()
        {
            using var context = NewContext();
            var user = await SeedBusinessAccountAsync(context);

            // A standalone client carrying the SAME email and the SAME name as the account. It is
            // somebody's manual entry, and nothing may quietly claim it - a link grants that
            // customer sight of this company's contracts.
            context.ContractClients.Add(new ContractClient
            {
                LegalEntityName = "Casey Client",
                EntityType = "a limited liability company",
                PrincipalAddress = "1569 Flatbush Ave.",
                City = "Brooklyn", State = "NY", Zip = "11210",
                NoticeEmail = user.Email,
                SourceUserId = null
            });
            await context.SaveChangesAsync();

            Assert.Equal(1, await NewService(context).BackfillAsync());

            var clients = await context.ContractClients.OrderBy(c => c.Id).ToListAsync();
            Assert.Equal(2, clients.Count);
            Assert.Null(clients[0].SourceUserId);            // the manual one, left alone
            Assert.Equal(user.Id, clients[1].SourceUserId);  // a new, separately linked one
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  3-4. One account, one client
        // ══════════════════════════════════════════════════════════════════════════════════════

        [Fact]
        public async Task TurningTheFlagOnTwiceProducesOneClient()
        {
            using var context = NewContext();
            var user = await SeedBusinessAccountAsync(context);
            var service = NewService(context);

            await service.ApplyBusinessFlagAsync(user, true, AdminId);
            await service.ApplyBusinessFlagAsync(user, true, AdminId);
            await service.EnsureLinkedClientAsync(user);
            await context.SaveChangesAsync();

            Assert.Single(context.ContractClients);
        }

        [Fact]
        public void TheSourceUserIdLinkIsUniqueInTheModel()
        {
            // The application check is not the guarantee - a race would slip past it and split one
            // client's contracts and invoices across two rows. UNIQUE is also the correct MySQL
            // spelling of "at most one non-null": the engine never treats two NULLs as equal, so
            // any number of standalone clients still coexist.
            using var context = NewContext();

            var index = context.Model
                .FindEntityType(typeof(ContractClient))!
                .GetIndexes()
                .Single(i => i.Properties.Count == 1
                             && i.Properties[0].Name == nameof(ContractClient.SourceUserId));

            Assert.True(index.IsUnique,
                "ContractClient.SourceUserId must carry a UNIQUE index - one business account may " +
                "never own two commercial clients.");
        }

        [Fact]
        public async Task ManyStandaloneClientsCoexistWithNullLinks()
        {
            // The other half of the uniqueness rule: NULLs do not collide.
            using var context = NewContext();
            var directory = NewDirectory(context);

            await directory.CreateClient(StandaloneDto("First Ltd"));
            await directory.CreateClient(StandaloneDto("Second Ltd"));
            await directory.CreateClient(StandaloneDto("Third Ltd"));

            var clients = await context.ContractClients.ToListAsync();
            Assert.Equal(3, clients.Count);
            Assert.All(clients, c => Assert.Null(c.SourceUserId));
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  5-7. On, off, and on again
        // ══════════════════════════════════════════════════════════════════════════════════════

        [Fact]
        public async Task TurningTheFlagOnCreatesTheClient()
        {
            using var context = NewContext();
            var user = await SeedBusinessAccountAsync(context);
            user.IsBusiness = false;
            await context.SaveChangesAsync();

            var outcome = await NewService(context).ApplyBusinessFlagAsync(user, true, AdminId);

            Assert.Equal(BusinessClientService.LinkOutcome.Created, outcome);
            Assert.True((await context.ContractClients.SingleAsync()).IsActive);
            Assert.True(user.IsBusiness);
        }

        [Fact]
        public async Task TurningTheFlagOffDeactivatesTheClientWithoutDeletingAnything()
        {
            using var context = NewContext();
            var user = await SeedBusinessAccountAsync(context);
            var service = NewService(context);
            await service.ApplyBusinessFlagAsync(user, true, AdminId);

            var outcome = await service.ApplyBusinessFlagAsync(user, false, AdminId);

            Assert.Equal(BusinessClientService.LinkOutcome.Deactivated, outcome);

            var client = await context.ContractClients.SingleAsync();
            Assert.False(client.IsActive);
            Assert.Equal(user.Id, client.SourceUserId);   // the link survives, ready to come back
            Assert.False(user.IsBusiness);
        }

        [Fact]
        public async Task TurningItBackOnReactivatesTheSAMERowRatherThanCreatingASecond()
        {
            using var context = NewContext();
            var user = await SeedBusinessAccountAsync(context);
            var service = NewService(context);

            await service.ApplyBusinessFlagAsync(user, true, AdminId);
            var originalId = (await context.ContractClients.SingleAsync()).Id;

            // Staff correct the seeded name to the real registered entity - work that must survive
            // the round trip, which a "delete and recreate" implementation would throw away.
            var stored = await context.ContractClients.SingleAsync();
            stored.LegalEntityName = "Chick Tastic LLC";
            await context.SaveChangesAsync();

            await service.ApplyBusinessFlagAsync(user, false, AdminId);
            var outcome = await service.ApplyBusinessFlagAsync(user, true, AdminId);

            Assert.Equal(BusinessClientService.LinkOutcome.Reactivated, outcome);

            var client = await context.ContractClients.SingleAsync();
            Assert.Equal(originalId, client.Id);
            Assert.Equal("Chick Tastic LLC", client.LegalEntityName);
            Assert.True(client.IsActive);
        }

        [Fact]
        public async Task ReapplyingTheSameStateIsANoOp()
        {
            using var context = NewContext();
            var user = await SeedBusinessAccountAsync(context);
            var service = NewService(context);

            await service.ApplyBusinessFlagAsync(user, true, AdminId);

            Assert.Equal(
                BusinessClientService.LinkOutcome.Unchanged,
                await service.ApplyBusinessFlagAsync(user, true, AdminId));

            await service.ApplyBusinessFlagAsync(user, false, AdminId);

            Assert.Equal(
                BusinessClientService.LinkOutcome.Unchanged,
                await service.ApplyBusinessFlagAsync(user, false, AdminId));

            Assert.Single(context.ContractClients);
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  8-11. One roster, and invoicing off it
        // ══════════════════════════════════════════════════════════════════════════════════════

        [Fact]
        public async Task LinkedAndStandaloneClientsAppearInOneList()
        {
            using var context = NewContext();
            var user = await SeedBusinessAccountAsync(context);
            await NewService(context).ApplyBusinessFlagAsync(user, true, AdminId);
            await NewDirectory(context).CreateClient(StandaloneDto("No Account Ltd"));

            var options = Body(await NewInvoicesController(context).Clients());

            Assert.Equal(2, options.Count);

            var linked = options.Single(o => o.SourceUserId != null);
            var standalone = options.Single(o => o.SourceUserId == null);

            Assert.Equal(user.Id, linked.SourceUserId);
            Assert.Equal("Casey Client", linked.LinkedAccountName);
            Assert.Equal("casey55@chicktastic.invalid", linked.LinkedAccountEmail);
            Assert.Equal("No Account Ltd", standalone.LegalEntityName);

            // Both are ordinary, invoiceable clients. The badge is the only difference.
            Assert.All(options, o => Assert.True(o.IsActive));
        }

        [Fact]
        public async Task AnInvoiceIsCreatedForALinkedClientWithNoContract()
        {
            using var context = NewContext();
            var user = await SeedBusinessAccountAsync(context);
            await NewService(context).ApplyBusinessFlagAsync(user, true, AdminId);
            var client = await context.ContractClients.SingleAsync();

            var invoice = await NewInvoiceService(context).CreateAsync(new SaveInvoiceDto
            {
                ContractClientId = client.Id,
                ContractId = null,
                InvoiceDate = new DateTime(2026, 9, 8),
                Items = { new SaveInvoiceItemDto { Description = "September", Quantity = 1m, UnitPrice = 500m } }
            }, AdminId);

            Assert.Null(invoice.ContractId);
            Assert.Equal(client.Id, invoice.ContractClientId);
            Assert.Equal(500m, invoice.Total);
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  12-13. Editing
        // ══════════════════════════════════════════════════════════════════════════════════════

        [Fact]
        public async Task AClientIsEdited_AndThePhoneIsWrittenThroughToTheLinkedAccount()
        {
            using var context = NewContext();
            var user = await SeedBusinessAccountAsync(context);
            await NewService(context).ApplyBusinessFlagAsync(user, true, AdminId);
            var client = await context.ContractClients.SingleAsync();

            var result = Body(await NewDirectory(context).UpdateClient(client.Id, new UpdateCommercialClientDto
            {
                LegalEntityName = "Chick Tastic LLC",
                EntityType = "a limited liability company",
                PrincipalAddress = "1569 Flatbush Ave.",
                City = "Brooklyn",
                State = "NY",
                Zip = "11210",
                NoticeEmail = "accounts@chicktastic.invalid",
                Phone = "7185559999",
                BillingContact = new SaveContractContactDto
                {
                    FirstName = "Casey", LastName = "Client", Email = "casey@chicktastic.invalid"
                },
                ServiceLocation = new SaveContractServiceLocationDto
                {
                    BusinessBrand = "Chick Tastic",
                    Address = "1569 Flatbush Ave.", City = "Brooklyn", State = "NY", Zip = "11210"
                }
            }));

            Assert.Equal("Chick Tastic LLC", result.LegalEntityName);
            Assert.Single(context.ContractContacts);
            Assert.Single(context.ContractServiceLocations);

            var account = await context.Users.SingleAsync(u => u.Id == user.Id);

            // THE one field that syncs: same fact about the same party, no machinery behind it.
            Assert.Equal("7185559999", account.Phone);

            // And the ones that deliberately do NOT. Email is a login identity with a verification
            // flow behind it; the account's name is a person's, not the company's.
            Assert.Equal("casey55@chicktastic.invalid", account.Email);
            Assert.Equal("Casey", account.FirstName);
        }

        [Fact]
        public async Task EditingAClientNeverRewritesASignedContract()
        {
            // The contract-history protection, and it is structural rather than a policy: every
            // version renders from its own frozen snapshot taken at generation time.
            using var context = NewContext();
            await NewDirectory(context).CreateClient(StandaloneDto("Old Name Ltd"));
            var client = await context.ContractClients.SingleAsync();

            var snapshot = new ContractSnapshot();
            snapshot.Client.LegalEntityName = "Old Name Ltd";

            context.Contracts.Add(new Contract
            {
                Id = 70, ContractNumber = "DCC-2026-33334444",
                ContractClientId = client.Id, ContractorProfileId = 1,
                ContractTemplateId = 1, ContractServiceLocationId = 1,
                Status = ContractStatus.FullySigned
            });
            context.ContractVersions.Add(new ContractVersion
            {
                Id = 700, ContractId = 70, VersionNumber = 1,
                FullSnapshotJson = snapshot.ToJson()
            });
            await context.SaveChangesAsync();

            await NewDirectory(context).UpdateClient(client.Id, new UpdateCommercialClientDto
            {
                LegalEntityName = "Brand New Name LLC",
                EntityType = "a limited liability company",
                PrincipalAddress = "9 Somewhere Else",
                City = "Queens", State = "NY", Zip = "11375"
            });

            var version = await context.ContractVersions.SingleAsync();
            var frozen = ContractSnapshot.Parse(version.FullSnapshotJson);

            Assert.Equal("Old Name Ltd", frozen.Client.LegalEntityName);
        }

        [Fact]
        public void TheClientReviewPolicyStillTreatsARenameAsAContractModification()
        {
            // Untouched, and deliberately not replaced by anything above: this governs the OTHER
            // direction - a client editing their own details on the review page.
            var snapshot = new ContractSnapshot();
            snapshot.Client.LegalEntityName = "Chick Tastic LLC";
            snapshot.Client.PrincipalAddress = "1569 Flatbush Ave.";
            snapshot.Client.City = "Brooklyn";
            snapshot.Client.State = "NY";
            snapshot.Client.Zip = "11210";

            var rename = new ClientReviewInfoDto
            {
                CompanyLegalName = "Chick Tastic Holdings LLC",
                CompanyAddress = "1569 Flatbush Ave.",
                City = "Brooklyn", State = "NY", Zip = "11210"
            };

            Assert.True(ContractClientEditPolicy.IsContractModification(snapshot, rename));
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  14-19. Delete is a deactivation
        // ══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>A linked client carrying one contract and one paid invoice.</summary>
        private static async Task<ContractClient> SeedClientWithHistoryAsync(ApplicationDbContext context)
        {
            var user = await SeedBusinessAccountAsync(context);
            await NewService(context).ApplyBusinessFlagAsync(user, true, AdminId);
            var client = await context.ContractClients.SingleAsync();

            context.Contracts.Add(new Contract
            {
                Id = 80, ContractNumber = "DCC-2026-55556666",
                ContractClientId = client.Id, ContractorProfileId = 1,
                ContractTemplateId = 1, ContractServiceLocationId = 1,
                Status = ContractStatus.FullySigned
            });

            var invoice = new CommercialInvoice
            {
                Id = 800,
                InvoiceNumber = "DCI-2026-77778888",
                PublicToken = "1111222233334444555566667777888899990000",
                ContractClientId = client.Id,
                Status = InvoiceStatus.Paid,
                InvoiceDate = new DateTime(2026, 9, 1),
                DueDate = new DateTime(2026, 9, 16),
                SubTotal = 500m, Total = 500m, AmountPaid = 500m, BalanceDue = 0m,
                Currency = "USD",
                CreatedByUserId = AdminId
            };
            context.CommercialInvoices.Add(invoice);
            context.CommercialInvoicePayments.Add(new CommercialInvoicePayment
            {
                CommercialInvoiceId = invoice.Id,
                Amount = 500m,
                PaymentDate = new DateTime(2026, 9, 10),
                PaymentMethod = InvoicePaymentRecordMethod.AchBankTransfer,
                RecordedByUserId = AdminId
            });

            await context.SaveChangesAsync();
            return client;
        }

        [Fact]
        public async Task DeleteIsASoftDelete_AndKeepsEveryContractInvoiceAndPayment()
        {
            using var context = NewContext();
            var client = await SeedClientWithHistoryAsync(context);

            await NewDirectory(context).DeactivateClient(client.Id);

            // The client row itself is still there, just out of circulation.
            var stored = await context.ContractClients.SingleAsync();
            Assert.False(stored.IsActive);

            // And so is everything that refers to it, reference numbers included.
            Assert.Equal("DCC-2026-55556666", (await context.Contracts.SingleAsync()).ContractNumber);
            Assert.Equal("DCI-2026-77778888", (await context.CommercialInvoices.SingleAsync()).InvoiceNumber);
            Assert.Equal(500m, (await context.CommercialInvoicePayments.SingleAsync()).Amount);
        }

        [Fact]
        public async Task ADeletedClientDisappearsFromTheInvoiceAndContractPickers()
        {
            using var context = NewContext();
            var client = await SeedClientWithHistoryAsync(context);
            await NewDirectory(context).DeactivateClient(client.Id);

            // The invoice form's roster: active only, and it does not pass includeInactive.
            Assert.Empty(Body(await NewInvoicesController(context).Clients()));

            // The contract form's client picker, same rule from its own endpoint.
            Assert.Empty(Body(await NewDirectory(context).GetClients(null)));

            // ...but the Clients screen can still show it, which is the only caller that asks.
            var withInactive = Body(await NewInvoicesController(context).Clients(includeInactive: true));
            Assert.False(Assert.Single(withInactive).IsActive);
        }

        [Fact]
        public async Task AHistoricalInvoiceStillResolvesADeletedClient()
        {
            using var context = NewContext();
            var client = await SeedClientWithHistoryAsync(context);
            await NewDirectory(context).DeactivateClient(client.Id);

            var invoice = await context.CommercialInvoices
                .Include(i => i.Client)
                .SingleAsync();

            Assert.NotNull(invoice.Client);
            Assert.Equal(client.Id, invoice.Client!.Id);
            Assert.Equal("Casey Client", invoice.Client.LegalEntityName);
        }

        [Fact]
        public async Task AHistoricalContractStillResolvesADeletedClient()
        {
            using var context = NewContext();
            var client = await SeedClientWithHistoryAsync(context);
            await NewDirectory(context).DeactivateClient(client.Id);

            var contract = await context.Contracts
                .Include(c => c.ContractClient)
                .SingleAsync();

            Assert.NotNull(contract.ContractClient);
            Assert.Equal(client.Id, contract.ContractClient!.Id);
        }

        [Fact]
        public async Task DeletingALinkedClientRemovesTheDesignationButNeverTheUser()
        {
            using var context = NewContext();
            var client = await SeedClientWithHistoryAsync(context);

            await NewDirectory(context).DeactivateClient(client.Id);

            var user = await context.Users.SingleAsync(u => u.Id == BusinessUserId);

            Assert.False(user.IsBusiness);
            // The account itself is untouched - login, orders, history all intact.
            Assert.True(user.IsActive);
            Assert.Equal("casey55@chicktastic.invalid", user.Email);
        }

        [Fact]
        public async Task ADeletedLinkedClientIsNotResurrectedByTheBackfill()
        {
            // The reason Delete clears the business flag: without it the account is still flagged,
            // and the next sync or restart puts the client straight back - the admin deletes it and
            // watches it reappear.
            using var context = NewContext();
            var client = await SeedClientWithHistoryAsync(context);
            await NewDirectory(context).DeactivateClient(client.Id);

            Assert.Equal(0, await NewService(context).BackfillAsync());

            Assert.False((await context.ContractClients.SingleAsync()).IsActive);
            Assert.Single(context.ContractClients);
        }

        [Fact]
        public async Task DeletingAStandaloneClientLeavesEveryAccountAlone()
        {
            using var context = NewContext();
            await SeedBusinessAccountAsync(context);
            await NewDirectory(context).CreateClient(StandaloneDto());
            var client = await context.ContractClients.SingleAsync(c => c.SourceUserId == null);

            await NewDirectory(context).DeactivateClient(client.Id);

            Assert.False((await context.ContractClients.SingleAsync(c => c.Id == client.Id)).IsActive);
            Assert.True((await context.Users.SingleAsync(u => u.Id == BusinessUserId)).IsBusiness);
        }

        [Fact]
        public async Task AStandaloneClientIsRestoredFromTheClientsScreen()
        {
            using var context = NewContext();
            await NewDirectory(context).CreateClient(StandaloneDto());
            var client = await context.ContractClients.SingleAsync();
            await NewDirectory(context).DeactivateClient(client.Id);

            await NewDirectory(context).RestoreClient(client.Id);

            Assert.True((await context.ContractClients.SingleAsync()).IsActive);
        }

        [Fact]
        public async Task ALinkedClientIsRestoredThroughTheBusinessFlag_NotTheRestoreButton()
        {
            // Two routes to the same state is how the two end up disagreeing, so the direct restore
            // is refused and the flag is the single way back.
            using var context = NewContext();
            var client = await SeedClientWithHistoryAsync(context);
            var originalId = client.Id;
            await NewDirectory(context).DeactivateClient(client.Id);

            var refusal = await NewDirectory(context).RestoreClient(client.Id);
            Assert.IsType<BadRequestObjectResult>(refusal);

            var user = await context.Users.SingleAsync(u => u.Id == BusinessUserId);
            await NewService(context).ApplyBusinessFlagAsync(user, true, AdminId);

            var restored = await context.ContractClients.SingleAsync();
            Assert.Equal(originalId, restored.Id);
            Assert.True(restored.IsActive);
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  20-22. No guessing, standalone creation, and nothing else disturbed
        // ══════════════════════════════════════════════════════════════════════════════════════

        [Fact]
        public async Task NoLinkIsEverInferredFromAMatchingEmail()
        {
            using var context = NewContext();
            var user = await SeedBusinessAccountAsync(context);

            // Created by hand with the account's own address typed in as the billing email.
            var dto = StandaloneDto("Look Alike Ltd");
            dto.NoticeEmail = user.Email;

            var created = Body(await NewDirectory(context).CreateClient(dto));

            Assert.Null(created.SourceUserId);
            Assert.Equal(user.Email, created.NoticeEmail);
        }

        [Fact]
        public async Task AnEditCannotRePointTheLinkToAnotherAccount()
        {
            using var context = NewContext();
            await SeedBusinessAccountAsync(context);
            context.Users.Add(Customer(SecondBusinessUserId, "Other", "Owner",
                "other@example.invalid", isBusiness: true));
            await context.SaveChangesAsync();

            await NewDirectory(context).CreateClient(StandaloneDto());
            var client = await context.ContractClients.SingleAsync();

            // A perfectly valid business account, named in an edit payload. The server does not
            // read the field there: the link is granted by the business flag, because it opens
            // that customer's view of this company's contracts.
            await NewDirectory(context).UpdateClient(client.Id, new UpdateCommercialClientDto
            {
                LegalEntityName = "No Account Ltd",
                EntityType = "a limited liability company",
                PrincipalAddress = "1 Standalone Way",
                City = "Brooklyn", State = "NY", Zip = "11226",
                SourceUserId = SecondBusinessUserId
            });

            Assert.Null((await context.ContractClients.SingleAsync()).SourceUserId);
        }

        [Fact]
        public async Task StandaloneCreationStillWorksAndNeedsNoAccount()
        {
            // Kept deliberately: some commercial customers will never have a website account, and
            // staff must not be made to create one just to raise an invoice.
            using var context = NewContext();

            var created = Body(await NewDirectory(context).CreateClient(StandaloneDto()));

            Assert.True(created.Id > 0);
            Assert.Null(created.SourceUserId);
            Assert.Empty(context.Users);
            Assert.Empty(context.Contracts);
        }

        [Fact]
        public async Task EveryTransitionLeavesAnAuditRow_AndNoneOfThemCarryPaymentData()
        {
            using var context = NewContext();
            var user = await SeedBusinessAccountAsync(context);
            var service = NewService(context);

            await service.ApplyBusinessFlagAsync(user, true, AdminId);    // auto-created
            await service.ApplyBusinessFlagAsync(user, false, AdminId);   // deactivated
            await service.ApplyBusinessFlagAsync(user, true, AdminId);    // reactivated

            var client = await context.ContractClients.SingleAsync();
            await NewDirectory(context).DeactivateClient(client.Id);      // designation removed

            var rows = await context.AuditLogs
                .Where(a => a.EntityType == AuditEntityTypes.CommercialClient)
                .OrderBy(a => a.Id)
                .ToListAsync();

            Assert.Equal(
                new[]
                {
                    BusinessClientService.ActionAutoCreated,
                    BusinessClientService.ActionDeactivated,
                    BusinessClientService.ActionReactivated,
                    BusinessClientService.ActionBusinessLinkRemoved
                },
                rows.Select(r => r.Action).ToArray());

            // Who and what, never anything sensitive.
            foreach (var row in rows)
            {
                var payload = row.NewValues ?? string.Empty;
                Assert.DoesNotContain("Stripe", payload, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("routing", payload, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("account_number", payload, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("password", payload, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
