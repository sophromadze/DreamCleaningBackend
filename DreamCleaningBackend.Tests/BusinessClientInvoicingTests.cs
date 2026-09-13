using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs.Commercial;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Models.Commercial;
using DreamCleaningBackend.Models.Contracts;
using DreamCleaningBackend.Services;
using DreamCleaningBackend.Services.Commercial;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DreamCleaningBackend.Tests;

/// <summary>
/// A BUSINESS CUSTOMER'S ORDINARY BOOKINGS ARE INVOICEABLE (2026-09).
///
/// The defect these pin down: <c>Order.ContractClientId</c> is stamped only when an order is
/// created with (or switched to) the Invoice payment method, so a business customer's normal
/// bookings carried none — and the invoice form's "Cleanings covered" picker, which filtered on
/// exactly that column, reported "this client has no cleanings to bill" for a client with a diary
/// full of them. The only way out was to change every order's payment method by hand first, which
/// is backwards: choosing to bill a cleaning on an invoice is what should make it invoice-billed.
/// </summary>
public class BusinessClientInvoicingTests
{
    private sealed class Fixture : IDisposable
    {
        public readonly ApplicationDbContext Db = RecurringDiscountRegressionTests.Db();
        public readonly InvoiceService Invoices;
        public readonly InvoiceOrderLinkService Links;

        /// <summary>Client 1 is linked to user 1; client 2 is a different company entirely.</summary>
        public Fixture()
        {
            Db.Users.Add(new User { Id = 1, FirstName = "Bea", LastName = "Business", IsBusiness = true });
            Db.Users.Add(new User { Id = 2, FirstName = "Other", LastName = "Person" });
            Db.Users.Add(new User { Id = 9, FirstName = "Test", LastName = "Admin" });
            Db.ContractClients.Add(new ContractClient { Id = 1, LegalEntityName = "Bakery LLC", SourceUserId = 1 });
            Db.ContractClients.Add(new ContractClient { Id = 2, LegalEntityName = "Unrelated Co" });
            Db.ServiceTypes.Add(new ServiceType { Id = 1, Name = "Commercial cleaning" });
            Db.SaveChanges();

            Invoices = new InvoiceService(Db,
                new InvoiceNumberService(Db, NullLogger<InvoiceNumberService>.Instance),
                new BillingSettingsService(Db), new ConfigurationBuilder().Build(),
                NullLogger<InvoiceService>.Instance);

            Links = new InvoiceOrderLinkService(Db, Invoices,
                new OrderInvoiceAllocationService(Db, new RecordingAuditService(),
                    NullLogger<OrderInvoiceAllocationService>.Instance),
                NullLogger<InvoiceOrderLinkService>.Instance);
        }

        public Order AddOrder(int id, int userId, PaymentMethod method = PaymentMethod.Normal,
            int? clientId = null, string? status = null, bool isPaid = false)
        {
            var order = new Order
            {
                Id = id,
                UserId = userId,
                ContractClientId = clientId,
                ServiceTypeId = 1,
                ServiceDate = new DateTime(2026, 10, 4).AddDays(id),
                SubTotal = 850m,
                Tax = 75.43m,
                Total = 925.43m,
                Status = status ?? OrderStatuses.Pending,
                IsPaid = isPaid,
                PaymentMethod = method
            };
            Db.Orders.Add(order);
            Db.SaveChanges();
            return order;
        }

        public Task<CommercialInvoice> NewDraftAsync() => Invoices.CreateAsync(
            new SaveInvoiceDto
            {
                ContractClientId = 1,
                TaxType = InvoiceTaxType.Included,
                TaxRate = 8.875m
            }, 9);

        public void Dispose() => Db.Dispose();
    }

    /// <summary>
    /// The bug, stated directly: a business customer's not-yet-paid ordinary booking carries no
    /// ContractClientId and must still be offered on their company's invoice.
    /// </summary>
    [Fact]
    public async Task ThePickerOffersTheLinkedAccountsUnclaimedCleanings()
    {
        using var f = new Fixture();
        f.AddOrder(1, userId: 1);                   // the ordinary booking — the whole point
        f.AddOrder(2, userId: 1, clientId: 1);      // already billed to this client
        f.AddOrder(3, userId: 1, clientId: 2);      // another company's cleaning
        f.AddOrder(4, userId: 2);                   // somebody else entirely

        var ids = (await f.Links.GetEligibleOrdersAsync(1, null, null, null))
            .Orders.Select(o => o.OrderId).ToList();

        Assert.Contains(1, ids);
        Assert.Contains(2, ids);
        // A cleaning billed to a DIFFERENT company must never reach this client's invoice,
        // however the accounts are linked — that is the narrow half of the widening.
        Assert.DoesNotContain(3, ids);
        Assert.DoesNotContain(4, ids);
    }

