namespace DreamCleaningBackend.Helpers
{
    // Staff salaries are paid TWICE a month, so Outgoing Payments shows each admin's month as two
    // instalments that can be settled independently. This is the whole rule, and it is pure so the
    // arithmetic is asserted without a database.
    //
    // The instalments are deliberately NOT tied to calendar dates ("1st–15th" / "16th–end"). The
    // owner pays mid-month and at month end, but not on fixed days, and printing a date range the
    // payment does not actually land in would be a claim the page cannot back up. They are simply
    // the first and second payment of the month.
    public static class SalaryPaymentSchedule
    {
        /// <summary>First payment of the month.</summary>
        public const int FirstHalf = 1;

        /// <summary>Second payment of the month.</summary>
        public const int SecondHalf = 2;

        public static readonly int[] Halves = { FirstHalf, SecondHalf };

        public static bool IsValidHalf(int half) => half == FirstHalf || half == SecondHalf;

        public static string Label(int half) => half == FirstHalf ? "First payment" : "Second payment";

        /// <summary>
        /// Splits a month's salary into the two instalments. They ALWAYS re-add to exactly the
        /// total: the first is rounded down to the cent and the second takes the remainder, so an
        /// odd cent lands on the second payment. That is the right way round — the second payment
        /// is the one that settles the month, so it is the one that absorbs the difference, and no
        /// rounding rule can make the pair overpay.
        /// </summary>
        public static (decimal First, decimal Second) Split(decimal monthTotal)
        {
            var first = decimal.Round(monthTotal / 2m, 2, MidpointRounding.ToZero);
            return (first, monthTotal - first);
        }

        /// <summary>One instalment's amount. Convenience over <see cref="Split"/>.</summary>
        public static decimal AmountFor(decimal monthTotal, int half)
        {
            var (first, second) = Split(monthTotal);
            return half == FirstHalf ? first : second;
        }

        /// <summary>
        /// The share of a month's BONUSES that rides on one instalment: all of it on the second,
        /// none on the first.
        /// </summary>
        /// <remarks>
        /// Bonuses are earned per order across the whole month, so how much somebody has earned is
        /// not known until the month is over — which is precisely when the second payment goes out.
        /// Splitting them across both instalments would mean paying half of a figure nobody can
        /// compute yet, so the first payment stays the salary alone.
        /// </remarks>
        public static decimal BonusFor(decimal monthBonus, int half)
            => half == SecondHalf ? monthBonus : 0m;

        /// <summary>
        /// One instalment in full: its half of the salary, its share of the bonuses, and the sum.
        /// The two instalments always re-add to salary + bonus exactly.
        /// </summary>
        public static (decimal Salary, decimal Bonus, decimal Total) Instalment(
            decimal monthSalary, decimal monthBonus, int half)
        {
            var salary = AmountFor(monthSalary, half);
            var bonus = BonusFor(monthBonus, half);
            return (salary, bonus, salary + bonus);
        }
    }
}
