using DreamCleaningBackend.Helpers;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    // Salaries are entered in the currency they are paid in (the admins are paid in lari) and
    // settled in two instalments a month. Both rules are pure, so the arithmetic that decides what
    // reaches Finances — and what somebody is handed — is asserted without a database.
    public class SalaryCurrencyAndScheduleTests
    {
        // ── Currency ──────────────────────────────────────────────────────────────

        [Fact]
        public void OnlySalariesMayBeEnteredInAForeignCurrency()
        {
            // A supplier invoice or an ad bill arrives in dollars. Offering a currency toggle there
            // is an invitation to mis-tag one and report it at roughly 2.7x its real cost.
            Assert.True(ExpenseCurrency.AllowsCurrencyChoice(SalaryExpenseRules.SalariesCategoryId));
            Assert.False(ExpenseCurrency.AllowsCurrencyChoice(0));  // Subscriptions
            Assert.False(ExpenseCurrency.AllowsCurrencyChoice(3));  // Marketing
        }

        [Theory]
        [InlineData("GEL", "GEL")]
        [InlineData("gel", "GEL")]
        [InlineData("  GeL  ", "GEL")]
        [InlineData("USD", "USD")]
        [InlineData("", "USD")]
        [InlineData(null, "USD")]
        [InlineData("EUR", "USD")]   // unrecognised reports at face value rather than being converted
        public void AnythingButAnExplicitGelIsUsd(string? input, string expected)
        {
            Assert.Equal(expected, ExpenseCurrency.Normalize(input));
        }

        [Fact]
        public void AGelSalaryIsConvertedAtTheMonthsRate()
        {
            // 1,800 GEL at 0.37 USD/GEL = 666.00 — the same conversion the admin bonuses use.
            Assert.Equal(666.00m, ExpenseCurrency.ToUsd(1800m, "GEL", 0.37m));
        }

        [Fact]
        public void AUsdExpenseIsNeverTouchedByTheRate()
        {
            Assert.Equal(1800m, ExpenseCurrency.ToUsd(1800m, "USD", 0.37m));
        }

        [Fact]
        public void AMissingRateLeavesTheCostVisibleRatherThanZeroingIt()
        {
            // Losing an expense entirely understates costs and overstates profit. Reporting lari as
            // dollars is wrong too, but it stays on screen where somebody notices it.
            Assert.Equal(1800m, ExpenseCurrency.ToUsd(1800m, "GEL", 0m));
            Assert.Equal(1800m, ExpenseCurrency.ToUsd(1800m, "GEL", -1m));
        }

        [Fact]
        public void ConversionRoundsToCents()
        {
            // 1234.56 * 0.372 = 459.25632
            Assert.Equal(459.26m, ExpenseCurrency.ToUsd(1234.56m, "GEL", 0.372m));
        }

        // ── The two monthly instalments ───────────────────────────────────────────

        [Fact]
        public void AMonthSplitsIntoExactlyTwoPaymentsThatReAddToIt()
        {
            var (first, second) = SalaryPaymentSchedule.Split(1800m);
            Assert.Equal(900m, first);
            Assert.Equal(900m, second);
            Assert.Equal(1800m, first + second);
        }

        [Theory]
        [InlineData(1000.01)]
        [InlineData(999.99)]
        [InlineData(0.01)]
        [InlineData(1234.57)]
        [InlineData(0)]
        public void TheTwoPaymentsAlwaysReAddToTheMonthExactly(decimal total)
        {
            var (first, second) = SalaryPaymentSchedule.Split(total);
            Assert.Equal(total, first + second);
        }

        [Fact]
        public void AnOddCentLandsOnTheSecondPayment()
        {
            // The second payment settles the month, so it is the one that absorbs the difference —
            // and rounding the first DOWN means the pair can never overpay.
            var (first, second) = SalaryPaymentSchedule.Split(1000.01m);
            Assert.Equal(500.00m, first);
            Assert.Equal(500.01m, second);
            Assert.True(first <= second);
        }

        [Fact]
        public void AmountForMatchesSplit()
        {
            var (first, second) = SalaryPaymentSchedule.Split(777.77m);
            Assert.Equal(first, SalaryPaymentSchedule.AmountFor(777.77m, SalaryPaymentSchedule.FirstHalf));
            Assert.Equal(second, SalaryPaymentSchedule.AmountFor(777.77m, SalaryPaymentSchedule.SecondHalf));
        }

        [Fact]
        public void OnlyTheTwoInstalmentsExist()
        {
            // The pay endpoint takes the instalment number straight off the URL, so a third one
            // must be rejected rather than silently treated as the second.
            Assert.True(SalaryPaymentSchedule.IsValidHalf(1));
            Assert.True(SalaryPaymentSchedule.IsValidHalf(2));
            Assert.False(SalaryPaymentSchedule.IsValidHalf(0));
            Assert.False(SalaryPaymentSchedule.IsValidHalf(3));
            Assert.Equal(new[] { 1, 2 }, SalaryPaymentSchedule.Halves);
        }

        [Fact]
        public void TheInstalmentsAreNamedWithoutClaimingCalendarDates()
        {
            // The owner pays mid-month and at month end, but not on fixed days — printing "1st-15th"
            // would be a claim the page cannot back up.
            Assert.Equal("First payment", SalaryPaymentSchedule.Label(1));
            Assert.Equal("Second payment", SalaryPaymentSchedule.Label(2));
        }

        // ── Bonuses ride on the second payment ────────────────────────────────────

        [Fact]
        public void TheWholeMonthsBonusIsPaidWithTheSecondInstalment()
        {
            // Bonuses are earned per order across the month, so nobody knows the figure until the
            // month is over — which is exactly when the second payment goes out. Splitting them
            // would mean paying half of a number that cannot be computed yet.
            Assert.Equal(0m, SalaryPaymentSchedule.BonusFor(200m, SalaryPaymentSchedule.FirstHalf));
            Assert.Equal(200m, SalaryPaymentSchedule.BonusFor(200m, SalaryPaymentSchedule.SecondHalf));
        }

        [Fact]
        public void TheOwnersWorkedExample()
        {
            // 2,100 GEL salary for the month, 200 GEL of bonuses.
            // First payment 1,050. Second payment 1,050 + 200 = 1,250.
            var first = SalaryPaymentSchedule.Instalment(2100m, 200m, SalaryPaymentSchedule.FirstHalf);
            var second = SalaryPaymentSchedule.Instalment(2100m, 200m, SalaryPaymentSchedule.SecondHalf);

            Assert.Equal(1050m, first.Salary);
            Assert.Equal(0m, first.Bonus);
            Assert.Equal(1050m, first.Total);

            Assert.Equal(1050m, second.Salary);
            Assert.Equal(200m, second.Bonus);
            Assert.Equal(1250m, second.Total);
        }

        [Fact]
        public void TheTwoInstalmentsReAddToSalaryPlusBonusExactly()
        {
            var first = SalaryPaymentSchedule.Instalment(999.99m, 200.01m, SalaryPaymentSchedule.FirstHalf);
            var second = SalaryPaymentSchedule.Instalment(999.99m, 200.01m, SalaryPaymentSchedule.SecondHalf);

            Assert.Equal(999.99m + 200.01m, first.Total + second.Total);
            // The salary halves still settle between themselves; the bonus does not disturb that.
            Assert.Equal(999.99m, first.Salary + second.Salary);
        }

        [Fact]
        public void AMonthWithNoBonusIsUnchanged()
        {
            var second = SalaryPaymentSchedule.Instalment(2100m, 0m, SalaryPaymentSchedule.SecondHalf);
            Assert.Equal(1050m, second.Total);
            Assert.Equal(0m, second.Bonus);
        }

        [Fact]
        public void ABonusIsPayableEvenWithNoSalaryOnFile()
        {
            // Somebody on commission only. The second payment is the bonus alone rather than
            // nothing at all.
            var first = SalaryPaymentSchedule.Instalment(0m, 350m, SalaryPaymentSchedule.FirstHalf);
            var second = SalaryPaymentSchedule.Instalment(0m, 350m, SalaryPaymentSchedule.SecondHalf);

            Assert.Equal(0m, first.Total);
            Assert.Equal(350m, second.Total);
        }

        [Fact]
        public void AGelBonusConvertsWithTheSameRateEverythingElseUses()
        {
            // 200 GEL at the market rate, not a second rate of its own.
            Assert.Equal(76.64m, ExpenseCurrency.ToUsd(200m, "GEL", 0.38319m));
        }
    }
}
