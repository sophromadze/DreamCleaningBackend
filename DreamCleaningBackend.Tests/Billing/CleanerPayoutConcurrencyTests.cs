using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DreamCleaningBackend.Tests.Billing;

/// <summary>
/// The double-payment guard on cleaner payouts, against a REAL MariaDB (2026-10). The in-memory
/// provider has no locks, so the property that matters — two admins paying the same line at the
/// same moment record it once — can only be shown here. Runs when <c>DC_TEST_MARIADB</c> names a
/// local server; skipped otherwise, like the other integration tests in this folder.
/// </summary>
public class CleanerPayoutConcurrencyTests : IClassFixture<MariaDbDatabase>
{
    private readonly MariaDbDatabase _db;
    public CleanerPayoutConcurrencyTests(MariaDbDatabase db) => _db = db;

    private async Task<(int OrderId, int AssignmentId, int CleanerId, int AdminId)> SeedFinishedJobAsync()
    {
        var harness = new BillingHarness(_db);
        var admin = await harness.AddUserAsync();
        var order = await harness.AddOrderAsync(admin.Id, 326.63m, status: OrderStatuses.Done,
            serviceDate: DateTime.UtcNow.Date.AddDays(-3));

        await using var db = _db.CreateContext();
        var stored = await db.Orders.SingleAsync(o => o.Id == order.Id);
        stored.TotalDuration = 300;
        stored.MaidsCount = 1;
        stored.CleanerHourlyRate = 21m;

        var cleaner = new Cleaner { FirstName = "Ana", LastName = "Reyes", Email = $"ana.{Guid.NewGuid():N}@example.com", IsActive = true };
        db.Cleaners.Add(cleaner);
        await db.SaveChangesAsync();

        var assignment = new OrderCleaner { OrderId = order.Id, CleanerId = cleaner.Id, AssignedBy = admin.Id };
        db.OrderCleaners.Add(assignment);
        await db.SaveChangesAsync();

        return (order.Id, assignment.Id, cleaner.Id, admin.Id);
    }

    private OutgoingPaymentService NewService(out Data.ApplicationDbContext context)
    {
        context = _db.CreateContext();
        var audit = new ConcurrentAudit();
        return new OutgoingPaymentService(context, audit, new CleanerPayrollEditService(context, audit));
    }

    [MariaDbFact]
    public async Task TwoSimultaneousOrdersTabPaymentsOfOneLineRecordItOnce()
    {
        var job = await SeedFinishedJobAsync();

        var first = NewService(out var c1);
        var second = NewService(out var c2);
        await using (c1)
        await using (c2)
        {
            await Task.WhenAll(
                first.MarkCleanerPaidAsync(job.OrderId, job.AssignmentId, new MarkCleanerPaidDto(), job.AdminId),
                second.MarkCleanerPaidAsync(job.OrderId, job.AssignmentId, new MarkCleanerPaidDto(), job.AdminId));
        }

        await using var db = _db.CreateContext();
        var lines = await db.CleanerPayoutLines.Where(l => l.OrderCleanerId == job.AssignmentId).ToListAsync();
        Assert.Single(lines);
        Assert.Equal(105.00m, lines[0].Amount);
        Assert.Equal(105.00m, (await db.OrderCleaners.SingleAsync(oc => oc.Id == job.AssignmentId)).PaidAmount);
    }

    [MariaDbFact]
    public async Task TwoConfirmationsForTheSameLinesPayOnce_TheOtherIsTurnedAway()
    {
        var job = await SeedFinishedJobAsync();

        PayCleanersDto Confirm(string key) => new()
        {
            IdempotencyKey = key,
            Cleaners = new()
            {
                new PayCleanerSelectionDto
                {
                    CleanerId = job.CleanerId,
                    Lines = new() { new ExpectedPayoutLineDto { OrderCleanerId = job.AssignmentId, Amount = 105.00m } }
                }
            }
        };

        var first = NewService(out var c1);
        var second = NewService(out var c2);
        var outcomes = new List<Exception?>();
        await using (c1)
        await using (c2)
        {
            var tasks = new[]
            {
                Capture(() => first.PayCleanersAsync(Confirm("tab-one-" + Guid.NewGuid().ToString("N")[..8]), job.AdminId)),
                Capture(() => second.PayCleanersAsync(Confirm("tab-two-" + Guid.NewGuid().ToString("N")[..8]), job.AdminId))
            };
            outcomes.AddRange(await Task.WhenAll(tasks));
        }

        Assert.Single(outcomes, o => o == null);
        Assert.Single(outcomes, o => o is CleanerPayoutConflictException);

        await using var db = _db.CreateContext();
        Assert.Single(await db.CleanerPayouts.Where(p => p.CleanerId == job.CleanerId).ToListAsync());
    }

    private static async Task<Exception?> Capture(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception ex) { return ex; }
    }
}
