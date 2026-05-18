using DreamCleaningBackend.DTOs;

namespace DreamCleaningBackend.Services.Interfaces
{
    // Lifecycle of a user's loyalty discount:
    //   1. Background service (Phase 4) auto-activates X% at day-60 inactivity, upgrades to Y% at day-90.
    //   2. Admin can override via SetManualAsync / ClearAsync; the IsManualOverride flag freezes
    //      the value against background changes until the discount is consumed or admin clears it.
    //   3. Booking flow calls CalculateForOrderAsync to determine the candidate amount, then once
    //      the order is created and the discount actually applied, ApplyToOrderAsync consumes it.
    //   4. If that order is later cancelled, ReverseFromOrderAsync restores the % on the user.
    //
    // Every state-changing method writes an entry via IAuditService.LogLoyaltyDiscountChangeAsync
    // so the admin audit-history tab has a complete trail.
    public interface ILoyaltyDiscountService
    {
        Task<LoyaltyDiscountDto> GetForUserAsync(int userId);

        Task<LoyaltyDiscountDto> SetManualAsync(int userId, decimal percentage, int adminUserId);

        Task<LoyaltyDiscountDto> ClearAsync(int userId, int adminUserId);

        // Returns the candidate amount + percentage the loyalty discount would contribute to an
        // order with the given subtotal. Returns (0, 0) when the user has no active discount.
        // Does NOT consume anything — just a price preview.
        Task<(decimal amount, decimal percentage)> CalculateForOrderAsync(int userId, decimal subTotal);

        // Consume the loyalty discount on a freshly-created order. Reads the snapshot from
        // Order.LoyaltyDiscountAmount/Percentage so callers must persist that first. No-ops if
        // the order doesn't actually have a loyalty discount applied.
        Task ApplyToOrderAsync(int orderId);

        // Restore the loyalty discount to the user account when an order that consumed it is
        // cancelled. No-ops if the order didn't have one. Mirrors UserSpecialOffer reset.
        Task ReverseFromOrderAsync(int orderId);

        // Stacking gate (spec section 2.4). Pure function — given the loyalty candidate and the
        // subscription/promo dollar amounts the caller already computed, returns the post-stacking
        // values. Both the booking-flow backend and the booking page frontend use this same
        // algorithm so the values match across the wire.
        //
        // Rule:
        //   Round 1: loyalty vs subscription — higher wins, zero the loser.
        //   Round 2: round-1 winner vs promo/special/first-time — higher wins, zero the loser.
        //   Subscription that survived round 1 continues to stack with promo as before
        //   (existing behavior unchanged by this gate).
        //   Gift card, bubble points, reward balance: stack normally, untouched here.
        (decimal loyaltyAmount, decimal loyaltyPercentage, decimal subscriptionAmount, decimal promoAmount)
            ResolveStacking(decimal loyaltyCandidateAmount, decimal loyaltyCandidatePercentage,
                            decimal subscriptionAmount, decimal promoAmount);
    }
}