    /// <summary>A standalone client has no account to borrow cleanings from, and borrows none.</summary>
    [Fact]
    public async Task AStandaloneClientStillSeesOnlyItsOwnCleanings()
    {
        using var f = new Fixture();
        f.AddOrder(1, userId: 1);
        f.AddOrder(2, userId: 1, clientId: 2);

        var result = await f.Links.GetEligibleOrdersAsync(2, null, null, null);
        Assert.Equal(new[] { 2 }, result.Orders.Select(o => o.OrderId).ToArray());
    }

    /// <summary>
    /// THE PICKER AND THE SAVE GUARD ASK THE SAME QUESTION.
    ///
    /// Before this, the picker blocked on <c>IsPaid || InvoicePaidAt != null</c> while the save
    /// refused anything <c>IsSettledOnRecord</c> — so a cash job was offered as selectable and then
    /// rejected on save, which is the most confusing shape a validation error can take. Both now
    /// read <c>OrderPaymentFilter</c>.
    /// </summary>
    [Theory]
    [InlineData(PaymentMethod.Cash, null, false, false)]
    [InlineData(PaymentMethod.Normal, null, true, false)]
    [InlineData(PaymentMethod.Normal, null, false, true)]
    [InlineData(PaymentMethod.Invoice, null, false, true)]
    [InlineData(PaymentMethod.Normal, "Cancelled", false, false)]
    public async Task WhatThePickerOffersIsWhatTheSaveAccepts(
        PaymentMethod method, string? status, bool isPaid, bool expectedSelectable)
    {
        using var f = new Fixture();
        f.AddOrder(1, userId: 1, method: method, status: status, isPaid: isPaid);

        var row = (await f.Links.GetEligibleOrdersAsync(1, null, null, null)).Orders.Single();
        Assert.Equal(expectedSelectable, row.CanSelect);

        var invoice = await f.NewDraftAsync();
        var save = () => f.Links.SaveSelectionAsync(
            invoice.Id, new SaveInvoiceOrdersDto { OrderIds = new List<int> { 1 } }, 9);

        if (expectedSelectable) await save();
        else await Assert.ThrowsAsync<InvoiceWorkflowException>(save);
    }

    /// <summary>
    /// SENDING IS WHAT ADOPTS THE CLEANING — and voiding hands it back.
    ///
    /// Both halves matter. Adopting makes the order invoice-billed, which is what stops it being
    /// counted as settled money before the client has paid (<c>OrderPaymentFilter</c>) and what
    /// lets the invoice's own settlement activate it. Handing it back means a mistaken invoice
    /// does not leave a cleaning permanently marked as billed to a company.
    /// </summary>
    [Fact]
    public async Task SendingAdoptsTheCleaningAndVoidingGivesItBack()
    {
        using var f = new Fixture();
        var order = f.AddOrder(1, userId: 1);
        Assert.Null(order.ContractClientId);

        var invoice = await f.NewDraftAsync();
        await f.Links.SaveSelectionAsync(
            invoice.Id, new SaveInvoiceOrdersDto { OrderIds = new List<int> { 1 } }, 9);

        // A DRAFT IS A PROPOSAL: selecting has changed nothing on the order.
        Assert.Null(order.ContractClientId);
        Assert.Equal(PaymentMethod.Normal, order.PaymentMethod);

        await f.Links.CommitAllocationsAsync(invoice.Id, 9);

        Assert.Equal(1, order.ContractClientId);
        Assert.Equal(PaymentMethod.Invoice, order.PaymentMethod);
        // Adopted, NOT settled — the client has not paid the invoice yet.
        Assert.False(OrderPaymentFilter.IsSettledInMemory(order));

        var link = await f.Db.CommercialInvoiceOrders.SingleAsync();
        Assert.Equal((int)PaymentMethod.Normal, link.OriginalPaymentMethod);
        Assert.Null(link.OriginalContractClientId);
        Assert.Equal(925.43m, link.OriginalOrderTotal);

        await f.Links.RevertAllocationsAsync(invoice.Id, 9);

        Assert.Equal(PaymentMethod.Normal, order.PaymentMethod);
        Assert.Null(order.ContractClientId);
        Assert.Equal(925.43m, order.Total);
    }

