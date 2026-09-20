using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// SINGLE SOURCE OF TRUTH for "the customer picked a plan on their profile".
    ///
    /// ══ THE ONE RULE ══
    ///
    /// Choosing a plan from the account area records a PREFERENCE and nothing else. It never
    /// writes <see cref="User.SubscriptionId"/>, never moves
    /// <see cref="User.SubscriptionExpiryDate"/>, never grants a discount and never charges
    /// anything. A plan still becomes REAL exactly the way it always has: by booking a cleaning
    /// on that tier, which is what <c>BookingController</c> /
    /// <c>SavedCardChargeService</c> / <c>CombinedPaymentFollowUpService</c> activate through
    /// <c>ISubscriptionService.ActivateSubscription</c> once the order is placed or paid.
    ///
    /// ══ WHY IT IS A PREFERENCE AND NOT AN ACTIVATION ══
    ///
    /// <c>BookingCreationService.ResolveDiscountsAsync</c> grants the subscription discount only
    /// when the order owner ALREADY holds a live subscription of the selected tier. That single
    /// condition is what produces the company's actual offer — the first cleaning on a plan is
    /// full price, the second one in a row is discounted — and the booking page mirrors it so the
    /// preview and the charge agree.
    ///
    /// Writing <c>SubscriptionId</c> from a profile button would therefore hand the discount to
    /// the customer's VERY NEXT order, i.e. exactly the first cleaning the rule exists to exclude,
    /// on an account that has never booked anything. It would also make
    /// <c>RecurringPlanRule.IsActiveUserSubscription</c> answer true for somebody with no orders,
    /// which CRM/customer-stats reads as an active plan holder.
    ///
    /// So the preference is a separate column. The pricing chain is untouched by design: nothing
    /// in <c>OrderPricingCalculator</c>, <c>BookingCreationService</c>, the payment flow or the
    /// reporting queries reads <see cref="User.PreferredSubscriptionId"/> — only the booking
    /// page's initial tier selection does, which is the same choice the customer could make with
    /// one tap on that page anyway.
    /// </summary>
    public static class PlanSelectionPolicy
    {
        /// <summary>
        /// A plan may be chosen from the profile when it exists, is active, and is a REPEATING
        /// tier. "One Time" is the absence of a plan, not a plan to opt into — offering it as a
        /// choice would leave the customer wondering what selecting it bought them.
        /// </summary>
        public static bool IsSelectable(Subscription? subscription) =>
            subscription != null
            && subscription.IsActive
            && RecurringPlanRule.IsRecurringTier(subscription.SubscriptionDays);

        /// <summary>
        /// True when the customer's very next cleaning on <paramref name="tierDays"/> would carry
        /// the plan discount — i.e. they already hold that live tier. False both for somebody who
        /// has only expressed a preference and for somebody whose plan has lapsed.
        ///
        /// This is a READ of the same condition <c>ResolveDiscountsAsync</c> enforces, never a
        /// second implementation of it: it decides what the Plan tab SAYS, never what anybody is
        /// charged.
        /// </summary>
        public static bool NextCleaningWouldBeDiscounted(User user, int tierDays, DateTime nowUtc) =>
            RecurringPlanRule.IsActiveUserSubscription(user, nowUtc)
            && user.Subscription != null
            && user.Subscription.SubscriptionDays == tierDays;
    }
}
