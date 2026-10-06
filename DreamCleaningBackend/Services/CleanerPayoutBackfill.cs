using DreamCleaningBackend.Data;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services
{
    /// <summary>
    /// Turns the paid state that existed BEFORE payout records into payout history (2026-10), so
    /// the Paid cleaners tab starts complete and nothing paid is lost.
    ///
    /// Runs at startup, after migrations, from Program.cs — not inside the migration, because the
    /// one figure SQL cannot produce is what a line was worth: a line paid before amounts were
    /// recorded (the August 2026 bulk mark, note "Paid before payout tracking existed") is imported
    /// at the payroll calculator's figure, and the calculator is C#. Owner's call (2026-10-06):
    /// those lines count as SETTLED at that figure, not as a full top-up still owed.
    ///
    /// Safety properties, since it runs unattended:
    ///  - <b>Idempotent.</b> It only touches order lines flagged paid with no active payout line
    ///    behind them; once imported they have one, so the next start finds nothing.
    ///  - <b>Additive.</b> It inserts payouts and fills a missing <see cref="OrderCleaner.PaidAmount"/>.
    ///    It never changes an amount that was recorded, never un-pays anything, and never touches
    ///    an order's own figures.
    ///  - <b>Grouped like real payments.</b> One imported payout per cleaner, per admin who marked it,
    ///    per day it was marked (or, with no date on record, per service date) — the closest the
    ///    old data comes to "one transfer". Top-ups recorded on the same line cannot be told apart
    ///    any more, so a line imports as one amount: what it says it was paid.
    /// </summary>
    public static class CleanerPayoutBackfill
    {
        public const string LegacyNote = "Paid before payout tracking existed";
        public const string ImportedNote = "Imported from payments recorded before payout history existed";

        public static async Task<int> RunAsync(ApplicationDbContext context, CancellationToken ct = default)
        {
            var pendingOrderIds = await context.OrderCleaners
                .Where(oc => oc.IsPaid
                             && !context.CleanerPayoutLines.Any(l => l.OrderCleanerId == oc.Id && l.RevertedAt == null))
                .Select(oc => oc.OrderId)
                .Distinct()
                .ToListAsync(ct);

            if (pendingOrderIds.Count == 0) return 0;

            // What the calculator needs: the services (is TotalDuration already per cleaner?) and
            // every assignment with its overrides.
            var orders = await context.Orders
                .Include(o => o.OrderServices).ThenInclude(os => os.Service)
                .Include(o => o.OrderCleaners).ThenInclude(oc => oc.Cleaner)
                .Where(o => pendingOrderIds.Contains(o.Id))
                .AsSplitQuery()
                .ToListAsync(ct);

            var coveredIds = (await context.CleanerPayoutLines
                    .Where(l => l.RevertedAt == null && l.OrderCleanerId != null
                                && pendingOrderIds.Contains(l.OrderId))
                    .Select(l => l.OrderCleanerId!.Value)
                    .ToListAsync(ct))
                .ToHashSet();

            var userIds = orders.SelectMany(o => o.OrderCleaners)
                .Where(oc => oc.PaidByUserId.HasValue)
                .Select(oc => oc.PaidByUserId!.Value)
                .Distinct()
                .ToList();
            var userNames = await context.Users
                .Where(u => userIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => (u.FirstName + " " + u.LastName).Trim(), ct);

            var items = new List<(Order Order, OrderCleaner Assignment, decimal Amount, bool Legacy)>();

            foreach (var order in orders)
            {
                var payroll = CleanerPayrollCalculator.Build(
                    order, CleanerPayrollCalculator.HasCleanerHoursService(order), order.OrderCleaners);

                foreach (var assignment in order.OrderCleaners.Where(oc => oc.IsPaid && !coveredIds.Contains(oc.Id)))
                {
                    var legacy = assignment.PaidAmount == null;
                    var amount = legacy
                        ? payroll.Lines.FirstOrDefault(l => l.OrderCleanerId == assignment.Id)?.Payout ?? 0m
                        : assignment.PaidAmount!.Value;

                    amount = OrderPricingCalculator.Round2(amount);

                    // The settled figure becomes the line's recorded amount, so the settlement rule
                    // reads it as paid in full rather than as nothing paid.
                    if (legacy) assignment.PaidAmount = amount;

                    items.Add((order, assignment, amount, legacy));
                }
            }

            var payouts = items
                .GroupBy(i => (
                    i.Assignment.CleanerId,
                    i.Assignment.PaidByUserId,
                    Day: (i.Assignment.PaidAt ?? i.Order.ServiceDate).Date,
                    i.Legacy))
                .Select(g =>
                {
                    var first = g.First().Assignment;
                    var notes = g.Select(i => i.Assignment.PaymentNote).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
                    var payout = new CleanerPayout
                    {
                        CleanerId = g.Key.CleanerId,
                        CleanerName = CleanerPayoutLedger.DescribeCleaner(first),
                        PaidOn = g.Key.Day,
                        PaidVia = g.Select(i => i.Assignment.PaidVia).FirstOrDefault(v => v.HasValue),
                        Note = notes.Count == 1 ? Truncate(notes[0]!, 500) : g.Key.Legacy ? LegacyNote : ImportedNote,
                        CreatedByUserId = g.Key.PaidByUserId,
                        CreatedByName = g.Key.PaidByUserId.HasValue && userNames.TryGetValue(g.Key.PaidByUserId.Value, out var name)
                            ? name
                            : null,
                        CreatedAt = g.Max(i => i.Assignment.PaidAt) ?? DateTime.UtcNow,
                        IsImported = true
                    };

                    foreach (var item in g.OrderBy(i => i.Order.ServiceDate).ThenBy(i => i.Order.Id))
                    {
                        payout.Lines.Add(new CleanerPayoutLine
                        {
                            OrderCleanerId = item.Assignment.Id,
                            OrderId = item.Order.Id,
                            CleanerId = item.Assignment.CleanerId,
                            ServiceDate = item.Order.ServiceDate,
                            Amount = item.Amount,
                            IsTopUp = false
                        });
                    }

                    payout.Amount = OrderPricingCalculator.Round2(payout.Lines.Sum(l => l.Amount));
                    return payout;
                })
                .ToList();

            context.CleanerPayouts.AddRange(payouts);
            await context.SaveChangesAsync(ct);
            return payouts.Count;
        }

        private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
    }
}