    /// <summary>
    /// A link row written before the snapshot existed has nothing to restore, and must be left
    /// alone rather than guessed back to Normal — a cleaning genuinely booked as an Invoice order
    /// would otherwise be quietly turned into an unpaid card job by voiding its invoice.
    /// </summary>
    [Fact]
    public async Task VoidingLeavesAPreSnapshotRowAlone()
    {
        using var f = new Fixture();
        var order = f.AddOrder(1, userId: 1, method: PaymentMethod.Invoice, clientId: 1);
        var invoice = await f.NewDraftAsync();

        f.Db.CommercialInvoiceOrders.Add(new CommercialInvoiceOrder
        {
            CommercialInvoiceId = invoice.Id,
            OrderId = 1,
            AllocatedAmount = 925.43m,
            OriginalOrderTotal = 925.43m,
            CommittedAt = DateTime.UtcNow,
            OriginalPaymentMethod = null       // the legacy shape
        });
        await f.Db.SaveChangesAsync();

        await f.Links.RevertAllocationsAsync(invoice.Id, 9);

        Assert.Equal(PaymentMethod.Invoice, order.PaymentMethod);
        Assert.Equal(1, order.ContractClientId);
    }

    /// <summary>
    /// The Orders panel's "Send Invoice" question, both halves: the invoices covering a cleaning,
    /// and — when none does — the client a new draft would bill, resolved through the ACCOUNT LINK
    /// so an admin does not have to know the customer is flagged as a business.
    /// </summary>
    [Fact]
    public async Task AnOrderKnowsWhichClientWouldInvoiceItEvenWithNoneOfItsOwn()
    {
        using var f = new Fixture();
        f.AddOrder(1, userId: 1);
        f.AddOrder(2, userId: 2);

        var linked = await f.Links.GetOrderInvoicesAsync(1);
        Assert.Null(linked.ContractClientId);
        Assert.Equal(1, linked.SuggestedContractClientId);
        Assert.Equal("Bakery LLC", linked.SuggestedClientName);
        Assert.True(linked.CanBeInvoiced);
        Assert.Empty(linked.Invoices);

        // No commercial client anywhere in reach: the panel says so rather than offering a draft
        // it could not raise.
        var unlinked = await f.Links.GetOrderInvoicesAsync(2);
        Assert.Null(unlinked.ContractClientId);
        Assert.Null(unlinked.SuggestedContractClientId);
    }

    /// <summary>
    /// The Invoices tab's rule: a customer's invoices come from BOTH directions. An ordinary
    /// customer with no business flag still has one when a cleaning of theirs was billed on a
    /// company's invoice — which is why the tab is driven by what comes back, not by the flag.
    /// </summary>
    [Fact]
    public async Task AUsersInvoicesComeFromTheirClientAndFromTheirCleanings()
    {
        using var f = new Fixture();
        f.AddOrder(1, userId: 2);   // an ordinary customer's cleaning…

        var invoice = await f.NewDraftAsync();

        // …billed on the company's invoice.
        f.Db.CommercialInvoiceOrders.Add(new CommercialInvoiceOrder
        {
            CommercialInvoiceId = invoice.Id, OrderId = 1, AllocatedAmount = 925.43m
        });
        await f.Db.SaveChangesAsync();

        // The business account owns it because the invoice is addressed to its client.
        var business = await f.Links.GetUserInvoicesAsync(1);
        Assert.Single(business);
        Assert.Equal(invoice.InvoiceNumber, business[0].InvoiceNumber);

        // The ordinary customer owns it because one of THEIR cleanings is on it.
        Assert.Single(await f.Links.GetUserInvoicesAsync(2));

        // A customer with neither gets nothing, which is what hides the tab.
        Assert.Empty(await f.Links.GetUserInvoicesAsync(9));
    }
}
