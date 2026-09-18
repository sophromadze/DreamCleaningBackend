using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// "HOW MUCH OF THIS ORDER'S OWN TOTAL IS STILL OWED?" — stated once, because part-payments
    /// made the answer stop being "all of it or none of it".
    ///
    /// Before part-payments (2026-09) an order was paid or it was not: <c>IsPaid</c> was the whole
    /// story and the amount to charge was always <c>Order.Total</c>. An order can now hold money
    /// that does not settle it, and every surface that used to print "Unpaid — $2,743.65" has to
    /// print "Partially paid — $1,000.00 of $2,743.65" instead. Spelling that arithmetic at each
    /// call site is how a payment page ends up charging a different figure from the one the admin
    /// panel shows.
    ///
    /// Two things this deliberately does NOT know about:
    ///
    /// <list type="bullet">
    /// <item><b>Order-edit top-ups.</b> <see cref="Order.AmountPaid"/> covers the order's own
    /// total only. Money owed because an admin later RAISED the price lives on
    /// <see cref="OrderUpdateHistory"/> and is collected by its own flow — see
    /// <c>PendingUpdateAmount</c>. An order can owe both; they are added up for display, never
    /// merged into one balance.</item>
    /// <item><b>Refunds.</b> <see cref="Order.TotalRefundedAmount"/> is money going the other way
    /// and never reduces what is still owed on an unpaid order.</item>
    /// </list>
    ///
    /// <b>EF cannot translate a method call inside a projection or a bigger Where</b>, so the
    /// handful of in-query call sites write these expressions out by hand — the same
    /// arrangement <see cref="OrderPaymentFilter"/> documents. This file is where the rule is
    /// STATED; grep for <c>AmountPaid</c> to find the copies.
    /// </summary>
    public static class OrderBalance
    {
        /// <summary>Stripe will not accept a charge below this, so a balance under it cannot be
        /// collected online and is treated as settled. Mirrors the constant the booking flow has
        /// always used for gift-card-covered orders.</summary>
        public const decimal StripeMinimumChargeAmount = 0.50m;

        /// <summary>Anything below this is not money — it is rounding.</summary>
        public const decimal MinimumMeaningfulAmount = 0.01m;

        /// <summary>
        /// What is still owed on the order's own total. Zero once <see cref="Order.IsPaid"/> is
        /// set, whatever the columns say: the normal full-payment flow settles an order without
        /// ever touching <see cref="Order.AmountPaid"/>, so reading the subtraction alone would
        /// report every ordinary paid order as owing its whole total.
        /// </summary>
        public static decimal AmountDue(Order order)
        {
            if (order.IsPaid) return 0m;
            return OrderPricingCalculator.Round2(Math.Max(0m, order.Total - order.AmountPaid));
        }

        /// <summary>True when money has arrived against this order's total but has not settled it.
        /// This is what the "Partially paid" pill is driven by — never a bare
        /// <c>AmountPaid &gt; 0</c>, which stays true after the final payment lands.</summary>
        public static bool IsPartiallyPaid(Order order) =>
            !order.IsPaid
            && order.PaymentMethod == PaymentMethod.Normal
            && order.AmountPaid >= MinimumMeaningfulAmount
            && AmountDue(order) >= MinimumMeaningfulAmount;

        /// <summary>True when this payment leaves nothing collectable behind, so the order should
        /// be marked paid. The threshold is Stripe's minimum rather than a cent: a 30¢ remainder
        /// can never be charged online, and leaving the order unpaid over it would strand it.</summary>
        public static bool SettlesOrder(decimal amountDueAfterPayment) =>
            amountDueAfterPayment < StripeMinimumChargeAmount;

        /// <summary>
        /// Money taken beyond the order's total — only reachable when an admin lowers the price
        /// after a deposit was paid. Reported, never auto-refunded: the order's price moving is a
        /// decision a person just made, and the refund is theirs to make too.
        /// </summary>
        public static decimal OverpaidAmount(Order order) =>
            OrderPricingCalculator.Round2(Math.Max(0m, order.AmountPaid - order.Total));

        /// <summary>
        /// Whether an admin may ask this order for a part-payment at all, with the reason when not.
        /// Refused for anything whose money is not the residential Stripe balance:
        ///
        /// <list type="bullet">
        /// <item>a settled order — there is no original total left to split;</item>
        /// <item>a cancelled or refunded one;</item>
        /// <item>a method handled outside Stripe (Cash/Zelle/Check/Other/Invoice) — for Invoice in
        /// particular the commercial ledger, not this, is what says how much is owed;</item>
        /// <item>a recurring occurrence, whose payments are already sequenced by
        /// <c>RecurringPaymentPolicy</c> — two rules deciding what the customer may pay next would
        /// contradict each other.</item>
        /// </list>
        /// </summary>
        public static bool CanRequestPartialPayment(Order order, out string? refusal)
        {
            if (order.IsPaid)
            {
                refusal = "This order is already paid in full.";
                return false;
            }
            if (OrderStatuses.IsCancelled(order.Status) || OrderStatuses.IsRefunded(order.Status))
            {
                refusal = "This order is cancelled and cannot take payments.";
                return false;
            }
            if (order.PaymentMethod != PaymentMethod.Normal)
            {
                refusal = "This order is settled outside the website, so it has no online balance to split.";
                return false;
            }
            if (order.RecurringSeriesId.HasValue)
            {
                refusal = "Recurring cleanings are paid one occurrence at a time and can't be split.";
                return false;
            }
            if (AmountDue(order) < StripeMinimumChargeAmount)
            {
                refusal = "This order has nothing left to collect.";
                return false;
            }

            refusal = null;
            return true;
        }
    }
}
