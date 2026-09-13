using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs.Commercial;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Helpers.Commercial;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Models.Commercial;
using DreamCleaningBackend.Models.Contracts;
using DreamCleaningBackend.Services;
using DreamCleaningBackend.Services.Commercial;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DreamCleaningBackend.Tests;

public class LinkedInvoiceDraftTests
{
    private sealed class Fixture : IDisposable
    {
        public ApplicationDbContext Db = RecurringDiscountRegressionTests.Db();
        public InvoiceService Invoices;
        public InvoiceOrderLinkService Links;
        public readonly DateTime[] Dates = { new(2026, 9, 13), new(2026, 9, 27), new(2026, 10, 4) };
        public Fixture(params decimal[] amounts)
        {
            Db.ContractClients.Add(new ContractClient { Id = 1, LegalEntityName = "Test company" });
            Db.Users.Add(new User { Id = 1, FirstName = "Test", LastName = "Admin" });
            Db.ServiceTypes.Add(new ServiceType { Id = 1, Name = "Commercial cleaning" });
            for (var i = 0; i < 3; i++) Db.Orders.Add(new Order { Id = i + 1, UserId = 1, ContractClientId = 1,
                ServiceTypeId = 1, ServiceDate = Dates[i], Total = amounts.Length == 0 ? 925.43m : amounts[i],
                Status = OrderStatuses.Pending, PaymentMethod = PaymentMethod.Invoice });
            Db.SaveChanges();
            Invoices = new InvoiceService(Db, new InvoiceNumberService(Db, NullLogger<InvoiceNumberService>.Instance),
                new BillingSettingsService(Db), new ConfigurationBuilder().Build(), NullLogger<InvoiceService>.Instance);
            Links = new InvoiceOrderLinkService(Db, Invoices, RecurringDiscountRegressionTests.Stub<IOrderInvoiceAllocationService>(),
                NullLogger<InvoiceOrderLinkService>.Instance);
        }
        public SaveInvoiceDto Draft() => new() { ContractClientId = 1, TaxType = InvoiceTaxType.Included, TaxRate = 8.875m,
            OrderIds = new() { 3, 1, 2 }, ServiceStartDate = Dates[0], ServiceEndDate = Dates[0], ServiceDates = new() { Dates[0] },
            Items = new() { new() { Description = "Stale cloned single cleaning", Quantity = 1, UnitPrice = 925.43m } } };
        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public async Task CreateAndUpdateDeriveDatesLinesAndTotalEvenWhenBrowserSendsOneStaleLine()
    {
        using var f = new Fixture(); var dto = f.Draft();
        var invoice = await f.Invoices.CreateAsync(dto, 1);
        Assert.Equal(f.Dates, InvoiceService.ParseServiceDates(invoice.ServiceDatesJson));
        Assert.Equal(f.Dates[0], invoice.ServiceStartDate); Assert.Equal(f.Dates[2], invoice.ServiceEndDate);
        Assert.Equal(2776.29m, invoice.Total); Assert.Equal(invoice.Total, invoice.BalanceDue);
        Assert.Equal(226.31m, invoice.TaxAmount); Assert.Equal(2549.98m, invoice.Total - invoice.TaxAmount);
        var calculated = InvoiceCalculator.Calculate(new InvoiceTotalsInput { TaxType = InvoiceTaxType.Included, TaxRate = 8.875m,
            Lines = invoice.Items.Select(i => new InvoiceLineInput { Quantity = i.Quantity, UnitPrice = i.UnitPrice }).ToList() });
        Assert.Equal(2549.98m, calculated.PreTaxTotal);
        Assert.Equal(calculated.Total, calculated.PreTaxTotal + calculated.TaxAmount);
        Assert.Equal(3, invoice.Items.Count); Assert.All(invoice.Items, i => Assert.Equal(925.43m, i.Amount));
        Assert.DoesNotContain(invoice.Items, i => i.Description.Contains("Stale"));
        var id = invoice.Id;
        f.Db.ChangeTracker.Clear();
        dto.OrderIds = null; // old clients cannot overwrite existing links with their stale items
        invoice = await f.Invoices.UpdateAsync(id, dto, 1);
        Assert.Equal(2776.29m, invoice.Total); Assert.Equal(3, invoice.Items.Count);
        Assert.Equal(3, await f.Db.CommercialInvoiceItems.CountAsync(i => i.CommercialInvoiceId == id));
        dto.OrderIds = new() { 2, 1 };
        invoice = await f.Invoices.UpdateAsync(id, dto, 1);
        Assert.Equal(f.Dates.Take(2), InvoiceService.ParseServiceDates(invoice.ServiceDatesJson));
        Assert.Equal(f.Dates[1], invoice.ServiceEndDate); Assert.Equal(1850.86m, invoice.Total);
        Assert.Equal(2, await f.Db.CommercialInvoiceItems.CountAsync(i => i.CommercialInvoiceId == id));
        Assert.Equal(2, await f.Db.CommercialInvoiceOrders.CountAsync(i => i.CommercialInvoiceId == id));
        Assert.All(await f.Db.Orders.ToListAsync(), o => Assert.Equal(925.43m, o.Total));
    }

