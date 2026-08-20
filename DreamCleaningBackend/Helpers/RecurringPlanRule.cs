using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// SINGLE SOURCE OF TRUTH for "is this a recurring plan?".
    ///
    /// The seeded tiers are One Time (0 days), Weekly (7), Bi-Weekly (14) and Monthly (30), and a
    /// One Time booking stores <c>SubscriptionId = 1</c> rather than null — so a null check alone
    /// counts every single-cleaning order as recurring. <c>SubscriptionDays &gt; 0</c> is the only
    /// safe test, and it is the reason this rule is worth a file of its own.
    ///
    /// Do NOT use <c>Order.SubscriptionDiscountAmount &gt; 0</c> as a recurring marker. It looks
    /// like one and is wrong twice over (verified against production, 2026-08: 1 order matched it
    /// where 7 were genuinely on a plan):
    ///   • <c>BookingCreationService.ResolveDiscountsAsync</c> only grants the discount when the
    ///     customer ALREADY holds an active subscription for the tier, so the first booking on a
    ///     plan always records 0 — by design, and mirrored on the booking page, so preview and
    ///     charge agree.
    ///   • <c>OrderPricingCalculator.ResolveLoyaltyStacking</c> zeroes the subscription slot
    ///     whenever loyalty beats it, which is exactly what happens to long-standing regulars.
    /// </summary>
    public static class RecurringPlanRule
    {
        /// <summary>The kernel: a tier is recurring when it repeats on some cadence.</summary>
        public static bool IsRecurringTier(int subscriptionDays) => subscriptionDays > 0;

        /// <summary>True when the order was placed on a repeating plan rather than as a one-off.</summary>
        public static bool IsRecurringOrder(int? subscriptionId, int? tierDays) =>
            subscriptionId != null && tierDays != null && IsRecurringTier(tierDays.Value);

        /// <summary>
        /// True when the USER currently holds a live recurring plan. Note this is a SNAPSHOT of
        /// today and says nothing about what any past order was booked on — historical reporting
        /// must read the order's own tier through <see cref="IsRecurringOrder"/> instead.
        /// </summary>
        public static bool IsActiveUserSubscription(
            int? subscriptionId, int? tierDays, DateTime? expiresAt, DateTime nowUtc) =>
            IsRecurringOrder(subscriptionId, tierDays)
            && (expiresAt == null || expiresAt.Value >= nowUtc);

        /// <inheritdoc cref="IsActiveUserSubscription(int?, int?, DateTime?, DateTime)"/>
        public static bool IsActiveUserSubscription(User u, DateTime nowUtc) =>
            IsActiveUserSubscription(
                u.SubscriptionId, u.Subscription?.SubscriptionDays, u.SubscriptionExpiryDate, nowUtc);
    }
}
