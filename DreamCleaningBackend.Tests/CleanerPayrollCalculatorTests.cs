using DreamCleaningBackend.Services;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// The per-cleaner payout split behind the Outgoing Payments page.
    ///
    /// The page replaced a manager typing these sums into WhatsApp by hand, so several of the
    /// cases below are lifted straight from real messages — if the calculator ever disagrees with
    /// one of them, it is the calculator that is wrong.
    /// </summary>
    public class CleanerPayrollCalculatorTests
    {
        private static CleanerPayrollCalculator.AssignmentInput Assignment(
            int id, decimal? rate = null, decimal? minutes = null) =>
            new()
            {
                OrderCleanerId = id,
                CleanerId = id,
                SalaryHourlyRate = rate,
                SalaryBillableMinutes = minutes
            };

        private static List<CleanerPayrollCalculator.AssignmentInput> Assignments(int count) =>
            Enumerable.Range(1, count).Select(i => Assignment(i)).ToList();

        // ===== The real WhatsApp payouts this page replaced =====

        /// <summary>"Residential, 2 cleaners, 9 hours (4.5 each), $21 → $94.50 each."</summary>
        [Fact]
        public void ResidentialTwoCleaners_MatchesTheHandWrittenPayout()
        {
            var result = CleanerPayrollCalculator.Build(
                totalDuration: 540m, maidsCount: 2, hasCleanerService: false,
                orderHourlyRate: 21m, tips: 0m, assignments: Assignments(2));

            Assert.All(result.Lines, l => Assert.Equal(270m, l.BillableMinutes));
            Assert.All(result.Lines, l => Assert.Equal(94.50m, l.Salary));
            Assert.Equal(189.00m, result.TotalSalary);
        }

        /// <summary>"Move in/out, 2 cleaners, 7 hours (3.5 each), $21 → $73.50 each."</summary>
        [Fact]
        public void MoveInOutTwoCleaners_MatchesTheHandWrittenPayout()
        {
            var result = CleanerPayrollCalculator.Build(
                totalDuration: 420m, maidsCount: 2, hasCleanerService: false,
                orderHourlyRate: 21m, tips: 0m, assignments: Assignments(2));

            Assert.All(result.Lines, l => Assert.Equal(73.50m, l.Salary));
            Assert.Equal(147.00m, result.TotalSalary);
        }

        /// <summary>"Deep, 1 cleaner, 3 hours total, $20 → $60."</summary>
        [Fact]
        public void DeepSingleCleaner_MatchesTheHandWrittenPayout()
        {
            var result = CleanerPayrollCalculator.Build(
                totalDuration: 180m, maidsCount: 1, hasCleanerService: false,
                orderHourlyRate: 20m, tips: 0m, assignments: Assignments(1));

            Assert.Equal(60.00m, Assert.Single(result.Lines).Salary);
            Assert.Equal(60.00m, result.TotalSalary);
        }

        // ===== The split is over the ASSIGNED cleaners =====

        /// <summary>
        /// An order priced for 2 maids that only one cleaner turned up to pays that one cleaner
        /// for the WHOLE job. Splitting by MaidsCount would show — and pay — half.
        /// </summary>
        [Fact]
        public void OneCleanerOnATwoMaidOrder_IsPaidForTheWholeJob()
        {
            var result = CleanerPayrollCalculator.Build(
                totalDuration: 540m, maidsCount: 2, hasCleanerService: false,
                orderHourlyRate: 21m, tips: 0m, assignments: Assignments(1));

            Assert.Equal(540m, Assert.Single(result.Lines).BillableMinutes);
            Assert.Equal(189.00m, result.TotalSalary);
            Assert.Equal(1, result.AssignedCount);
        }

        /// <summary>Three cleaners on a two-maid order split it three ways.</summary>
        [Fact]
        public void ThreeCleanersOnATwoMaidOrder_SplitThreeWays()
        {
            var result = CleanerPayrollCalculator.Build(
                totalDuration: 540m, maidsCount: 2, hasCleanerService: false,
                orderHourlyRate: 20m, tips: 0m, assignments: Assignments(3));

            Assert.All(result.Lines, l => Assert.Equal(180m, l.BillableMinutes));
            Assert.Equal(180.00m, result.TotalSalary);
        }

        /// <summary>
        /// With nobody assigned there is no per-cleaner truth to sum, so the order keeps the
        /// MaidsCount estimate. Zeroing it would understate labour cost on every done-but-
        /// unassigned order and inflate reported net income.
        /// </summary>
        [Fact]
        public void NoCleanersAssigned_KeepsTheMaidsCountEstimate()
        {
            var result = CleanerPayrollCalculator.Build(
                totalDuration: 540m, maidsCount: 2, hasCleanerService: false,
                orderHourlyRate: 21m, tips: 0m,
                assignments: new List<CleanerPayrollCalculator.AssignmentInput>());

            Assert.Empty(result.Lines);
            Assert.Equal(
                OrderPricingCalculator.CalculateCleanerTotalSalary(540m, 2, false, 21m),
                result.TotalSalary);
            Assert.Equal(189.00m, result.TotalSalary);
        }

        // ===== Per-cleaner overrides drive the order total =====

        /// <summary>
        /// The owner's worked example: two cleaners at 5h each on $20 costs $200; raising ONE of
        /// them to $25/hr makes the order cost $225, not $200 and not $250. The order's single
        /// CleanerHourlyRate cannot express that, which is why the total is summed from the lines.
        /// </summary>
        [Fact]
        public void RaisingOneCleanersRate_RaisesTheOrderTotalByThatCleanerAlone()
        {
            var before = CleanerPayrollCalculator.Build(
                totalDuration: 600m, maidsCount: 2, hasCleanerService: false,
                orderHourlyRate: 20m, tips: 0m, assignments: Assignments(2));

            Assert.Equal(200.00m, before.TotalSalary);

            var after = CleanerPayrollCalculator.Build(
                totalDuration: 600m, maidsCount: 2, hasCleanerService: false,
                orderHourlyRate: 20m, tips: 0m,
                assignments: new List<CleanerPayrollCalculator.AssignmentInput>
                {
                    Assignment(1),
                    Assignment(2, rate: 25m)
                });

            Assert.Equal(100.00m, after.Lines[0].Salary);
            Assert.Equal(125.00m, after.Lines[1].Salary);
            Assert.Equal(225.00m, after.TotalSalary);
            Assert.False(after.Lines[0].RateOverridden);
            Assert.True(after.Lines[1].RateOverridden);
        }

        /// <summary>One cleaner leaving early is priced on their own hours, not the even split.</summary>
        [Fact]
        public void OverridingOneCleanersHours_OnlyMovesThatLine()
        {
            var result = CleanerPayrollCalculator.Build(
                totalDuration: 540m, maidsCount: 2, hasCleanerService: false,
                orderHourlyRate: 20m, tips: 0m,
                assignments: new List<CleanerPayrollCalculator.AssignmentInput>
                {
                    Assignment(1, minutes: 180m),
                    Assignment(2)
                });

            Assert.Equal(60.00m, result.Lines[0].Salary);   // 3h × $20
            Assert.Equal(90.00m, result.Lines[1].Salary);   // the 4h30 automatic split × $20
            Assert.Equal(150.00m, result.TotalSalary);
            Assert.True(result.Lines[0].HoursOverridden);
            Assert.False(result.Lines[1].HoursOverridden);
        }

        /// <summary>
        /// An override equal to the automatic figure is still an override — it must not silently
        /// start tracking the order again on the next re-price.
        /// </summary>
        [Fact]
        public void AnOverrideEqualToTheAutomaticFigure_IsStillRecordedAsAnOverride()
        {
            var result = CleanerPayrollCalculator.Build(
                totalDuration: 540m, maidsCount: 2, hasCleanerService: false,
                orderHourlyRate: 20m, tips: 0m,
                assignments: new List<CleanerPayrollCalculator.AssignmentInput>
                {
                    Assignment(1, rate: 20m, minutes: 270m),
                    Assignment(2)
                });

            Assert.True(result.Lines[0].RateOverridden);
            Assert.True(result.Lines[0].HoursOverridden);
            Assert.Equal(result.Lines[1].Salary, result.Lines[0].Salary);
        }

        // ===== Changing the ORDER's rate =====

        /// <summary>
        /// The order's rate is the DEFAULT: every assigned cleaner without their own rate moves
        /// with it, and the order's total follows. This is what the Outgoing Payments page's
        /// "Hourly rate for this order" control does.
        /// </summary>
        [Fact]
        public void ChangingTheOrderRate_MovesEveryCleanerWithoutTheirOwnRate()
        {
            var at21 = CleanerPayrollCalculator.Build(
                540m, 2, false, 21m, 0m, Assignments(2));
            var at25 = CleanerPayrollCalculator.Build(
                540m, 2, false, 25m, 0m, Assignments(2));

            Assert.Equal(189.00m, at21.TotalSalary);
            Assert.Equal(225.00m, at25.TotalSalary);
        }

        /// <summary>
        /// A cleaner carrying an explicit rate is NOT moved by a change to the order's default.
        /// Somebody set that figure on purpose, and changing the order rate is not an instruction
        /// to discard it — clearing the override is.
        /// </summary>
        [Fact]
        public void ChangingTheOrderRate_LeavesAnExplicitPerCleanerRateAlone()
        {
            var result = CleanerPayrollCalculator.Build(
                totalDuration: 540m, maidsCount: 2, hasCleanerService: false,
                orderHourlyRate: 25m, tips: 0m,
                assignments: new List<CleanerPayrollCalculator.AssignmentInput>
                {
                    Assignment(1),
                    Assignment(2, rate: 21m)
                });

            Assert.Equal(112.50m, result.Lines[0].Salary);  // 4h30 × $25 — followed the order
            Assert.Equal(94.50m, result.Lines[1].Salary);   // 4h30 × $21 — kept its own
            Assert.Equal(207.00m, result.TotalSalary);
        }

        /// <summary>
        /// The service pins the old rate onto already-PAID lines before moving the order, so this
        /// is the shape the calculator then sees: the paid line stays at what was handed over
        /// while the unpaid one moves. Raising the rate must never retroactively inflate the
        /// reported cost of work already settled at the old figure.
        /// </summary>
        [Fact]
        public void APaidLinePinnedAtTheOldRate_DoesNotMoveWhenTheOrderRateRises()
        {
            var result = CleanerPayrollCalculator.Build(
                totalDuration: 540m, maidsCount: 2, hasCleanerService: false,
                orderHourlyRate: 25m, tips: 0m,
                assignments: new List<CleanerPayrollCalculator.AssignmentInput>
                {
                    Assignment(1, rate: 21m),  // paid earlier at 21, pinned
                    Assignment(2)              // still unpaid, follows the order
                });

            Assert.Equal(94.50m, result.Lines[0].Salary);
            Assert.Equal(112.50m, result.Lines[1].Salary);
            Assert.Equal(207.00m, result.TotalSalary);
        }

        // ===== Tips =====

        /// <summary>Tip shares must re-add to the order's tips EXACTLY, including odd cents.</summary>
        [Theory]
        [InlineData(30.00, 2)]
        [InlineData(10.00, 3)]
        [InlineData(0.01, 2)]
        [InlineData(55.55, 4)]
        [InlineData(100.00, 7)]
        public void TipShares_AlwaysReAddToTheOrdersTips(decimal tips, int count)
        {
            var shares = CleanerPayrollCalculator.SplitTips(tips, count);

            Assert.Equal(count, shares.Count);
            Assert.Equal(tips, shares.Sum());
            // Nobody's share may be more than a cent away from anybody else's.
            Assert.True(shares.Max() - shares.Min() <= 0.01m);
        }

        [Fact]
        public void NoTips_GivesEverybodyZero()
        {
            Assert.All(CleanerPayrollCalculator.SplitTips(0m, 3), s => Assert.Equal(0m, s));
        }

        [Fact]
        public void TipsRideOnTopOfSalary_InThePayoutButNotTheOrdersLabourCost()
        {
            var result = CleanerPayrollCalculator.Build(
                totalDuration: 540m, maidsCount: 2, hasCleanerService: false,
                orderHourlyRate: 21m, tips: 31.00m, assignments: Assignments(2));

            Assert.Equal(15.50m, result.Lines[0].Tips);
            Assert.Equal(110.00m, result.Lines[0].Payout);       // 94.50 + 15.50
            Assert.Equal(189.00m, result.TotalSalary);            // tips are NOT labour cost
        }

        // ===== Cleaner-hours service types =====

        /// <summary>
        /// Cleaner-hours service types store TotalDuration as PER CLEANER already, so it must not
        /// be divided again — each cleaner is paid the stored duration in full.
        /// </summary>
        [Fact]
        public void CleanerHoursServiceType_DoesNotSplitTheDurationAgain()
        {
            var result = CleanerPayrollCalculator.Build(
                totalDuration: 240m, maidsCount: 2, hasCleanerService: true,
                orderHourlyRate: 20m, tips: 0m, assignments: Assignments(2));

            Assert.All(result.Lines, l => Assert.Equal(240m, l.BillableMinutes));
            Assert.Equal(160.00m, result.TotalSalary);
        }

        /// <summary>
        /// Inherited from CalculatePerCleanerBillableMinutes: the split rounds DOWN, so adding a
        /// cleaner can never raise the payout for identical work. The 456-minute case is the one
        /// that originally exposed it.
        /// </summary>
        [Fact]
        public void AddingACleaner_NeverRaisesTheTotalPayout()
        {
            var one = CleanerPayrollCalculator.Build(
                456m, 1, false, 21m, 0m, Assignments(1)).TotalSalary;
            var two = CleanerPayrollCalculator.Build(
                456m, 2, false, 21m, 0m, Assignments(2)).TotalSalary;

            Assert.Equal(157.50m, one);
            Assert.Equal(147.00m, two);
            Assert.True(two <= one);
        }
    }
}
