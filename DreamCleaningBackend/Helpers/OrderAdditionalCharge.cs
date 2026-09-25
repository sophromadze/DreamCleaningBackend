using System.Linq.Expressions;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// "HOW MUCH EXTRA DOES THIS ORDER STILL OWE BECAUSE ITS PRICE WENT UP AFTER IT WAS PAID?" —
    /// stated once, because the same five-line subtraction was written out at six call sites and
    /// one of them quoting a different number is how a customer gets billed twice.
    ///
    /// This is NOT <see cref="OrderBalance"/>. That answers what is owed on the order's OWN total
    /// (deposits, part-payments). This answers what is owed ON TOP of a settled order because an
    /// edit raised the price. An order can owe both; surfaces add them, nothing merges them.
    ///
    /// <b>ONLY POSITIVE HISTORY ROWS COUNT AS MONEY COLLECTED — this is the whole point of the
    /// file.</b> An <see cref="OrderUpdateHistory"/> row records every edit, including one that
    /// LOWERS the price, and a lowering edit used to store a negative
    /// <see cref="OrderUpdateHistory.AdditionalAmount"/>. Because
    /// <c>IsPaid = additionalAmount &lt;= 0.01m</c>, that negative row was ALSO marked settled — so
    /// it landed inside "sum of what the customer already paid", where subtracting it ADDED to the
    /// bill. Order #359, September 2026, is the worked example:
    ///
    /// <list type="number">
    /// <item>booked at $2,743.65 (InitialTotal = 2743.65);</item>
    /// <item>edited DOWN to $500.00 — history row AdditionalAmount = <b>−2,243.65</b>, IsPaid = true;</item>
    /// <item>edited back UP to $2,743.65 — history row AdditionalAmount = +2,243.65, unpaid.</item>
    /// </list>
    ///
    /// The order was back at exactly the price the customer had already paid, so the correct
    /// outstanding amount was <b>$0.00</b>. What every surface computed instead was
    /// <c>totalDelta − alreadyPaid</c> = <c>2,243.65 − (−2,243.65)</c> = <b>$4,487.30</b> — twice
    /// the increase, and the figure that reached the customer's payment page and email. Note the
    /// delta is measured against the ORIGINAL booking, not against the previous edit, which is why
    /// step 3 on its own is not what was owed.
    ///
    /// A negative row is a price DECREASE, never money the customer handed over. Excluding it
    /// loses nothing: the decrease is already fully described by that row's own OriginalTotal /
    /// NewTotal, and by the order's Total against its InitialTotal.
    ///
    /// New rows are clamped on the way in (<see cref="Collectable"/>) so the shape cannot recur,
    /// and the reads below exclude negatives so the rows already sitting in production cannot
    /// trigger it either. Both halves are needed — neither covers the other's case.
    ///
    /// <b>EF cannot translate a method call inside a bigger Where</b>, so the in-query call sites
    /// chain <see cref="WasCollected"/> as its own <c>.Where(...)</c> instead — the same
    /// arrangement <see cref="OrderBalance"/> and <see cref="OrderPaymentFilter"/> document. This
    /// file is where the rule is STATED; grep for <c>AdditionalAmount</c> to find the copies.
    /// </summary>
    public static class OrderAdditionalCharge
    {
        /// <summary>Anything below this is not money — it is rounding. An update row at or under it
        /// is a decrease or a no-op, which is why such rows are written already marked paid.</summary>
        public const decimal MinimumCollectableAmount = 0.01m;

        /// <summary>
        /// What may be STORED as an update row's additional amount: never below zero.
        ///
        /// A price decrease still writes its row, and every other field on it — OriginalTotal,
        /// NewTotal, OriginalSubTotal, OriginalTax and the rest — is recorded exactly as it
        /// happened, so the audit trail and the admin Update History panel keep the full record.
        /// Only this one field is floored, because it is the only one read as "money owed".
        /// </summary>
        public static decimal Collectable(decimal additionalAmount) =>
            additionalAmount > 0m ? additionalAmount : 0m;

        /// <summary>
        /// An update row that actually COLLECTED money: settled, and for a positive amount.
        ///
        /// The <c>&gt; 0</c> is not belt-and-braces, it is the fix. Rows written before
        /// <see cref="Collectable"/> existed carry negative amounts with <c>IsPaid = true</c>, and
        /// a bare <c>h.IsPaid</c> sum lets one of those subtract a negative into the bill.
        /// </summary>
        public static readonly Expression<Func<OrderUpdateHistory, bool>> WasCollected =
            h => h.IsPaid && h.AdditionalAmount > 0m;

        /// <summary>
        /// <b>TIPS ARE PART OF THE COMPARISON (2026-09).</b> Every comparison here is made on the
        /// full, tip-INCLUSIVE totals — the same figures the history rows' AdditionalAmount is
        /// computed from (<c>order.Total − originalTotal</c> in both writers) and the same figures
        /// the booking-time snapshot stamps (InitialTotal includes InitialTips).
        ///
        /// It used to subtract tips from both sides, on the theory that tips are only ever
        /// collected with the booking. They are not: an admin adding a tip to a paid order (order
        /// #386 — +$270.00 tips) wrote an unpaid +$270.00 history row, and then every surface that
        /// reads THIS file — the payment page, the payment link, "send updated payment", the admin
        /// panel's reminder row — computed $0.00 owed, because the only thing that moved was the
        /// part being subtracted. The row said "Unpaid" and nothing could collect it. The reverse
        /// was also wrong: a paid row's AdditionalAmount already includes any tip delta, so
        /// subtracting a tip-inclusive "collected" from a tip-free delta under-charged later edits.
        /// </summary>
        public static decimal CurrentTotal(Order order) => order.Total;

        /// <summary>True when the order carries a booking-time snapshot of what was first charged.</summary>
        public static bool HasInitialSnapshot(Order order) =>
            order.InitialTotal != 0m || order.InitialTips != 0m || order.InitialCompanyDevelopmentTips != 0m;

        /// <summary>
        /// What the customer originally paid, tips included.
        ///
        /// <paramref name="firstHistoryOriginalTotal"/> is the FALLBACK for orders whose
        /// Initial* columns are all zero — orders placed before those columns were stamped, which
        /// is a large share of production. For those the earliest update row's OriginalTotal is
        /// the only surviving record of the booking price, so this fallback is load-bearing and
        /// must keep working; null (no history at all) reads as zero, exactly as before.
        /// </summary>
        public static decimal OriginalTotal(Order order, decimal? firstHistoryOriginalTotal) =>
            HasInitialSnapshot(order)
                ? order.InitialTotal
                : (firstHistoryOriginalTotal ?? 0m);

        /// <summary>
        /// The outstanding additional amount: how far the price has risen above the original
        /// booking, less what has already been collected against that rise.
        ///
        /// Both clamps matter. The delta is floored first, so an order currently priced BELOW its
        /// booking owes nothing rather than a negative a later increase could cancel against; the
        /// result is floored again, so over-collection is never reported as money owed backwards.
        /// </summary>
        public static decimal Outstanding(decimal currentTotal, decimal originalTotal, decimal collectedToDate)
        {
            var totalDelta = Math.Max(0m, currentTotal - originalTotal);
            return OrderPricingCalculator.Round2(Math.Max(0m, totalDelta - collectedToDate));
        }

        /// <inheritdoc cref="Outstanding(decimal, decimal, decimal)"/>
        /// <remarks>
        /// <b>No snapshot AND no history means nothing is owed (2026-09).</b> A top-up only exists
        /// because an edit raised the price, and every edit writes an <see cref="OrderUpdateHistory"/>
        /// row. With neither record the "original" price is unknown, and reading it as zero reported
        /// the order's WHOLE total as an unpaid top-up — which the pending-update payment intent
        /// would then have charged a second time on a legacy paid order.
        /// </remarks>
        public static decimal Outstanding(Order order, decimal? firstHistoryOriginalTotal, decimal collectedToDate) =>
            !HasInitialSnapshot(order) && firstHistoryOriginalTotal == null
                ? 0m
                : Outstanding(
                    CurrentTotal(order),
                    OriginalTotal(order, firstHistoryOriginalTotal),
                    collectedToDate);

        /// <summary>In-memory counterpart of <see cref="WasCollected"/>, for callers that already
        /// hold the rows.</summary>
        public static decimal CollectedToDate(IEnumerable<OrderUpdateHistory> histories) =>
            histories.Where(h => h.IsPaid && h.AdditionalAmount > 0m).Sum(h => h.AdditionalAmount);

        /// <inheritdoc cref="Outstanding(decimal, decimal, decimal)"/>
        public static decimal Outstanding(Order order, IEnumerable<OrderUpdateHistory> histories)
        {
            var rows = histories as IReadOnlyCollection<OrderUpdateHistory> ?? histories.ToList();
            var firstOriginalTotal = rows
                .OrderBy(h => h.UpdatedAt)
                .Select(h => (decimal?)h.OriginalTotal)
                .FirstOrDefault();
            return Outstanding(order, firstOriginalTotal, CollectedToDate(rows));
        }

        /// <summary>
        /// The database-backed resolution every endpoint that charges, displays or messages an
        /// additional amount goes through, so the payment intent, the admin panel, the email and
        /// the SMS cannot quote four different numbers.
        /// </summary>
        public static async Task<decimal> OutstandingAsync(
            ApplicationDbContext context, Order order, CancellationToken cancellationToken = default)
        {
            decimal? firstOriginalTotal = null;
            if (!HasInitialSnapshot(order))
            {
                firstOriginalTotal = await context.OrderUpdateHistories
                    .Where(h => h.OrderId == order.Id)
                    .OrderBy(h => h.UpdatedAt)
                    .Select(h => (decimal?)h.OriginalTotal)
                    .FirstOrDefaultAsync(cancellationToken);
            }

            var collectedToDate = await context.OrderUpdateHistories
                .Where(h => h.OrderId == order.Id)
                .Where(WasCollected)
                .SumAsync(h => (decimal?)h.AdditionalAmount, cancellationToken) ?? 0m;

            return Outstanding(order, firstOriginalTotal, collectedToDate);
        }
    }
}
