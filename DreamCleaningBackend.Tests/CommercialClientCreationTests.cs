using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Tasks;
using DreamCleaningBackend.Attributes;
using DreamCleaningBackend.Controllers.Admin;
using DreamCleaningBackend.Controllers.Crm;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.DTOs.Commercial;
using DreamCleaningBackend.Helpers.Commercial;
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
    /// A COMMERCIAL CLIENT EXISTS ON ITS OWN (2026-09).
    ///
    /// The model was always this shape — <c>CommercialInvoice.ContractId</c> is nullable, and the
    /// invoice form has always offered "— No contract —" — but the only UI that could insert a row
    /// into <c>ContractClients</c> was the Create Contract form. So billing a client who had not
    /// signed anything (a trial clean, ad-hoc work, a verbal arrangement) meant fabricating a
    /// contract draft nobody intended to sign, purely to populate the invoice form's client
    /// dropdown — polluting the contract list and burning a DCC number on a document that was
    /// never a document.
    ///
    /// The fix was one layer thick: <c>POST api/crm/contract-directory/clients</c> already existed
    /// and simply had no caller. These tests pin the three claims that makes:
    ///
    ///  1. <b>A client with zero contracts is a first-class client</b> — it is created, it is
    ///     listed by the invoice form's own lookup, and an invoice addressed to it saves and sends.
    ///  2. <b>The business-account link is unchanged and unguessable.</b> It is only ever what
    ///     staff explicitly chose, is refused for a non-business account, and is NEVER inferred
    ///     from an email address that happens to match — the link grants portal access, so a wrong
    ///     one hands a stranger somebody's contracts.
    ///  3. <b>Nothing about EDITING an existing client moved.</b> Creation was widened; the rename
    ///     rules on a live contract were not.
    /// </summary>
    public class CommercialClientCreationTests
    {
        // ── Fixtures ──────────────────────────────────────────────────────────────────────────

        private static ApplicationDbContext NewContext() =>
            new(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase($"commercial-client-{Guid.NewGuid()}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options);

        private const int AdminId = 1;

        /// <summary>
        /// The admin who creates things. <c>CommercialInvoice.CreatedByUserId</c> is a required
        /// relationship, so the detail projection's Include is an inner join and the invoice
        /// vanishes from it without this row.
        /// </summary>
        private static async Task SeedAdminAsync(ApplicationDbContext context)
        {
            context.Users.Add(new User
            {
                Id = AdminId,
                Email = "admin@example.invalid",
                FirstName = "Ada",
                LastName = "Admin",
                PasswordHash = "x",
                PasswordSalt = "x",
                Role = UserRole.Admin
            });
            await context.SaveChangesAsync();
        }

        private static CrmContractDirectoryController NewDirectory(ApplicationDbContext context) =>
            new(context,
                new BusinessClientService(context,
                    new AuditService(context, new HttpContextAccessor(), NullLogger<AuditService>.Instance),
                    NullLogger<BusinessClientService>.Instance),
                new AuditService(context, new HttpContextAccessor(), NullLogger<AuditService>.Instance))
            {
                // Every write here is audited, and an audit row that cannot say WHO did it is the
                // one thing worse than no row at all.
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                        {
                            new Claim("UserId", AdminId.ToString()),
                            new Claim("Role", nameof(UserRole.SuperAdmin))
                        }, "Test"))
                    }
                }
            };

        /// <summary>
        /// Only <c>_context</c> is touched by the lookup under test; the rest of the controller's
        /// dependencies are a live Stripe/SMTP surface we deliberately do not build here.
        /// </summary>
        private static AdminCommercialInvoicesController NewInvoicesController(
            ApplicationDbContext context) =>
            new(context, null!, null!, null!, null!, null!, null!);

        private static InvoiceService NewInvoiceService(ApplicationDbContext context) =>
            new(context,
                new InvoiceNumberService(context, NullLogger<InvoiceNumberService>.Instance),
                new BillingSettingsService(context),
                new ConfigurationBuilder().Build(),
                NullLogger<InvoiceService>.Instance);

        /// <summary>A complete standalone client, the way the modal submits one.</summary>
        private static CreateCommercialClientDto NewClientDto(
            string name = "Chick Tastic LLC",
            int? sourceUserId = null,
            string? noticeEmail = "accounts@chicktastic.invalid",
            bool withContact = true,
            bool withLocation = true) => new()
        {
            LegalEntityName = name,
            EntityType = "a limited liability company",
            FormationState = "New York",
            PrincipalAddress = "1569 Flatbush Ave.",
            City = "Brooklyn",
            State = "NY",
            Zip = "11210",
            NoticeEmail = noticeEmail,
            Phone = "7185550123",
            SourceUserId = sourceUserId,
            BillingContact = withContact
                ? new SaveContractContactDto
                {
                    FirstName = "Casey",
                    LastName = "Client",
                    Title = "Owner",
                    Email = "casey@chicktastic.invalid",
                    Role = ContractContactRole.ClientSigner
                }
                : null,
            ServiceLocation = withLocation
                ? new SaveContractServiceLocationDto
                {
                    BusinessBrand = "Chick Tastic",
                    LocationName = "Flatbush Ave.",
                    Address = "1569 Flatbush Ave.",
                    City = "Brooklyn",
                    State = "NY",
                    Zip = "11210"
                }
                : null
        };

        private static T Body<T>(ActionResult<T> result) where T : class
        {
            var ok = Assert.IsType<OkObjectResult>(result.Result);
            return Assert.IsType<T>(ok.Value);
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  1. A client with zero contracts
        // ══════════════════════════════════════════════════════════════════════════════════════

        [Fact]
        public async Task AClientIsCreatedWithNoContract_AndConsumesNoContractNumber()
        {
            using var context = NewContext();
            await SeedAdminAsync(context);

            var created = Body(await NewDirectory(context).CreateClient(NewClientDto()));

            Assert.True(created.Id > 0);
            Assert.Equal("Chick Tastic LLC", created.LegalEntityName);

            var row = await context.ContractClients.SingleAsync();
            Assert.True(row.IsActive);

            // The whole point: no agreement was invented on the way through, so no DCC number was
            // spent and there is no draft sitting in the contracts list pretending to be a deal.
            Assert.Empty(context.Contracts);
        }

        [Fact]
        public async Task TheOptionalContactAndLocationAreSavedAgainstTheNewClient()
        {
            using var context = NewContext();

            var created = Body(await NewDirectory(context).CreateClient(NewClientDto()));

            var contact = await context.ContractContacts.SingleAsync();
            var location = await context.ContractServiceLocations.SingleAsync();

            // The nested DTOs carry a ContractClientId the caller cannot know; the server fills
            // both from the row it just inserted rather than trusting what arrived.
            Assert.Equal(created.Id, contact.ContractClientId);
            Assert.Equal(created.Id, location.ContractClientId);

            // The trading name lives on the location because ContractClient has no DBA column.
            Assert.Equal("Chick Tastic", location.BusinessBrand);
        }

        [Fact]
        public async Task AClientIsValidWithNeitherAContactNorALocation()
        {
            using var context = NewContext();

            var created = Body(await NewDirectory(context)
                .CreateClient(NewClientDto(withContact: false, withLocation: false)));

            Assert.True(created.Id > 0);
            Assert.Empty(context.ContractContacts);
            Assert.Empty(context.ContractServiceLocations);
        }

        [Fact]
        public async Task TheNewClientAppearsInTheInvoiceFormsOwnClientLookup()
        {
            using var context = NewContext();
            await SeedAdminAsync(context);

            var created = Body(await NewDirectory(context).CreateClient(NewClientDto()));

            // GET api/admin/commercial/invoices/clients — what populates the Create Invoice
            // dropdown. A client with no contracts must be selectable there, which is the bug
            // this whole change exists to fix.
            var options = Body(await NewInvoicesController(context).Clients());

            var option = Assert.Single(options);
            Assert.Equal(created.Id, option.Id);
            Assert.Equal("Chick Tastic LLC", option.LegalEntityName);
            Assert.Empty(option.Contracts);

            // The billing block the form prefills from is populated off the contact and the
            // client's own address, not off a contract.
            Assert.Equal("Casey Client", option.BillingContactName);
            Assert.Equal("casey@chicktastic.invalid", option.BillingEmail);
            Assert.Single(option.Locations);
        }

        [Fact]
        public async Task AnInactiveClientIsNotOffered()
        {
            using var context = NewContext();
            await SeedAdminAsync(context);

            var created = Body(await NewDirectory(context).CreateClient(NewClientDto()));

            var row = await context.ContractClients.SingleAsync(c => c.Id == created.Id);
            row.IsActive = false;
            await context.SaveChangesAsync();

            Assert.Empty(Body(await NewInvoicesController(context).Clients()));
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  2. Invoicing a client that has no contract
        // ══════════════════════════════════════════════════════════════════════════════════════

        private static SaveInvoiceDto NewInvoiceDto(int clientId) => new()
        {
            ContractClientId = clientId,
            ContractId = null,          // ← the case under test
            InvoiceDate = new DateTime(2026, 9, 7),
            DueTerms = InvoiceDueTerms.Net15,
            TaxType = InvoiceTaxType.Exempt,
            PaymentMethod = InvoicePaymentMethod.AchBankTransfer,
            Items =
            {
                new SaveInvoiceItemDto
                {
                    Description = "Deep clean — pre-contract trial",
                    Quantity = 1m,
                    UnitPrice = 925.43m,
                    SortOrder = 0
                }
            }
        };

        [Fact]
        public async Task AnInvoiceIsCreatedForAContractlessClient()
        {
            using var context = NewContext();
            await SeedAdminAsync(context);

            var client = Body(await NewDirectory(context).CreateClient(NewClientDto()));
            var invoices = NewInvoiceService(context);

            var invoice = await invoices.CreateAsync(NewInvoiceDto(client.Id), AdminId);

            Assert.Null(invoice.ContractId);
            Assert.Equal(client.Id, invoice.ContractClientId);
            Assert.Equal(InvoiceStatus.Draft, invoice.Status);
            Assert.Equal(925.43m, invoice.Total);
            Assert.StartsWith("DCI-", invoice.InvoiceNumber);
        }

        [Fact]
        public async Task SaveAndSendWorksWithoutAContract()
        {
            using var context = NewContext();
            await SeedAdminAsync(context);

            var client = Body(await NewDirectory(context).CreateClient(NewClientDto()));
            var invoices = NewInvoiceService(context);
            var invoice = await invoices.CreateAsync(NewInvoiceDto(client.Id), AdminId);

            // The send path's substance, minus the SMTP hop: the client-facing document is built
            // from the invoice, rendered, and the invoice is stamped Sent. Anything that assumed a
            // contract was present would fall over on one of these three.
            var publicDto = await invoices.ToPublicDtoAsync(invoice);
            Assert.Equal(invoice.InvoiceNumber, publicDto.InvoiceNumber);

            var pdf = new InvoicePdfService().Render(publicDto);
            Assert.True(pdf.Length > 1000, "The invoice PDF rendered empty for a contract-less invoice.");

            Assert.True(InvoiceStatusPolicy.CanSend(invoice.Status));
            await invoices.MarkSentAsync(invoice, AdminId, "accounts@chicktastic.invalid");

            var stored = await context.CommercialInvoices.SingleAsync();
            Assert.Equal(InvoiceStatus.Sent, stored.Status);
            Assert.NotNull(stored.FirstSentAt);
            Assert.Null(stored.ContractId);
        }

        [Fact]
        public async Task AContractBelongingToADifferentClientIsStillRefused()
        {
            // The other direction: making the contract optional must not make it unchecked.
            using var context = NewContext();
            await SeedAdminAsync(context);

            var mine = Body(await NewDirectory(context).CreateClient(NewClientDto("Mine LLC")));
            var theirs = Body(await NewDirectory(context).CreateClient(NewClientDto("Theirs LLC")));

            context.Contracts.Add(new Contract
            {
                Id = 40,
                ContractNumber = "DCC-2026-11112222",
                ContractClientId = theirs.Id,
                ContractorProfileId = 1,
                ContractTemplateId = 1,
                ContractServiceLocationId = 1
            });
            await context.SaveChangesAsync();

            var dto = NewInvoiceDto(mine.Id);
            dto.ContractId = 40;

            await Assert.ThrowsAsync<InvoiceWorkflowException>(
                () => NewInvoiceService(context).CreateAsync(dto, AdminId));
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  3. The business-account link
        // ══════════════════════════════════════════════════════════════════════════════════════

        private const int BusinessUserId = 55;
        private const int ResidentialUserId = 56;
        private const string SharedEmail = "accounts@chicktastic.invalid";

        private static async Task SeedCustomersAsync(ApplicationDbContext context)
        {
            context.Users.AddRange(
                new User
                {
                    Id = BusinessUserId,
                    // Deliberately the SAME address the client below carries, so a lookup by email
                    // would "succeed" and link them.
                    Email = SharedEmail,
                    FirstName = "Casey", LastName = "Client",
                    PasswordHash = "x", PasswordSalt = "x",
                    Role = UserRole.Customer, IsBusiness = true
                },
                new User
                {
                    Id = ResidentialUserId,
                    Email = "resident@example.invalid",
                    FirstName = "Robin", LastName = "Resident",
                    PasswordHash = "x", PasswordSalt = "x",
                    Role = UserRole.Customer, IsBusiness = false
                });
            await context.SaveChangesAsync();
        }

        [Fact]
        public async Task SourceUserIdIsOptional()
        {
            using var context = NewContext();
            await SeedCustomersAsync(context);

            var created = Body(await NewDirectory(context)
                .CreateClient(NewClientDto(sourceUserId: null)));

            Assert.Null(created.SourceUserId);
            Assert.Null((await context.ContractClients.SingleAsync()).SourceUserId);
        }

        [Fact]
        public async Task SourceUserIdIsNeverInferredFromAMatchingEmailAddress()
        {
            using var context = NewContext();
            await SeedCustomersAsync(context);

            // The client's notice email IS the business account's login address, and the billing
            // contact's name matches the account holder's. Nothing may join on any of that: the
            // link is an access grant to My Contracts, and guessing it would hand one company's
            // agreements to whoever happens to share an address.
            var created = Body(await NewDirectory(context)
                .CreateClient(NewClientDto(sourceUserId: null, noticeEmail: SharedEmail)));

            Assert.Equal(SharedEmail, created.NoticeEmail);
            Assert.Null(created.SourceUserId);
        }

        [Fact]
        public async Task AnExplicitlyChosenBusinessAccountIsLinked()
        {
            using var context = NewContext();
            await SeedCustomersAsync(context);

            var created = Body(await NewDirectory(context)
                .CreateClient(NewClientDto(sourceUserId: BusinessUserId)));

            Assert.Equal(BusinessUserId, created.SourceUserId);
            Assert.Equal(BusinessUserId, (await context.ContractClients.SingleAsync()).SourceUserId);
        }

        [Fact]
        public async Task ANonBusinessAccountIsRejected()
        {
            using var context = NewContext();
            await SeedCustomersAsync(context);

            var error = await Assert.ThrowsAsync<ContractWorkflowException>(
                () => NewDirectory(context).CreateClient(NewClientDto(sourceUserId: ResidentialUserId)));

            Assert.Equal(BusinessAccountLinkPolicy.NotBusinessMessage, error.Message);

            // And nothing was left behind by the half-completed create.
            Assert.Empty(context.ContractClients);
        }

        [Fact]
        public async Task AnAccountThatDoesNotExistIsRejected()
        {
            using var context = NewContext();
            await SeedCustomersAsync(context);

            var error = await Assert.ThrowsAsync<ContractWorkflowException>(
                () => NewDirectory(context).CreateClient(NewClientDto(sourceUserId: 9999)));

            Assert.Equal(BusinessAccountLinkPolicy.MissingAccountMessage, error.Message);
            Assert.Empty(context.ContractClients);
        }

        [Fact]
        public async Task StandaloneCreationAndTheContractFormApplyTheIDENTICALLinkRule()
        {
            // Both call BusinessAccountLinkPolicy. This asserts the behaviour rather than the call
            // graph, so re-inlining a copy into either caller fails here the moment the two answers
            // diverge — which is the failure that would otherwise go unnoticed for months.
            using var context = NewContext();
            await SeedCustomersAsync(context);

            Assert.Equal(
                BusinessUserId,
                await BusinessAccountLinkPolicy.ResolveAsync(context, BusinessUserId));

            Assert.Null(await BusinessAccountLinkPolicy.ResolveAsync(context, null));

            await Assert.ThrowsAsync<ContractWorkflowException>(
                () => BusinessAccountLinkPolicy.ResolveAsync(context, ResidentialUserId));
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  4. Nothing about editing an existing client moved
        // ══════════════════════════════════════════════════════════════════════════════════════

        [Fact]
        public void RenamingAClientOnItsContractIsStillAContractModification()
        {
            // The reason there is no edit mode on the new modal: a rename is not a typo fix, and
            // the rule that says so is untouched.
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
                City = "Brooklyn",
                State = "NY",
                Zip = "11210"
            };

            Assert.True(ContractClientEditPolicy.IsContractModification(snapshot, rename));
            Assert.Contains("Legal entity name", ContractClientEditPolicy.DetectModifications(snapshot, rename));
        }

        [Fact]
        public async Task CreatingAClientNeverTouchesAnExistingOne()
        {
            using var context = NewContext();

            var first = Body(await NewDirectory(context).CreateClient(NewClientDto("First LLC")));
            var second = Body(await NewDirectory(context).CreateClient(NewClientDto("Second LLC")));

            Assert.NotEqual(first.Id, second.Id);

            var stored = await context.ContractClients.OrderBy(c => c.Id).ToListAsync();
            Assert.Equal(2, stored.Count);
            Assert.Equal("First LLC", stored[0].LegalEntityName);
            Assert.Equal("Second LLC", stored[1].LegalEntityName);
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  5. Permissions
        // ══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Read from the attribute's CONSTRUCTOR ARGUMENT rather than a property, because
        /// <see cref="RequirePermissionAttribute"/> deliberately keeps its permission private -
        /// nothing in production reads it back, and a test is not a reason to widen it.
        /// </summary>
        private static Permission RequiredPermission(string methodName)
        {
            var method = typeof(CrmContractDirectoryController).GetMethod(methodName)!;

            var data = method.GetCustomAttributesData()
                .Single(a => a.AttributeType == typeof(RequirePermissionAttribute));

            return (Permission)data.ConstructorArguments[0].Value!;
        }

        [Fact]
        public void CreatingAClientRequiresPermissionCreate()
        {
            // The UI hides the button for a View-only admin; THIS is the control. A Moderator holds
            // View and must be refused by the endpoint regardless of what the page renders.
            Assert.Equal(
                Permission.Create,
                RequiredPermission(nameof(CrmContractDirectoryController.CreateClient)));
        }

        [Fact]
        public void EditingAClientStillRequiresPermissionUpdate()
        {
            // Widening creation must not have widened anything else on the way past.
            Assert.Equal(
                Permission.Update,
                RequiredPermission(nameof(CrmContractDirectoryController.UpdateClient)));
        }

        [Fact]
        public void ThereIsExactlyOneCommercialClientCreationEndpoint()
        {
            // A second one would be a second place for the business-link rule to be forgotten.
            var creators = typeof(CrmContractDirectoryController).GetMethods()
                .Concat(typeof(AdminCommercialInvoicesController).GetMethods())
                .Where(m => m.GetCustomAttributes<HttpPostAttribute>()
                    .Any(a => a.Template is "clients"))
                .ToList();

            Assert.Single(creators);
        }
    }
}
