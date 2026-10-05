using DreamCleaningBackend.Controllers.Crm;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Models.Commercial;
using DreamCleaningBackend.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DreamCleaningBackend.Tests;

/// <summary>
/// "Total spent" and CRM lifetime value are REALIZED money (2026-10): unpaid future bookings —
/// a weekly-flat-fee plan's operational visits included — are not spend, and a paid commercial
/// invoice counts exactly once, through the cleanings it settled.
/// </summary>
public class RealizedSpendTests
{
    private static readonly DateTime Future = DateTime.Today.AddDays(10);
    private static readonly DateTime Past = DateTime.Today.AddDays(-10);

    /// <summary>Expected realized spend: 100 (card) + 952.66 (paid weekly invoice) + 100 (part-refunded 120).</summary>
    private const decimal Expected = 1152.66m;

    private static List<Order> Orders()
    {
        var id = 1;
        Order O(decimal total, string status, Action<Order>? also = null)
        {
            var o = new Order { Id = id++, UserId = 1, ServiceTypeId = 1, Total = total, Status = status,
                ServiceDate = Past, PaymentMethod = PaymentMethod.Normal, ServiceAddress = "x" };
            also?.Invoke(o);
            return o;
        }

        var list = new List<Order>
        {
            O(100m, OrderStatuses.Done, o => o.IsPaid = true),                                  // paid card cleaning
            O(200m, OrderStatuses.Pending, o => o.ServiceDate = Future),                         // unpaid future booking
            O(50m, OrderStatuses.Cancelled, o => o.IsPaid = true),                               // cancelled
            O(70m, OrderStatuses.Refunded, o => { o.IsPaid = true; o.TotalRefundedAmount = 70m; }),
            O(120m, OrderStatuses.Done, o => { o.IsPaid = true; o.TotalRefundedAmount = 20m; }), // part refund
        };

        // Twelve weekly-flat-fee operational visits at $0, still unpaid (future weeks).
        for (var i = 0; i < 12; i++)
            list.Add(O(0m, OrderStatuses.Pending, o => { o.PaymentMethod = PaymentMethod.Invoice; o.ContractClientId = 1; o.ServiceDate = Future.AddDays(i); }));

        // One past week whose $952.66 invoice was SENT (allocations written) and PAID.
        foreach (var share in new[] { 158.78m, 158.78m, 158.78m, 158.78m, 158.77m, 158.77m })
            list.Add(O(share, OrderStatuses.Done, o => { o.PaymentMethod = PaymentMethod.Invoice; o.ContractClientId = 1; o.InvoicePaidAt = Past; }));

        return list;
    }

    [Fact]
    public void InMemory_CountsOnlyRealizedMoney()
    {
        var realized = Orders().Where(OrderPaymentFilter.IsRealizedSpendInMemory).Sum(OrderPaymentFilter.RealizedAmount);
        Assert.Equal(Expected, realized);
    }

    [Fact]
    public async Task TheQueryForm_AgreesWithTheInMemoryForm()
    {
        using var db = RecurringDiscountRegressionTests.Db();
        db.Orders.AddRange(Orders());
        await db.SaveChangesAsync();

        Assert.Equal(Expected, await db.Orders.WhereRealizedSpend().SumAsync(o => o.Total - o.TotalRefundedAmount));
    }

    [Fact]
    public async Task CrmLifetimeValue_IsRealized_AndThePaidInvoiceIsNotCountedTwice()
    {
        using var db = RecurringDiscountRegressionTests.Db();
        db.Users.Add(new User { Id = 1, FirstName = "Onyx", LastName = "Owner", Email = "o@example.invalid",
            IsActive = true, Role = UserRole.Customer, TotalSpentAmount = 99999m });
        db.ServiceTypes.Add(new ServiceType { Id = 1, Name = "Commercial" });
        db.Orders.AddRange(Orders());
        // The paid invoice itself is in the database too — it must not be ADDED to its cleanings.
        db.CommercialInvoices.Add(new CommercialInvoice { Id = 1, ContractClientId = 1, InvoiceNumber = "DCI-2026-00000001",
            Status = InvoiceStatus.Paid, Total = 952.66m, AmountPaid = 952.66m });
        await db.SaveChangesAsync();

        var audit = new AuditService(db, new HttpContextAccessor(), NullLogger<AuditService>.Instance);
        var result = await new CrmCustomersController(db, audit).GetCustomer(1);

        var detail = Assert.IsType<CrmCustomerDetailDto>(Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result.Result).Value);
        Assert.Equal(Expected, detail.LifetimeValue);
    }

    [Fact]
    public void EveryTotalSpentAndLifetimeValueSum_GoesThroughTheRealizedRule()
    {
        var root = Path.Combine(FindBackendRoot(), "Controllers");
        foreach (var file in new[]
                 {
                     Path.Combine("Admin", "AdminUsersController.cs"),
                     Path.Combine("Crm", "CrmCustomersController.cs"),
                     Path.Combine("Crm", "CrmAutomationController.cs")
                 })
        {
            var code = File.ReadAllText(Path.Combine(root, file));
            Assert.DoesNotContain("g.Sum(o => o.Total)", code);
            Assert.DoesNotContain("userOrders.Sum(o => o.Total)", code);
            Assert.DoesNotContain("nonCancelled.Sum(o => o.Total", code);
            Assert.True(code.Contains("WhereRealizedSpend") || code.Contains("IsRealizedSpendInMemory"), file);
            // Invoice money reaches spend through its cleanings only — never added on top.
            Assert.DoesNotContain("CommercialInvoices", code);
        }
    }

    private static string FindBackendRoot()
    {
        var dir = new DirectoryInfo(SourceTree.TestsProjectDir);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "DreamCleaningBackend", "Controllers")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "DreamCleaningBackend");
    }
}
