using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// Who may change an order's promo/special and subscription discounts in the admin order
    /// editor: a SuperAdmin only (owner's rule, 2026-09). An Admin — granted direct saves or not —
    /// sees both boxes read-only.
    ///
    /// "Read-only" still MOVES. When an Admin changes the subtotal (a quantity, an extra, a typed
    /// SubTotal or Total), the editor re-scales both discounts to the new subtotal with
    /// <c>rescaleDiscountToSubTotal</c> so a 25% promo stays 25% — that is the order's discount
    /// following its price, not somebody changing it. So the server cannot refuse "any different
    /// figure"; it accepts the stored figure while the subtotal is unchanged, and the stored
    /// figure re-scaled to the proposed subtotal once it moves. Keeping the old dollar amount on
    /// a moved subtotal is refused: the editor never sends that, and it changes the discount rate.
    ///
    /// The tolerance covers the typed-Total path (admin-total-solve.ts), which re-scales against an
    /// ESTIMATE of the subtotal and then anchors the stored subtotal a few cents away from it.
    ///
    /// Since 2026-10 "following its price" means the BOOKING rule the order recorded
    /// (OrderPricingCalculator.ResolveEditedDiscounts): a percentage stays that percentage, and a
    /// fixed-amount promo stays fixed. An order with no recorded rule keeps the proportional
    /// re-scale, which is what the decimal overload below still checks.
    /// </summary>
    public static class OrderDiscountEditPolicy
    {
        public const string RefusalMessage = "Only a SuperAdmin can change an order's discounts.";

        private const decimal Tolerance = 0.05m;

        public static bool MayEditDiscounts(UserRole role) => role == UserRole.SuperAdmin;

        /// <summary>
        /// True when the edit leaves both discounts as the order's own: unchanged while the
        /// subtotal stays put, or exactly what the order's booking rules give on the proposed
        /// subtotal once it moves. <paramref name="order"/> is the order as stored right now.
        /// </summary>
        public static bool KeepsDiscounts(Order order, SuperAdminUpdateOrderDto dto)
        {
            var proposedSubTotal = dto.SubTotal ?? order.SubTotal;
            var (promo, subscription, _) = OrderPricingCalculator.ResolveEditedDiscounts(order, proposedSubTotal);
            return FollowsRule(order.DiscountAmount, order.SubTotal, dto.DiscountAmount, proposedSubTotal, promo)
                && FollowsRule(order.SubscriptionDiscountAmount, order.SubTotal, dto.SubscriptionDiscountAmount, proposedSubTotal, subscription);
        }

        private static bool FollowsRule(
            decimal currentDiscount, decimal currentSubTotal, decimal? proposedDiscount, decimal proposedSubTotal, decimal ruleDiscount)
        {
            if (!proposedDiscount.HasValue) return true;
            var proposed = proposedDiscount.Value;
            if (Math.Abs(proposed - ruleDiscount) <= Tolerance) return true;
            // An unchanged subtotal may always keep the stored figure.
            return Math.Abs(proposedSubTotal - currentSubTotal) <= Tolerance
                && Math.Abs(proposed - currentDiscount) <= Tolerance;
        }

        /// <summary>
        /// True when the edit leaves both discounts as the order's own discounts — unchanged, or
        /// re-scaled with the subtotal. <paramref name="currentSubTotal"/> and the two current
        /// amounts are the order as stored right now.
        /// </summary>
        public static bool KeepsDiscounts(
            decimal currentSubTotal,
            decimal currentDiscount,
            decimal currentSubscriptionDiscount,
            SuperAdminUpdateOrderDto dto)
        {
            var proposedSubTotal = dto.SubTotal ?? currentSubTotal;
            return IsUnchangedOrRescaled(currentDiscount, currentSubTotal, dto.DiscountAmount, proposedSubTotal)
                && IsUnchangedOrRescaled(currentSubscriptionDiscount, currentSubTotal, dto.SubscriptionDiscountAmount, proposedSubTotal);
        }

        public static bool IsUnchangedOrRescaled(
            decimal currentDiscount, decimal currentSubTotal, decimal? proposedDiscount, decimal proposedSubTotal)
        {
            // Not part of the edit — the server leaves the column alone.
            if (!proposedDiscount.HasValue) return true;

            var proposed = proposedDiscount.Value;

            // Nothing to scale against: the only legitimate figure is the stored one.
            if (currentSubTotal <= 0) return Math.Abs(proposed - currentDiscount) <= Tolerance;

            // The stored figure is accepted as-is ONLY while the subtotal stays put. Once the
            // subtotal moves, the editor always re-scales the discount with it, so a stored
            // dollar amount carried onto a DIFFERENT subtotal is a changed discount rate - e.g.
            // a $100 promo kept while a $400 subtotal drops to $100 is a 100% discount - and that
            // is exactly the change only a SuperAdmin may make.
            if (Math.Abs(proposedSubTotal - currentSubTotal) <= Tolerance)
                return Math.Abs(proposed - currentDiscount) <= Tolerance;

            // Same formula as the frontend's rescaleDiscountToSubTotal.
            var rescaled = Math.Round(
                proposedSubTotal * (currentDiscount / currentSubTotal), 2, MidpointRounding.AwayFromZero);
            return Math.Abs(proposed - rescaled) <= Tolerance;
        }
    }
}
