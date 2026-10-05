using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs.Commercial;
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

/// <summary>
/// Linking cleanings to an invoice under a WEEKLY FLAT FEE contract (2026-10): one fee per
/// distinct contract service week, never one per cleaning. The Onyx shape — six visits a week,
/// $875.00 pre-tax, $77.66 tax, $952.66 a week.
/// </summary>
public class WeeklyFlatFeeInvoiceLinkTests
{
    private const int WeeklyContract = 40;
    private const int PerVisitContract = 41;
    private const int OtherClientContract = 42;

    private sealed class Fixture : IDisposable
    {
        public readonly ApplicationDbContext Db = RecurringDiscountRegressionTests.Db();
        public readonly InvoiceService Invoices;
        public readonly InvoiceOrderLinkService Links;

        /// <summary>Sunday Oct 4 – Friday Oct 9, then Sunday Oct 11 – Friday Oct 16, 2026.</summary>
        public readonly DateTime[] Dates = Enumerable.Range(0, 6).Select(i => new DateTime(2026, 10, 4).AddDays(i))
            .Concat(Enumerable.Range(0, 6).Select(i => new DateTime(2026, 10, 11).AddDays(i))).ToArray();

        public Fixture(string weekDefinition = "Sunday through Saturday")
        {
            Db.ContractClients.Add(new ContractClient { Id = 1, LegalEntityName = "Onyx LLC", IsActive = true });
            Db.ContractClients.Add(new ContractClient { Id = 2, LegalEntityName = "Someone Else Inc", IsActive = true });
            Db.ContractServiceLocations.Add(new ContractServiceLocation { Id = 1, ContractClientId = 1,
                Address = "1 Commerce St", City = "New York", State = "NY", Zip = "10001" });
            Db.ContractServiceLocations.Add(new ContractServiceLocation { Id = 2, ContractClientId = 2,
                Address = "2 Other St", City = "New York", State = "NY", Zip = "10002" });
            AddContract(WeeklyContract, 1, ContractPricingBasis.WeeklyFlatFee, weekDefinition);
            AddContract(PerVisitContract, 1, ContractPricingBasis.PerVisit, weekDefinition);
            AddContract(OtherClientContract, 2, ContractPricingBasis.WeeklyFlatFee, weekDefinition);
            Db.Users.Add(new User { Id = 1, FirstName = "Test", LastName = "Admin" });
            Db.ServiceTypes.Add(new ServiceType { Id = 1, Name = "Commercial cleaning" });

            // Each generated visit still carries the template's copied price — $952.66 — which is
            // exactly the figure that must NOT be summed six times.
            for (var i = 0; i < Dates.Length; i++)
                Db.Orders.Add(new Order { Id = i + 1, UserId = 1, ContractClientId = 1, ContractId = WeeklyContract,
                    ServiceTypeId = 1, ServiceDate = Dates[i], RecurrenceOccurrenceDate = Dates[i], Total = 952.66m,
                    Status = OrderStatuses.Pending, PaymentMethod = PaymentMethod.Invoice });
            Db.SaveChanges();

            Invoices = new InvoiceService(Db, new InvoiceNumberService(Db, NullLogger<InvoiceNumberService>.Instance),
                new BillingSettingsService(Db), new ConfigurationBuilder().Build(), NullLogger<InvoiceService>.Instance);
            Links = new InvoiceOrderLinkService(Db, Invoices, RecurringDiscountRegressionTests.Stub<IOrderInvoiceAllocationService>(),
                NullLogger<InvoiceOrderLinkService>.Instance);
        }

        private void AddContract(int id, int clientId, ContractPricingBasis basis, string weekDefinition) =>
            Db.Contracts.Add(new Contract
            {
                Id = id, ContractNumber = $"DCC-2026-000000{id}", ContractClientId = clientId,
                ContractServiceLocationId = clientId, ContractorProfileId = 1, ContractTemplateId = 1,
                Status = ContractStatus.FullySigned,
                DraftSnapshotJson = RecurringServiceDaysAndCountTests.Snapshot(basis, weekDefinition).ToJson()
            });

        public SaveInvoiceDto Draft(IEnumerable<int> orderIds, InvoiceTaxType tax = InvoiceTaxType.Added, int? contractId = WeeklyContract) => new()
        {
            ContractClientId = 1, ContractId = contractId, TaxType = tax, TaxRate = 8.875m,
            OrderIds = orderIds.ToList(),
            Items = new() { new() { Description = "placeholder", Quantity = 1, UnitPrice = 1 } }
        };

        public void Dispose() => Db.Dispose();
    }

    private static IEnumerable<int> Week1 => Enumerable.Range(1, 6);
    private static IEnumerable<int> BothWeeks => Enumerable.Range(1, 12);

