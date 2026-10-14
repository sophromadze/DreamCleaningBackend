using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// What one order brought in, as the admin-facing screens describe it (2026-10): the Admins tab
    /// on Outgoing Payments and the percentage bonus set in the shifts panel. Pure, so every rule
    /// is asserted without a database.
    ///
    /// <list type="bullet">
    /// <item><b>Order total</b> = price without tips: the post-discount subtotal plus its sales tax,
    /// net of refunds (pro-rata, through <see cref="OrderRevenueMath.Split"/> — the one place orders
    /// become money).</item>
    /// <item><b>Net</b> = order total − sales tax − cleaners' wages − the order's staff bonuses. The
    /// owner asked for tax to come off here even though the reports treat retained tax as income;
    /// this is a per-admin performance figure, not the P&amp;L.</item>
    /// </list>
    ///
    /// These figures are DISPLAY and PAY-BASIS only. Nothing here is a cost and nothing here is
    /// added to Statistics or Finances — the bonuses reach those through
    /// <c>IAdminBonusService.GetOrderBonusCostsGelAsync</c> and nowhere else, so subtracting them
    /// here cannot count them twice.
    /// </summary>
    public static class AdminOrderEarnings
    {
        public readonly struct OrderFigures
        {
            /// <summary>Price without tips (sales tax included), net of refunds.</summary>
            public decimal OrderTotal { get; init; }

            /// <summary>Order total minus sales tax: the taxable revenue the company earned.</summary>
            public decimal Revenue { get; init; }

            public decimal Tax => OrderTotal - Revenue;
        }

        public static OrderFigures Figures(
            decimal subTotal,
            decimal discountAmount,
            decimal subscriptionDiscountAmount,
            decimal loyaltyDiscountAmount,
            decimal tax,
            decimal tips,
            decimal companyDevelopmentTips,
            decimal totalRefundedAmount)
        {
            var money = OrderRevenueMath.Split(
                subTotal, discountAmount, subscriptionDiscountAmount, loyaltyDiscountAmount,
                tax, tips, companyDevelopmentTips, totalRefundedAmount);
            return new OrderFigures
            {
                // Retained tax is left at 0 in the split, so Tax is the whole tax kept on the order.
                Revenue = money.Revenue,
                OrderTotal = money.Revenue + money.Tax + money.TaxRetained
            };
        }

        /// <summary>Revenue − wages − bonuses. May be negative; it is reported as it is.</summary>
        public static decimal Net(decimal revenue, decimal cleanerWages, decimal bonusesUsd)
            => revenue - cleanerWages - bonusesUsd;

        /// <summary>
        /// The rule in force for an order: the latest one whose start date is on or before the
        /// order's service date (a later-created row wins a tie on the same date). Null when none
        /// has started yet.
        /// </summary>
        public static AdminPercentBonusRule? RuleOn(IEnumerable<AdminPercentBonusRule> rulesForPerson, DateTime serviceDate)
        {
            var day = serviceDate.Date;
            return rulesForPerson
                .Where(r => r.StartDate.Date <= day)
                .OrderByDescending(r => r.StartDate.Date)
                .ThenByDescending(r => r.Id)
                .FirstOrDefault();
        }

        /// <summary>
        /// The percentage bonus on one order, in USD (orders are priced in dollars).
        /// <paramref name="netBeforePercentBonus"/> is the net with the FIXED bonuses already
        /// taken off but not this percentage — a percentage of a figure that already had itself
        /// subtracted would have no answer. A negative base earns nothing, never a claw-back.
        /// </summary>
        public static decimal PercentBonusUsd(
            AdminPercentBonusRule? rule, bool isNewCustomerOrder, decimal orderTotal, decimal netBeforePercentBonus)
        {
            if (rule == null) return 0m;
            // The percent for the kind of customer this order was — snapshotted on the order, the
            // same flag that picks between the new and returning per-order rates.
            var percent = isNewCustomerOrder ? rule.NewCustomerPercent : rule.ExistingCustomerPercent;
            if (percent <= 0m) return 0m;
            var basis = rule.Basis == AdminPercentBonusBasis.NetIncome ? netBeforePercentBonus : orderTotal;
            if (basis <= 0m) return 0m;
            return OrderPricingCalculator.Round2(basis * percent / 100m);
        }
    }
}
