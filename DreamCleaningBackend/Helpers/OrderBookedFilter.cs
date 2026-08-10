using DreamCleaningBackend.Models;
using System.Linq.Expressions;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// SINGLE SOURCE OF TRUTH for "this row in Orders is a real booking" — the set the CRM/marketing
    /// views count when they report how many jobs a channel produced. Three kinds of row are NOT a
    /// real booking, and every one of them used to inflate the CRM Ads tab's "Booked orders":
    ///
    /// 1. <b>Cancelled</b> — the obvious one.
    /// 2. <b>Refunded</b> — Status is overwritten with "Refunded" the moment a refund clears the whole
    ///    balance, so a cancelled-then-refunded order no longer reads as "Cancelled" and slipped past
    ///    a bare status != Cancelled check. A fully-refunded order earned nothing either way, which is
    ///    also how AdminStatisticsController.TotalOrders and the CRM customer order counts treat it.
    /// 3. <b>Never paid</b> — the self-service flow inserts the order as Pending/IsPaid=false BEFORE
    ///    payment confirms (see BookingCreationService), so an abandoned checkout leaves a permanent
    ///    Pending row. OrderService auto-cancels those once the service date passes, but only lazily,
    ///    when someone happens to load the order — rows nobody opens stay Pending forever. Manual
    ///    payments (cash/Zelle/check) carry IsPaid=false BY DESIGN, so they qualify on PaymentMethod
    ///    instead; this is the same "paid" test AdminStatisticsController and the auto-cancel use.
    ///
    /// Status comparisons rely on MySQL's case-insensitive collation, like the rest of the codebase
    /// (see <see cref="OrderStatuses"/>) — keep this predicate in SQL, not in memory.
    /// </summary>
    public static class OrderBookedFilter
    {
        /// <summary>EF-translatable predicate — use as <c>.Where(OrderBookedFilter.IsRealBooking)</c>.</summary>
        public static readonly Expression<Func<Order, bool>> IsRealBooking = o =>
            o.Status != OrderStatuses.Cancelled
            && o.Status != OrderStatuses.Refunded
            && (o.IsPaid || o.PaymentMethod != PaymentMethod.Normal);
    }
}