    [Theory]
    [InlineData(InvoiceTaxType.Added)]
    [InlineData(InvoiceTaxType.Included)]
    public async Task SixCleaningsFromOneServiceWeek_AreOneWeeklyFee(InvoiceTaxType tax)
    {
        using var f = new Fixture();
        var invoice = await f.Invoices.CreateAsync(f.Draft(Week1, tax), 1);

        var item = Assert.Single(invoice.Items);
        Assert.Equal(1m, item.Quantity);
        Assert.Contains("Weekly commercial cleaning service fee - 6 scheduled visits per week", item.Description);
        Assert.Equal(875.00m, invoice.Total - invoice.TaxAmount);
        Assert.Equal(77.66m, invoice.TaxAmount);
        Assert.Equal(952.66m, invoice.Total);

        // The internal allocation adds up to the invoice — it is not six weekly fees.
        var links = await f.Db.CommercialInvoiceOrders.Where(l => l.CommercialInvoiceId == invoice.Id).ToListAsync();
        Assert.Equal(6, links.Count);
        Assert.Equal(952.66m, links.Sum(l => l.AllocatedAmount));
        Assert.All(await f.Db.Orders.ToListAsync(), o => Assert.Equal(952.66m, o.Total)); // draft touches no order
    }

    [Fact]
    public async Task TwelveCleaningsOverTwoServiceWeeks_AreTwoWeeklyFees()
    {
        using var f = new Fixture();
        var preview = await f.Invoices.PreviewLinkedOrdersAsync(f.Draft(BothWeeks), null);

        Assert.True(preview.PricedAsWeeklyFlatFee);
        Assert.Equal(2, preview.ServiceWeekCount);
        Assert.Equal(2, preview.Items.Count);
        Assert.All(preview.Items, i => Assert.Equal(875.00m, i.UnitPrice));
        Assert.Equal(1905.31m, preview.InvoiceTotal); // 1,750.00 + 155.31 tax
        Assert.Equal(preview.InvoiceTotal, preview.Allocations.Sum(a => a.AllocatedAmount));
        Assert.DoesNotContain(preview.Warnings, w => w.StartsWith("Only"));
    }

    [Fact]
    public async Task APartialWeek_WarnsButIsNotProrated()
    {
        using var f = new Fixture();
        var preview = await f.Invoices.PreviewLinkedOrdersAsync(f.Draft(new[] { 2, 3, 4 }), null);

        Assert.Equal(875.00m, Assert.Single(preview.Items).UnitPrice);
        Assert.Equal(952.66m, preview.InvoiceTotal);
        Assert.Contains(preview.Warnings, w => w.Contains("Only 3 of 6 scheduled visits for the service week of Oct 4, 2026")
            && w.Contains("flat weekly fee of $875.00"));

        // Not blocked: the draft can still be created.
        var invoice = await f.Invoices.CreateAsync(f.Draft(new[] { 2, 3, 4 }), 1);
        Assert.Equal(952.66m, invoice.Total);
    }

    [Fact]
    public async Task TheWeekBoundaryIsTheContracts_NotARollingSevenDays()
    {
        // "Monday through Sunday": Sunday Oct 4 closes the PREVIOUS service week, so Sunday–Friday
        // spans two contract weeks. Read from the contract, never inferred from the selection.
        using var f = new Fixture("Monday through Sunday");
        var preview = await f.Invoices.PreviewLinkedOrdersAsync(f.Draft(Week1), null);

        Assert.Equal(2, preview.ServiceWeekCount);
        Assert.Equal(DayOfWeek.Monday, ServiceWeekCalculator.ParseWeekStart("Monday through Sunday"));
        Assert.Equal(DayOfWeek.Sunday, ServiceWeekCalculator.ParseWeekStart("Sunday through Saturday"));
        Assert.Equal(DayOfWeek.Monday, ServiceWeekCalculator.ParseWeekStart("calendar week"));
    }

    [Fact]
    public async Task AMovedVisit_StillSettlesTheWeekItWasScheduledIn()
    {
        using var f = new Fixture();
        var friday = await f.Db.Orders.FindAsync(6);
        friday!.ServiceDate = new DateTime(2026, 10, 12); // moved into the next week by an admin
        await f.Db.SaveChangesAsync();

        var preview = await f.Invoices.PreviewLinkedOrdersAsync(f.Draft(Week1), null);
        Assert.Equal(1, preview.ServiceWeekCount);
    }

