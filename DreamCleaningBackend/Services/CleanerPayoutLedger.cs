using DreamCleaningBackend.Data;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services
{
    /// <summary>
    /// THE ONLY WRITER of cleaner payout records (2026-10) — <see cref="CleanerPayout"/> /
    /// <see cref="CleanerPayoutLine"/> and the paid-state cache they keep on
    /// <see cref="OrderCleaner"/>.
    ///
    /// The Orders tab (one line, or a whole order) and the Unpaid cleaners tab (several cleaners
    /// at once) both record through here, which is what keeps the three tabs consistent: a
    /// payment made on either screen is the same rows, read back by both.
    ///
    /// It decides nothing about AMOUNTS. Callers compute what is owed with
    /// <see cref="CleanerPayrollCalculator"/> and <see cref="CleanerPayoutSettlement"/>, under the
    /// order lock (<see cref="LockOrdersAsync"/>), and hand the result in. It writes nothing to the
    /// database by itself either: changes are tracked on the context and saved by the caller inside
    /// its own transaction, so a payout and the order lines it settles commit together or not at all.
    /// </summary>
    public sealed class CleanerPayoutLedger
    {
        private readonly ApplicationDbContext _context;

        public CleanerPayoutLedger(ApplicationDbContext context)
        {
            _context = context;
        }

        /// <summary>One order line to pay now, as the caller computed it.</summary>
        public sealed record LineToPay(Order Order, OrderCleaner Assignment, decimal Amount, bool IsTopUp);

        /// <summary>What every payout recorded in one action shares.</summary>
        public sealed class PayoutDetails
        {
            public DateTime PaidOn { get; init; }

            /// <summary>Null = each cleaner's own saved method.</summary>
            public CleanerPaymentMethod? PaidVia { get; init; }
            public string? Reference { get; init; }
            public string? Note { get; init; }
            public int? UserId { get; init; }
            public string? UserName { get; init; }

            /// <summary>The confirmation's one-time key; each payout stores it suffixed with its cleaner.</summary>
            public string? IdempotencyKey { get; init; }
        }

        public static string? PayoutKey(string? idempotencyKey, int cleanerId) =>
            string.IsNullOrWhiteSpace(idempotencyKey) ? null : $"{idempotencyKey.Trim()}:{cleanerId}";

        /// <summary>
        /// Takes a row lock on the orders a payout touches, for the rest of the caller's
        /// transaction. Every payout write takes it first and only THEN reads what is owed, so two
        /// admins paying the same lines at the same moment are serialised: the second one waits,
        /// then reads the line as already settled and pays nothing.
        ///
        /// A no-op on a non-relational provider (the in-memory test database has no locks, and no
        /// concurrency to guard against).
        /// </summary>
        public async Task LockOrdersAsync(IEnumerable<int> orderIds)
        {
            if (!_context.Database.IsRelational()) return;

            var ids = orderIds.Distinct().OrderBy(id => id).ToList();
            if (ids.Count == 0) return;

            // Ints only, so the list is safe to inline. Ordered so two lockers of overlapping sets
            // always queue in the same order instead of deadlocking.
            try
            {
                await _context.Database.ExecuteSqlRawAsync(
                    $"SELECT `Id` FROM `Orders` WHERE `Id` IN ({string.Join(",", ids)}) ORDER BY `Id` FOR UPDATE");
            }
            catch (MySqlConnector.MySqlException ex) when (ex.Number is 1020 or 1205 or 1213)
            {
                // 1020: a row changed since this transaction last read it; 1205: lock wait timeout;
                // 1213: deadlock. Another payment for the same orders won — nothing was written here.
                throw new CleanerPayoutConflictException(
                    "Another payment for these orders was being recorded at the same moment. "
                    + "Nothing was paid here — refresh and check before trying again.");
            }
        }

        /// <summary>
        /// Records one payout per cleaner covering the given lines, and brings each order line's
        /// paid-state cache up to date. Returns the new payouts (tracked, not yet saved).
        /// </summary>
        public List<CleanerPayout> Record(IReadOnlyCollection<LineToPay> lines, PayoutDetails details)
        {
            var payouts = new List<CleanerPayout>();
            var now = DateTime.UtcNow;

            foreach (var group in lines.GroupBy(l => l.Assignment.CleanerId))
            {
                var cleaner = group.First().Assignment.Cleaner;
                var payout = new CleanerPayout
                {
                    CleanerId = group.Key,
                    CleanerName = DescribeCleaner(group.First().Assignment),
                    PaidOn = details.PaidOn.Date,
                    // "Each cleaner's own saved method" is resolvable here, because one payout is
                    // always one cleaner.
                    PaidVia = details.PaidVia ?? cleaner?.PaymentMethod,
                    Reference = Trim(details.Reference, 200),
                    Note = Trim(details.Note, 500),
                    CreatedByUserId = details.UserId,
                    CreatedByName = Trim(details.UserName, 200),
                    CreatedAt = now,
                    IdempotencyKey = PayoutKey(details.IdempotencyKey, group.Key)
                };

                foreach (var line in group.OrderBy(l => l.Order.ServiceDate).ThenBy(l => l.Order.Id))
                {
                    var amount = OrderPricingCalculator.Round2(line.Amount);
                    payout.Lines.Add(new CleanerPayoutLine
                    {
                        OrderCleanerId = line.Assignment.Id,
                        OrderId = line.Order.Id,
                        CleanerId = line.Assignment.CleanerId,
                        ServiceDate = line.Order.ServiceDate,
                        Amount = amount,
                        IsTopUp = line.IsTopUp
                    });

                    // The cache, in step with the row just added. Running total, never replaced:
                    // see OrderCleaner.PaidAmount.
                    var assignment = line.Assignment;
                    var paidSoFar = assignment.IsPaid ? assignment.PaidAmount ?? 0m : 0m;
                    assignment.IsPaid = true;
                    assignment.PaidAmount = OrderPricingCalculator.Round2(paidSoFar + amount);
                    assignment.PaidVia = payout.PaidVia ?? assignment.PaidVia;
                    assignment.PaidAt = now;
                    assignment.PaidByUserId = details.UserId;
                    if (!string.IsNullOrWhiteSpace(payout.Note)) assignment.PaymentNote = payout.Note;
                }

                payout.Amount = OrderPricingCalculator.Round2(payout.Lines.Sum(l => l.Amount));
                _context.CleanerPayouts.Add(payout);
                payouts.Add(payout);
            }

            return payouts;
        }

        /// <summary>
        /// Marks lines reverted and rewrites the paid-state cache of every order line they covered
        /// from what is still standing. History is kept: nothing is deleted.
        /// </summary>
        public async Task RevertAsync(IReadOnlyCollection<CleanerPayoutLine> lines, int? userId)
        {
            var now = DateTime.UtcNow;
            foreach (var line in lines.Where(l => l.RevertedAt == null))
            {
                line.RevertedAt = now;
                line.RevertedByUserId = userId;
            }

            var assignmentIds = lines.Where(l => l.OrderCleanerId.HasValue)
                .Select(l => l.OrderCleanerId!.Value).Distinct().ToList();
            if (assignmentIds.Count == 0) return;

            var assignments = await _context.OrderCleaners
                .Where(oc => assignmentIds.Contains(oc.Id))
                .ToListAsync();

            foreach (var assignment in assignments)
                await ResyncAsync(assignment);
        }

        /// <summary>
        /// Rewrites one order line's paid state from its ACTIVE payout lines — tracked ones
        /// included, so a revert made earlier in the same unit of work is already counted.
        /// No active line left means "not paid", cleared exactly as Undo always cleared it.
        /// </summary>
        public async Task ResyncAsync(OrderCleaner assignment)
        {
            var stored = await _context.CleanerPayoutLines
                .Include(l => l.Payout)
                .Where(l => l.OrderCleanerId == assignment.Id)
                .ToListAsync();

            // Lines added in this unit of work and not saved yet are tracked but not queryable.
            var pending = _context.ChangeTracker.Entries<CleanerPayoutLine>()
                .Where(e => e.State == EntityState.Added && e.Entity.OrderCleanerId == assignment.Id)
                .Select(e => e.Entity);

            var active = stored.Concat(pending).Distinct()
                .Where(l => l.RevertedAt == null)
                .ToList();

            if (active.Count == 0)
            {
                assignment.IsPaid = false;
                assignment.PaidAmount = null;
                assignment.PaidVia = null;
                assignment.PaidAt = null;
                assignment.PaidByUserId = null;
                assignment.PaymentNote = null;
                return;
            }

            var latest = active
                .OrderByDescending(l => l.Payout?.CreatedAt ?? DateTime.MinValue)
                .ThenByDescending(l => l.Id)
                .First();

            assignment.IsPaid = true;
            assignment.PaidAmount = OrderPricingCalculator.Round2(active.Sum(l => l.Amount));
            assignment.PaidVia = latest.Payout?.PaidVia;
            assignment.PaidAt = latest.Payout?.CreatedAt;
            assignment.PaidByUserId = latest.Payout?.CreatedByUserId;
            assignment.PaymentNote = latest.Payout?.Note;
        }

        public static string DescribeCleaner(OrderCleaner assignment) =>
            assignment.Cleaner == null
                ? $"Cleaner #{assignment.CleanerId}"
                : $"{assignment.Cleaner.FirstName} {assignment.Cleaner.LastName}".Trim();

        private static string? Trim(string? value, int max)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var trimmed = value.Trim();
            return trimmed.Length <= max ? trimmed : trimmed[..max];
        }
    }

    /// <summary>
    /// The lines asked for are no longer what the admin was shown — somebody paid them, or the
    /// hours changed, between opening the confirmation and pressing it. Nothing was recorded.
    /// </summary>
    public class CleanerPayoutConflictException : Exception
    {
        public CleanerPayoutConflictException(string message) : base(message) { }
    }
}