    [Fact]
    public async Task PreviewSumsUnequalOrdersAndAgreedTotalUsesExistingEqualAllocationWithoutWrites()
    {
        using var f = new Fixture(900, 950, 1000); var dto = f.Draft();
        var preview = await f.Invoices.PreviewLinkedOrdersAsync(dto, null);
        Assert.Equal(2850m, preview.InvoiceTotal);
        Assert.Equal(new[] { 900m, 950m, 1000m }, preview.Items.Select(i => i.UnitPrice));
        dto.NegotiatedGroupTotal = 2500;
        preview = await f.Invoices.PreviewLinkedOrdersAsync(dto, null);
        Assert.Equal(2500m, preview.InvoiceTotal);
        Assert.Equal(new[] { 833.34m, 833.33m, 833.33m }, preview.Allocations.Select(a => a.AllocatedAmount));
        Assert.Empty(await f.Db.CommercialInvoices.ToListAsync()); Assert.Empty(await f.Db.CommercialInvoiceOrders.ToListAsync());
        var invoice = await f.Invoices.CreateAsync(dto, 1);
        Assert.Equal(2500m, invoice.Total);
        Assert.Equal(new[] { 900m, 950m, 1000m }, await f.Db.Orders.OrderBy(o => o.Id).Select(o => o.Total).ToArrayAsync());
        Assert.All(invoice.CoveredOrders, l => Assert.Null(l.CommittedAt));
    }

    [Fact]
    public async Task SelectionEndpointDerivesDatesAndClearingSelectionRestoresStandaloneSaving()
    {
        using var f = new Fixture(); var dto = f.Draft(); dto.OrderIds = null;
        var invoice = await f.Invoices.CreateAsync(dto, 1);
        await f.Links.SaveSelectionAsync(invoice.Id, new SaveInvoiceOrdersDto { OrderIds = new() { 3, 1, 2 } }, 1);
        Assert.Equal(2776.29m, invoice.Total); Assert.Equal(f.Dates, InvoiceService.ParseServiceDates(invoice.ServiceDatesJson));
        dto.OrderIds = new(); dto.ServiceDates = new() { new(2026, 12, 1) };
        invoice = await f.Invoices.UpdateAsync(invoice.Id, dto, 1);
        Assert.Single(invoice.Items); Assert.Equal(925.43m, invoice.Total);
        Assert.Equal(dto.ServiceDates, InvoiceService.ParseServiceDates(invoice.ServiceDatesJson));
        Assert.Empty(await f.Db.CommercialInvoiceOrders.ToListAsync());
    }

    [Fact]
    public async Task LinkedOrdersReplaceClonedDiscountAndAddedTaxStillReproducesTheSelectedGrossTotal()
    {
        using var f = new Fixture(900, 950, 1000); var dto = f.Draft();
        dto.DiscountType = InvoiceDiscountType.Percentage; dto.DiscountValue = 10;
        dto.TaxType = InvoiceTaxType.Added;
        var invoice = await f.Invoices.CreateAsync(dto, 1);
        Assert.Equal(2850m, invoice.Total); Assert.Equal(0, invoice.DiscountAmount);
        Assert.Equal(invoice.Total, invoice.Items.Sum(i => i.Amount) + invoice.TaxAmount);
        Assert.Equal(new[] { 900m, 950m, 1000m }, invoice.CoveredOrders.OrderBy(i => i.OrderId).Select(i => i.AllocatedAmount));
    }

    [Theory]
    [InlineData("client")]
    [InlineData("contract")]
    [InlineData("missing")]
    [InlineData("paid")]
    [InlineData("cancelled")]
    [InlineData("claimed")]
    public async Task InvalidOrderSelectionIsRejectedOnOrdinaryDraftSave(string reason)
    {
        using var f = new Fixture(); var dto = f.Draft();
        var order = (await f.Db.Orders.FindAsync(1))!;
        if (reason == "client") order.ContractClientId = 99;
        if (reason == "contract") dto.ContractId = 99;
        if (reason == "missing") dto.OrderIds!.Add(99);
        if (reason == "paid") order.IsPaid = true;
        if (reason == "cancelled") order.Status = OrderStatuses.Cancelled;
        if (reason == "claimed") f.Db.CommercialInvoiceOrders.Add(new CommercialInvoiceOrder {
            OrderId = 1, Invoice = new CommercialInvoice { ContractClientId = 1, Status = InvoiceStatus.Sent, InvoiceNumber = "Already issued" } });
        await f.Db.SaveChangesAsync();
        var count = await f.Db.CommercialInvoices.CountAsync();
        await Assert.ThrowsAsync<InvoiceWorkflowException>(() => f.Invoices.CreateAsync(dto, 1));
        Assert.Equal(count, await f.Db.CommercialInvoices.CountAsync());
    }

    [Theory]
    [InlineData(InvoiceStatus.Sent)]
    [InlineData(InvoiceStatus.Paid)]
    [InlineData(InvoiceStatus.Void)]
    public async Task FinalizedLinkedSnapshotsCannotBeRebuilt(InvoiceStatus status)
    {
        using var f = new Fixture(); var invoice = await f.Invoices.CreateAsync(f.Draft(), 1);
        invoice.Status = status; await f.Db.SaveChangesAsync();
        var descriptions = invoice.Items.Select(i => i.Description).ToArray();
        await Assert.ThrowsAsync<InvoiceWorkflowException>(() => f.Invoices.UpdateAsync(invoice.Id, f.Draft(), 1));
        await Assert.ThrowsAsync<InvoiceWorkflowException>(() => f.Invoices.PreviewLinkedOrdersAsync(f.Draft(), invoice.Id));
        await Assert.ThrowsAsync<InvoiceWorkflowException>(() => f.Links.SaveSelectionAsync(invoice.Id, new SaveInvoiceOrdersDto { OrderIds = new() { 1 } }, 1));
        Assert.Equal(status, invoice.Status); Assert.Equal(2776.29m, invoice.Total);
        Assert.Equal(descriptions, invoice.Items.Select(i => i.Description));
    }
}