    [Fact]
    public async Task PerVisitContracts_KeepBillingEachCleaning()
    {
        using var f = new Fixture();
        foreach (var order in await f.Db.Orders.Where(o => o.Id <= 3).ToListAsync())
        {
            order.ContractId = PerVisitContract;
            order.Total = 100m;
        }
        await f.Db.SaveChangesAsync();

        var preview = await f.Invoices.PreviewLinkedOrdersAsync(
            f.Draft(new[] { 1, 2, 3 }, InvoiceTaxType.Included, PerVisitContract), null);

        Assert.False(preview.PricedAsWeeklyFlatFee);
        Assert.Equal(3, preview.Items.Count);
        Assert.Equal(300m, preview.InvoiceTotal);
        Assert.Empty(preview.Warnings);
    }

    [Theory]
    [InlineData("other client")]
    [InlineData("other contract")]
    [InlineData("no contract on invoice")]
    public async Task CleaningsOfAnotherCustomerOrContract_CannotBeLinked(string reason)
    {
        using var f = new Fixture();
        var order = (await f.Db.Orders.FindAsync(1))!;
        var dto = f.Draft(Week1);

        if (reason == "other client") { order.ContractClientId = 2; order.ContractId = OtherClientContract; }
        if (reason == "other contract") order.ContractId = PerVisitContract;
        if (reason == "no contract on invoice") dto.ContractId = null;
        await f.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvoiceWorkflowException>(() => f.Invoices.CreateAsync(dto, 1));
        Assert.Empty(await f.Db.CommercialInvoices.ToListAsync());
    }

    [Fact]
    public async Task ThePickerListsAnotherContractsCleaningWithItsReason()
    {
        using var f = new Fixture();
        var order = (await f.Db.Orders.FindAsync(1))!;
        order.ContractId = PerVisitContract;
        await f.Db.SaveChangesAsync();

        var eligible = await f.Links.GetEligibleOrdersAsync(1, null, null, null, WeeklyContract);

        var row = eligible.Orders.Single(o => o.OrderId == 1);
        Assert.False(row.CanSelect);
        Assert.Contains("not the one on this invoice", row.BlockedReason);
        Assert.Equal(new DateTime(2026, 10, 4), row.OccurrenceDate);
        Assert.All(eligible.Orders.Where(o => o.OrderId != 1), o => Assert.True(o.CanSelect));
        Assert.Equal("DCC-2026-00000040", eligible.Orders.Single(o => o.OrderId == 2).ContractNumber);
    }

    [Fact]
    public async Task ExistingIssuedInvoices_AreNeverRePriced()
    {
        using var f = new Fixture();
        var sent = new CommercialInvoice
        {
            ContractClientId = 1, ContractId = WeeklyContract, Status = InvoiceStatus.Sent, InvoiceNumber = "DCI-2026-11112222",
            TaxType = InvoiceTaxType.Added, TaxRate = 8.875m, SubTotal = 5250m, TaxAmount = 465.94m, Total = 5715.94m,
            BalanceDue = 5715.94m, Items = new List<CommercialInvoiceItem>
            {
                new() { Description = "Commercial cleaning (legacy six lines)", Quantity = 6, UnitPrice = 875m, Amount = 5250m }
            }
        };
        f.Db.CommercialInvoices.Add(sent);
        await f.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvoiceWorkflowException>(() => f.Invoices.UpdateAsync(sent.Id, f.Draft(Week1), 1));
        await Assert.ThrowsAsync<InvoiceWorkflowException>(() => f.Invoices.PreviewLinkedOrdersAsync(f.Draft(Week1), sent.Id));

        f.Db.ChangeTracker.Clear();
        var reloaded = await f.Db.CommercialInvoices.Include(i => i.Items).SingleAsync(i => i.Id == sent.Id);
        Assert.Equal(5715.94m, reloaded.Total);
        Assert.Equal(6m, Assert.Single(reloaded.Items).Quantity);
        Assert.Empty(await f.Db.CommercialInvoiceOrders.ToListAsync());
    }

    [Fact]
    public async Task ZeroPricedOperationalVisits_StillInvoiceTheFullWeeklyFee()
    {
        // Generated weekly-flat-fee visits are $0 operational records (2026-10). Their own total is
        // irrelevant: the invoice prices the WEEK from the contract.
        using var f = new Fixture();
        foreach (var order in await f.Db.Orders.ToListAsync()) { order.Total = 0m; order.SubTotal = 0m; order.Tax = 0m; }
        await f.Db.SaveChangesAsync();

        var invoice = await f.Invoices.CreateAsync(f.Draft(Week1), 1);

        Assert.Single(invoice.Items);
        Assert.Equal(875.00m, invoice.Total - invoice.TaxAmount);
        Assert.Equal(952.66m, invoice.Total);
        var links = await f.Db.CommercialInvoiceOrders.Where(l => l.CommercialInvoiceId == invoice.Id).ToListAsync();
        Assert.Equal(952.66m, links.Sum(l => l.AllocatedAmount));
        Assert.All(links, l => Assert.Equal(0m, l.OriginalOrderTotal)); // voiding hands back $0, not a price
    }
}
