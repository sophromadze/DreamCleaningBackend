using System.Linq.Expressions;
using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// "HAS THIS ORDER ACTUALLY BEEN PAID FOR?" — one definition, for reporting.
    ///
    /// Until 2026-09 the answer was spelled `o.IsPaid || o.PaymentMethod != PaymentMethod.Normal`
    /// in about a dozen queries, and it was right: `IsPaid` is Stripe-only by design, and every
    /// other method meant an admin had recorded money that had already changed hands.
    ///
    /// <b>The Invoice method broke that equivalence.</b> An invoice-billed cleaning is handled
    /// outside Stripe like the others, but choosing the method settles nothing — the client pays
    /// their invoice later, and until they do the order is a job that has been booked and not paid
    /// for. Counting it the old way would report a commercial cleaning as revenue on the day it was
    /// entered, potentially weeks before any money arrived and even if it never did.
    ///
    /// So the rule gains one clause: an Invoice order counts only once
    /// <see cref="Order.InvoicePaidAt"/> is stamped, which happens inside the transaction that
    /// activates the order when its invoice reaches a zero balance.
    ///
    /// <b>NOTHING HISTORICAL MOVES.</b> No row written before this feature can carry
    /// <see cref="PaymentMethod.Invoice"/>, so the extra clause is provably a no-op over existing
    /// data — which is exactly why Invoice got its own timestamp column rather than reusing
    /// <c>ManualPaymentRecordedAt</c>, whose value on legacy rows cannot be relied on.
    ///
    /// <b>EF cannot translate a method call inside a bigger <c>Where</c></b>, so in-query call
    /// sites that compose this with other conditions still write the expression out by hand — the
    /// same arrangement <c>CleanerPayrollCalculator.HasCleanerHoursService</c> documents. This file
    /// is where the rule is STATED; grep for <c>InvoicePaidAt</c> to find the copies.
    /// </summary>
    public static class OrderPaymentFilter
    {
        /// <summary>EF-translatable on its own: <c>.Where(OrderPaymentFilter.IsSettled)</c>.</summary>
        public static readonly Expression<Func<Order, bool>> IsSettled = o =>
            o.IsPaid
            || (o.PaymentMethod != PaymentMethod.Normal
                && (o.PaymentMethod != PaymentMethod.Invoice || o.InvoicePaidAt != null));

        /// <summary>In-memory equivalent, for code holding a loaded entity.</summary>
        public static bool IsSettledInMemory(Order order) =>
            order.IsPaid
            || (order.PaymentMethod != PaymentMethod.Normal
                && (order.PaymentMethod != PaymentMethod.Invoice || order.InvoicePaidAt != null));
    }
}
