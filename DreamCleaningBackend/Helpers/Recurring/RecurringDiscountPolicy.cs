using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;

namespace DreamCleaningBackend.Helpers.Recurring;

/// <summary>Only an active lifetime entitlement or the administrator's standing series agreement.</summary>
public static class RecurringDiscountPolicy
{
    /// <summary>
    /// What one recurring cleaning's loyalty discount comes to.
    ///
    /// <para><b>Percent and Amount are the same figure said twice</b>, not two discounts. The
    /// series agreement may have been written either way — "10% off every clean" or "$50 off
    /// every clean" — and whichever way it was written, an occurrence is stored with BOTH, so
    /// every surface that already reads <c>Order.LoyaltyDiscountPercentage</c> keeps working and
    /// the amount the admin promised is the amount that comes off.</para>
    /// </summary>
    public readonly record struct Result(decimal Percent, decimal Amount, string Source);

    /// <summary>
    /// Resolve the discount for a subtotal.
    ///
    /// <para><b>Lifetime and series never stack</b> — the better of the two applies, compared as
    /// MONEY rather than as percentages, because a fixed series amount has no percentage to
    /// compare until a subtotal exists. That is the whole reason the subtotal is a parameter.</para>
    ///
    /// <para>A fixed amount is <b>clamped to the subtotal</b>: a $200 standing discount on a $150
    /// cleaning takes the cleaning to zero, never below it.</para>
    /// </summary>
    public static Result Resolve(
        User? user, decimal? seriesPercent, decimal? seriesAmount, bool commercial, decimal subTotal)
    {
        if (commercial || subTotal <= 0m) return new Result(0m, 0m, "None");

        var lifetimePercent = user is { IsActive: true, IsDeleted: false, LoyaltyDiscountIsLifetime: true }
            ? Math.Clamp(user.LoyaltyDiscountPercentage, 0m, 100m)
            : 0m;
        var lifetimeAmount = OrderPricingCalculator.Round2(subTotal * lifetimePercent / 100m);

        var (recurringPercent, recurringAmount) = seriesAmount.HasValue
            ? FromAmount(Math.Clamp(seriesAmount.Value, 0m, subTotal), subTotal)
            : FromPercent(Math.Clamp(seriesPercent ?? 0m, 0m, 100m), subTotal);

        if (lifetimeAmount > 0m && lifetimeAmount >= recurringAmount)
            return new Result(lifetimePercent, lifetimeAmount, "Lifetime Loyalty");

        return recurringAmount > 0m
            ? new Result(recurringPercent, recurringAmount, "Recurring Series")
            : new Result(0m, 0m, "None");
    }

    private static (decimal Percent, decimal Amount) FromPercent(decimal percent, decimal subTotal) =>
        (percent, OrderPricingCalculator.Round2(subTotal * percent / 100m));

    /// <summary>
    /// A fixed amount carried back into the percentage column every other loyalty surface reads.
    /// Rounded to the 2dp that column stores, so a later re-price of the same order reproduces
    /// the amount to within a cent rather than inventing a different discount.
    /// </summary>
    private static (decimal Percent, decimal Amount) FromAmount(decimal amount, decimal subTotal) =>
        (Math.Round(amount / subTotal * 100m, 2, MidpointRounding.AwayFromZero), OrderPricingCalculator.Round2(amount));
}
