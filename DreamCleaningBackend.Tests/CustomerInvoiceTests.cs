using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using DreamCleaningBackend.Services.Interfaces;
using DreamCleaningBackend.Services.Commercial;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DreamCleaningBackend.Tests;

/// <summary>
/// Regular customer invoices — Admin → Invoices (2026-09).
///
/// A regular invoice is a bill wrapped around ONE order's own payment: every invoice is backed by
/// a part-payment request, the card path settles that request, and the invoice's status is read
/// from the request and the order. These pin down the three things that make that safe:
///  - status is derived and can never claim money the order does not show (or the reverse),
///  - an order's open invoices can never ask for more than it owes, and a FULL invoice cannot
///    sit beside SPLIT ones,
///  - the public page's DTO carries nothing internal.
/// Email and SMS are not exercised — creating and voiding never send anything.
/// </summary>
public class CustomerInvoiceTests
{
    private static ApplicationDbContext Db() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString(), b => b.EnableNullChecks(false))
        .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);

    private static Order NewOrder(decimal total = 2743.65m, decimal amountPaid = 0m, bool isPaid = false,
        string status = OrderStatuses.Pending, PaymentMethod method = PaymentMethod.Normal, int? recurringSeriesId = null) => new()
    {
        Id = 1,
        UserId = 1,
        ServiceTypeId = 1,
        ServiceDate = DateTime.UtcNow.Date.AddDays(7),
        ServiceTime = TimeSpan.FromHours(10),
        Status = status,
        PaymentMethod = method,
        IsPaid = isPaid,
        Total = total,
        AmountPaid = amountPaid,
        SubTotal = 2519.93m,
        Tax = 223.72m,
        ServiceAddress = "1579 Flatbush Ave.",
        ContactEmail = "test@example.invalid",
        ContactFirstName = "Test",
        ContactLastName = "Client",
        ContactPhone = "2125550100",
        RecurringSeriesId = recurringSeriesId
    };

    private static async Task<(ApplicationDbContext Db, CustomerInvoiceService Invoices, OrderPartialPaymentService Partial)>
        ServiceWith(Order order)
    {
        var db = Db();
        db.Users.Add(new User { Id = 1, FirstName = "Test", LastName = "Client" });
        db.Users.Add(new User { Id = 5, FirstName = "Ana", LastName = "Admin" });
        // Seeded because the invoice queries Include the order's service type, which the
        // in-memory provider treats as a required join.
        db.ServiceTypes.Add(new ServiceType { Id = 1, Name = "Regular Cleaning", Description = "" });
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        var audit = new RecordingAuditService();
        var partial = new OrderPartialPaymentService(db, audit, NullLogger<OrderPartialPaymentService>.Instance);
        var invoices = new CustomerInvoiceService(db, partial, null!, null!, audit,
            new BillingSettingsService(db), new ConfigurationBuilder().Build(),
            NullLogger<CustomerInvoiceService>.Instance);
        return (db, invoices, partial);
    }

    // ── Status is derived ─────────────────────────────────────────────────────────────────

    private static (CustomerInvoice Invoice, OrderPartialPayment Request) Pair(
        CustomerInvoiceKind kind = CustomerInvoiceKind.Full, decimal requested = 2743.65m,
        OrderPartialPaymentStatus requestStatus = OrderPartialPaymentStatus.Pending) =>
        (new CustomerInvoice { Kind = kind, Amount = requested },
         new OrderPartialPayment { RequestedAmount = requested, Status = requestStatus });

    [Fact]
    public void AnUnsentInvoiceIsNotSent_AndOnceSentReadsSent()
    {
        var (invoice, request) = Pair();
        Assert.Equal(CustomerInvoiceStatusPolicy.NotSent, CustomerInvoiceStatusPolicy.Resolve(invoice, NewOrder(), request));

        invoice.FirstSentAt = DateTime.UtcNow;
        Assert.Equal(CustomerInvoiceStatusPolicy.Sent, CustomerInvoiceStatusPolicy.Resolve(invoice, NewOrder(), request));
    }

    [Fact]
    public void ItsOwnRequestPaid_IsPaid()
    {
        var (invoice, request) = Pair(requestStatus: OrderPartialPaymentStatus.Paid);
        Assert.Equal(CustomerInvoiceStatusPolicy.Paid, CustomerInvoiceStatusPolicy.Resolve(invoice, NewOrder(), request));
    }

    [Fact]
    public void AnOrderSettledAnotherWay_MakesEveryInvoiceOnItPaid()
    {
        // Paid in full from the profile, or switched to cash after the invoice went out: an
        // invoice for an order that owes nothing must never still read as owed.
        var (invoice, request) = Pair(CustomerInvoiceKind.Split, 1000m);
        Assert.Equal(CustomerInvoiceStatusPolicy.Paid,
            CustomerInvoiceStatusPolicy.Resolve(invoice, NewOrder(isPaid: true), request));
        Assert.Equal(CustomerInvoiceStatusPolicy.Paid,
            CustomerInvoiceStatusPolicy.Resolve(invoice, NewOrder(method: PaymentMethod.Cash), request));
    }

    [Fact]
    public void VoidedOrWithdrawnReadsVoid_AndACancelledOrderReadsCancelled()
    {
        var (invoice, request) = Pair();
        invoice.VoidedAt = DateTime.UtcNow;
        Assert.Equal(CustomerInvoiceStatusPolicy.Void, CustomerInvoiceStatusPolicy.Resolve(invoice, NewOrder(), request));

        var (withdrawn, cancelledRequest) = Pair(requestStatus: OrderPartialPaymentStatus.Cancelled);
        Assert.Equal(CustomerInvoiceStatusPolicy.Void, CustomerInvoiceStatusPolicy.Resolve(withdrawn, NewOrder(), cancelledRequest));

        var (open, openRequest) = Pair();
        Assert.Equal(CustomerInvoiceStatusPolicy.Cancelled,
            CustomerInvoiceStatusPolicy.Resolve(open, NewOrder(status: OrderStatuses.Cancelled), openRequest));
    }

    [Fact]
    public void AFullInvoiceBillsTheLiveBalance_ASplitInvoiceBillsItsSlice()
    {
        var (full, fullRequest) = Pair();
        var partPaid = NewOrder(amountPaid: 1000m);
        var status = CustomerInvoiceStatusPolicy.Resolve(full, partPaid, fullRequest);
        Assert.Equal(1743.65m, CustomerInvoiceStatusPolicy.AmountDue(full, partPaid, fullRequest, status));

        var (split, splitRequest) = Pair(CustomerInvoiceKind.Split, 1000m);
        status = CustomerInvoiceStatusPolicy.Resolve(split, NewOrder(), splitRequest);
        Assert.Equal(1000m, CustomerInvoiceStatusPolicy.AmountDue(split, NewOrder(), splitRequest, status));

        // Nothing is owed on an invoice that is no longer payable.
        Assert.Equal(0m, CustomerInvoiceStatusPolicy.AmountDue(split, NewOrder(isPaid: true), splitRequest,
            CustomerInvoiceStatusPolicy.Paid));
    }

    // ── Creating ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AFullInvoice_IsBackedByOneRequestForTheWholeBalance()
    {
        var (db, invoices, _) = await ServiceWith(NewOrder());

        var created = await invoices.CreateAsync(new CreateCustomerInvoiceDto { OrderId = 1 }, adminUserId: 5);

        var invoice = Assert.Single(created);
        Assert.Equal(CustomerInvoiceKind.Full, invoice.Kind);
        Assert.Matches(@"^DCR-\d{4}-\d{8}$", invoice.InvoiceNumber);
        Assert.Equal(48, invoice.PublicToken.Length);

        var request = await db.OrderPartialPayments.SingleAsync();
        Assert.Equal(2743.65m, request.RequestedAmount);
        Assert.Equal(OrderPartialPaymentStatus.Pending, request.Status);
        Assert.Equal(request.Id, invoice.OrderPartialPaymentId);

        // The pay link needs the order's own secret payment token.
        Assert.False(string.IsNullOrEmpty((await db.Orders.SingleAsync()).PaymentAccessToken));
    }

    [Fact]
    public async Task ASecondFullInvoice_IsRefusedWhileTheFirstIsOpen()
    {
        var (_, invoices, _) = await ServiceWith(NewOrder());
        await invoices.CreateAsync(new CreateCustomerInvoiceDto { OrderId = 1 }, 5);

        await Assert.ThrowsAsync<CustomerInvoiceException>(() =>
            invoices.CreateAsync(new CreateCustomerInvoiceDto { OrderId = 1 }, 5));
    }

    [Fact]
    public async Task ASplitCreatesOneInvoicePerAmount_EachWithItsOwnOpenRequest()
    {
        var (db, invoices, _) = await ServiceWith(NewOrder());

        var created = await invoices.CreateAsync(new CreateCustomerInvoiceDto
        {
            OrderId = 1,
            SplitAmounts = new List<decimal> { 1000m, 1743.65m }
        }, 5);

        Assert.Equal(2, created.Count);
        Assert.All(created, i => Assert.Equal(CustomerInvoiceKind.Split, i.Kind));
        var requests = await db.OrderPartialPayments.OrderBy(p => p.Id).ToListAsync();
        Assert.Equal(new[] { 1000m, 1743.65m }, requests.Select(r => r.RequestedAmount));
        Assert.All(requests, r => Assert.Equal(OrderPartialPaymentStatus.Pending, r.Status));
        Assert.Equal(2, created.Select(i => i.InvoiceNumber).Distinct().Count());
    }

    [Fact]
    public async Task SplitInvoicesCanNeverAskForMoreThanTheOrderOwes()
    {
        var (db, invoices, _) = await ServiceWith(NewOrder());

        await Assert.ThrowsAsync<CustomerInvoiceException>(() => invoices.CreateAsync(new CreateCustomerInvoiceDto
        {
            OrderId = 1,
            SplitAmounts = new List<decimal> { 2000m, 1000m }
        }, 5));

        // A FRESH order for the second half. The refused batch above is rolled back by its
        // transaction on MariaDB, but the in-memory provider ignores transactions, so reusing the
        // order here would leave the batch's first split behind and test the provider instead of
        // the rule. Each split is checked against what is not already on another invoice.
        var (_, fresh, _) = await ServiceWith(NewOrder());
        await fresh.CreateAsync(new CreateCustomerInvoiceDto { OrderId = 1, SplitAmounts = new List<decimal> { 2000m } }, 5);
        await Assert.ThrowsAsync<CustomerInvoiceException>(() => fresh.CreateAsync(new CreateCustomerInvoiceDto
        {
            OrderId = 1,
            SplitAmounts = new List<decimal> { 800m }
        }, 5));
    }

    [Fact]
    public async Task AFullInvoiceAndSplitInvoicesNeverSitSideBySide()
    {
        var (_, invoices, _) = await ServiceWith(NewOrder());
        await invoices.CreateAsync(new CreateCustomerInvoiceDto { OrderId = 1 }, 5);

        await Assert.ThrowsAsync<CustomerInvoiceException>(() => invoices.CreateAsync(new CreateCustomerInvoiceDto
        {
            OrderId = 1,
            SplitAmounts = new List<decimal> { 500m }
        }, 5));
    }

    [Fact]
    public async Task TheOneLiveRequestRuleStillHoldsForOrdinaryPaymentRequests()
    {
        // Relaxed ONLY for split invoices. The order panel's own "ask for a deposit" keeps it.
        var (_, invoices, partial) = await ServiceWith(NewOrder());
        await invoices.CreateAsync(new CreateCustomerInvoiceDto { OrderId = 1, SplitAmounts = new List<decimal> { 1000m } }, 5);

        await Assert.ThrowsAsync<PartialPaymentException>(() => partial.CreateRequestAsync(1, 500m, null, 5));
    }

    [Fact]
    public async Task ARecurringOccurrenceCannotBeInvoiced()
    {
        var (_, invoices, _) = await ServiceWith(NewOrder(recurringSeriesId: 7));

        await Assert.ThrowsAsync<CustomerInvoiceException>(() =>
            invoices.CreateAsync(new CreateCustomerInvoiceDto { OrderId = 1 }, 5));
    }

    [Fact]
    public async Task AnOrderSettledOutsideTheWebsiteCannotBeInvoiced()
    {
        var (_, invoices, _) = await ServiceWith(NewOrder(method: PaymentMethod.Zelle, status: OrderStatuses.Active));

        await Assert.ThrowsAsync<CustomerInvoiceException>(() =>
            invoices.CreateAsync(new CreateCustomerInvoiceDto { OrderId = 1 }, 5));
    }

    // ── Additional charges (added after the order was paid) ───────────────────────────────

    private static async Task<(ApplicationDbContext Db, CustomerInvoiceService Invoices)> PaidOrderWithTopUp()
    {
        var order = NewOrder(total: 241.54m, isPaid: true, status: OrderStatuses.Active);
        order.InitialTotal = 141.54m;
        var (db, invoices, _) = await ServiceWith(order);
        db.OrderUpdateHistories.Add(new OrderUpdateHistory
        {
            Id = 1, OrderId = 1, UpdatedByUserId = 5, UpdatedAt = DateTime.UtcNow,
            OriginalTotal = 141.54m, NewTotal = 241.54m, AdditionalAmount = 100m, IsPaid = false
        });
        await db.SaveChangesAsync();
        return (db, invoices);
    }

    [Fact]
    public async Task APaidOrderThatWasEditedUp_IsInvoicedForTheExtraOnly()
    {
        var (db, invoices) = await PaidOrderWithTopUp();

        var invoice = Assert.Single(await invoices.CreateAsync(new CreateCustomerInvoiceDto { OrderId = 1 }, 5));

        Assert.Equal(CustomerInvoiceKind.Additional, invoice.Kind);
        Assert.Equal(100m, invoice.Amount);
        Assert.Null(invoice.OrderPartialPaymentId);
        Assert.Empty(await db.OrderPartialPayments.ToListAsync());

        var page = await invoices.GetPublicAsync(invoice.PublicToken);
        Assert.Equal(100m, page!.AmountDue);
        Assert.Equal(141.54m, page.OrderAmountPaid);
        Assert.Equal($"/order/1/pay?t={(await db.Orders.SingleAsync()).PaymentAccessToken}", page.CardPaymentPath);

        // One open additional invoice at a time, and it can't be split.
        await Assert.ThrowsAsync<CustomerInvoiceException>(() =>
            invoices.CreateAsync(new CreateCustomerInvoiceDto { OrderId = 1 }, 5));
    }

    [Fact]
    public async Task AnAdditionalInvoice_IsPaidOnceTheTopUpIsCollected_AndStaysPaid()
    {
        var (db, invoices) = await PaidOrderWithTopUp();
        var invoice = Assert.Single(await invoices.CreateAsync(new CreateCustomerInvoiceDto { OrderId = 1 }, 5));

        var row = await db.OrderUpdateHistories.SingleAsync();
        row.IsPaid = true;
        row.PaidAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        Assert.Equal(CustomerInvoiceStatusPolicy.Paid, (await invoices.GetAsync(invoice.Id))!.Status);

        // A later edit adds more: the paid invoice must not flip back to owed.
        var order = await db.Orders.SingleAsync();
        order.Total = 291.54m;
        db.OrderUpdateHistories.Add(new OrderUpdateHistory
        {
            Id = 2, OrderId = 1, UpdatedByUserId = 5, UpdatedAt = DateTime.UtcNow,
            OriginalTotal = 241.54m, NewTotal = 291.54m, AdditionalAmount = 50m, IsPaid = false
        });
        await db.SaveChangesAsync();
        Assert.Equal(CustomerInvoiceStatusPolicy.Paid, (await invoices.GetAsync(invoice.Id))!.Status);

        var next = Assert.Single(await invoices.CreateAsync(new CreateCustomerInvoiceDto { OrderId = 1 }, 5));
        Assert.Equal(50m, next.Amount);
    }

    [Fact]
    public async Task TheInvoicePdfRenders()
    {
        var (_, invoices, _) = await ServiceWith(NewOrder());
        var invoice = Assert.Single(await invoices.CreateAsync(new CreateCustomerInvoiceDto { OrderId = 1 }, 5));

        var pdf = await invoices.RenderPdfAsync(invoice.PublicToken);

        Assert.NotNull(pdf);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf!.Value.Bytes, 0, 4));
        Assert.Equal($"Dream-Cleaning-Invoice-{invoice.InvoiceNumber}.pdf", pdf.Value.FileName);
    }

    // ── Voiding ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task VoidingWithdrawsTheRequest_SoTheCardPageStopsTakingMoneyForIt()
    {
        var (db, invoices, _) = await ServiceWith(NewOrder());
        var invoice = Assert.Single(await invoices.CreateAsync(new CreateCustomerInvoiceDto { OrderId = 1 }, 5));

        await invoices.VoidAsync(invoice.Id, "Wrong amount", 5);

        Assert.Equal(OrderPartialPaymentStatus.Cancelled, (await db.OrderPartialPayments.SingleAsync()).Status);
        var dto = await invoices.GetAsync(invoice.Id);
        Assert.Equal(CustomerInvoiceStatusPolicy.Void, dto!.Status);
        Assert.False(dto.CanSend);

        // ...and frees the order to be invoiced again.
        Assert.Single(await invoices.CreateAsync(new CreateCustomerInvoiceDto { OrderId = 1 }, 5));
    }

    [Fact]
    public async Task APaidInvoiceCannotBeVoided()
    {
        var (db, invoices, _) = await ServiceWith(NewOrder());
        var invoice = Assert.Single(await invoices.CreateAsync(new CreateCustomerInvoiceDto { OrderId = 1 }, 5));
        var request = await db.OrderPartialPayments.SingleAsync();
        request.Status = OrderPartialPaymentStatus.Paid;
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<CustomerInvoiceException>(() => invoices.VoidAsync(invoice.Id, null, 5));
    }

    // ── The public page ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ThePublicPageResolvesByToken_AndPointsTheCardButtonAtThisInvoicesRequest()
    {
        var (db, invoices, _) = await ServiceWith(NewOrder());
        var invoice = Assert.Single(await invoices.CreateAsync(new CreateCustomerInvoiceDto { OrderId = 1 }, 5));
        var order = await db.Orders.SingleAsync();

        var page = await invoices.GetPublicAsync(invoice.PublicToken);

        Assert.NotNull(page);
        Assert.Equal(invoice.InvoiceNumber, page!.InvoiceNumber);
        Assert.Equal(2743.65m, page.AmountDue);
        Assert.Equal($"/order/1/pay?t={order.PaymentAccessToken}&request={invoice.OrderPartialPaymentId}&full=1", page.CardPaymentPath);

        Assert.Null(await invoices.GetPublicAsync("not-a-real-token"));
    }

    [Fact]
    public void ThePublicDtoCarriesNothingInternal()
    {
        var names = typeof(PublicCustomerInvoiceDto).GetProperties().Select(p => p.Name).ToList();
        foreach (var forbidden in new[] { "Id", "PublicToken", "VoidReason", "CreatedByName", "PartialPaymentId", "UserId", "SendCount" })
            Assert.DoesNotContain(forbidden, names);
    }
}
