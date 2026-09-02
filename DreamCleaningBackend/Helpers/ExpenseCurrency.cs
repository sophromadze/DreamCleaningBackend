namespace DreamCleaningBackend.Helpers
{
    // Almost every expense is billed in USD. Staff salaries are the exception — the admins are paid
    // in Georgian lari — so a salary row records the currency it was ENTERED in and the reporting
    // side converts. Same arrangement the per-order admin bonus already uses: the figure the owner
    // types is the figure they hand over, and USD is derived from it, never the other way round.
    public static class ExpenseCurrency
    {
        public const string Usd = "USD";
        public const string Gel = "GEL";

        /// <summary>
        /// Anything that is not an explicit "GEL" is USD. Deliberately permissive: an unrecognised
        /// value means an expense reports at face value rather than being silently multiplied by an
        /// exchange rate that was never meant to apply to it.
        /// </summary>
        public static string Normalize(string? currency)
            => string.Equals((currency ?? string.Empty).Trim(), Gel, StringComparison.OrdinalIgnoreCase)
                ? Gel
                : Usd;

        public static bool IsGel(string? currency) => Normalize(currency) == Gel;

        /// <summary>
        /// Which categories may be entered in a currency other than USD. Only salaries: a supplier
        /// invoice or an ad bill arrives in dollars, and offering a currency toggle on those is an
        /// invitation to mis-tag one and quietly report it at ~2.7× its real cost.
        /// </summary>
        public static bool AllowsCurrencyChoice(int categoryId) => SalaryExpenseRules.IsSalaryCategory(categoryId);

        /// <summary>
        /// The USD figure that reaches Statistics and Finances.
        /// <paramref name="usdPerGel"/> is the month's locked rate from MonthlyFinancialSnapshot —
        /// the SAME snapshot the admin bonuses convert through, so one month cannot be reported at
        /// two different rates depending on which cost you are looking at.
        /// </summary>
        /// <remarks>
        /// A non-positive rate returns the amount unconverted rather than zero. Losing an expense
        /// entirely (understating costs, overstating profit) is a worse failure than reporting a
        /// lari figure as though it were dollars, which at least stays visible and obviously wrong.
        /// FinancialRateService only ever yields a positive rate — it falls back to a configured
        /// default when the NBG API is down — so this is a guard, not a path.
        /// </remarks>
        public static decimal ToUsd(decimal amount, string? currency, decimal usdPerGel)
        {
            if (!IsGel(currency)) return amount;
            if (usdPerGel <= 0) return amount;
            return decimal.Round(amount * usdPerGel, 2, MidpointRounding.AwayFromZero);
        }

        /// <summary>The symbol the amount is written with. Display only.</summary>
        public static string Symbol(string? currency) => IsGel(currency) ? "₾" : "$";
    }
}
