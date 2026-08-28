using DreamCleaningBackend.Models;
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

        // ===== The split follows the count the job was STAFFED for =====

        /// <summary>
        /// The production case that exposed this (2026-08): an 18-hour Heavy job staffed for 3
        /// cleaners, only 2 of whom exist in the system. Each of them worked SIX hours, not nine.
        /// The third cleaner worked their six hours too — they are simply not on file — so the
        /// shortfall is reported as an unassigned slot and still counts toward the order's cost.
        ///
        /// Dividing by the assignment count instead produced 9h each and paid $225 a head against
        /// an order whose stored labour cost was $450.
        /// </summary>
        [Fact]
        public void AnUnderAssignedJob_StillPaysEachCleanerTheirStaffedShare()
        {
            var result = CleanerPayrollCalculator.Build(
                totalDuration: 1080m, maidsCount: 3, hasCleanerService: false,
                orderHourlyRate: 25m, tips: 0m, assignments: Assignments(2));

            Assert.All(result.Lines, l => Assert.Equal(360m, l.BillableMinutes));   // 6h, not 9h
            Assert.All(result.Lines, l => Assert.Equal(150.00m, l.Salary));

            Assert.Equal(3, result.SplitCount);
            Assert.Equal(1, result.UnassignedCount);
            Assert.Equal(150.00m, result.UnassignedSalaryEach);

            // 2 × $150 paid out + $150 owed to somebody not on file = the order's real cost.
            Assert.Equal(450.00m, result.TotalSalary);
        }

        /// <summary>
        /// An order priced for 2 with only one cleaner assigned pays that cleaner HALF the job —
        /// their own share — and reports the other half as an unassigned slot.
        /// </summary>
        [Fact]
        public void OneCleanerOnATwoMaidOrder_IsPaidTheirOwnShareOnly()
        {
            var result = CleanerPayrollCalculator.Build(
                totalDuration: 540m, maidsCount: 2, hasCleanerService: false,
                orderHourlyRate: 21m, tips: 0m, assignments: Assignments(1));

            Assert.Equal(270m, Assert.Single(result.Lines).BillableMinutes);
            Assert.Equal(94.50m, result.Lines[0].Salary);
            Assert.Equal(1, result.UnassignedCount);
            Assert.Equal(94.50m, result.UnassignedSalaryEach);
            Assert.Equal(189.00m, result.TotalSalary);
        }

        /// <summary>
        /// The other direction: MORE people assigned than the job was priced for. The split widens
        /// to the assignment count so the shares still add up to the job, rather than paying three
        /// people a two-way share each.
        /// </summary>
        [Fact]
        public void ThreeCleanersOnATwoMaidOrder_SplitThreeWays()
        {
            var result = CleanerPayrollCalculator.Build(
                totalDuration: 540m, maidsCount: 2, hasCleanerService: false,
                orderHourlyRate: 20m, tips: 0m, assignments: Assignments(3));

            Assert.All(result.Lines, l => Assert.Equal(180m, l.BillableMinutes));
            Assert.Equal(3, result.SplitCount);
            Assert.Equal(0, result.UnassignedCount);
            Assert.Equal(180.00m, result.TotalSalary);
        }

        /// <summary>
        /// With nobody assigned the whole job is unassigned slots — reported, counted, unpayable.
        /// The total still equals the MaidsCount estimate, because zeroing it would understate
        /// labour cost on every done-but-unassigned order and inflate reported net income.
        /// </summary>
        [Fact]
        public void NoCleanersAssigned_ReportsEveryStaffingSlotAndKeepsTheCost()
        {
            var result = CleanerPayrollCalculator.Build(
                totalDuration: 540m, maidsCount: 2, hasCleanerService: false,
                orderHourlyRate: 21m, tips: 0m,
                assignments: new List<CleanerPayrollCalculator.AssignmentInput>());

            Assert.Empty(result.Lines);
            Assert.Equal(2, result.UnassignedCount);
            Assert.Equal(94.50m, result.UnassignedSalaryEach);
            Assert.Equal(
                OrderPricingCalculator.CalculateCleanerTotalSalary(540m, 2, false, 21m),
                result.TotalSalary);
            Assert.Equal(189.00m, result.TotalSalary);
        }

        /// <summary>
        /// Tips are cut over everyone who worked, so an unassigned slot holds a share too — and
        /// the assigned shares plus the unassigned ones still re-add to the order's tips exactly.
        /// </summary>
        [Fact]
        public void TipsAreSharedAcrossTheStaffedCount_NotJustTheAssignedOne()
        {
            var result = CleanerPayrollCalculator.Build(
                totalDuration: 1080m, maidsCount: 3, hasCleanerService: false,
                orderHourlyRate: 25m, tips: 30m, assignments: Assignments(2));

            Assert.All(result.Lines, l => Assert.Equal(10.00m, l.Tips));
            Assert.Equal(10.00m, result.UnassignedTips);
            Assert.Equal(30.00m, result.Lines.Sum(l => l.Tips) + result.UnassignedTips);
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

        /// <summary>
        /// The hours on a payout line are the DISPLAYED total divided by the split count, so
        /// the panel's "12h total" and its "6h each" are the same arithmetic (2026-08).
        ///
        /// The order that exposed it stored 710 minutes and was staffed for 2: the page showed
        /// "12h total" beside "5h 30m per cleaner" and paid $231.00, and nobody could reconcile
        /// the two numbers on screen. Dividing the rounded total pays what 12h at $21 actually
        /// costs.
        /// </summary>
        [Fact]
        public void PayoutLines_SplitTheDisplayedTotal_NotTheRawMinutes()
        {
            var result = CleanerPayrollCalculator.Build(
                totalDuration: 710m, maidsCount: 2, hasCleanerService: false,
                orderHourlyRate: 21m, tips: 0m, assignments: Assignments(2));

            Assert.Equal(360m, result.AutomaticBillableMinutes);
            Assert.All(result.Lines, l => Assert.Equal(126.00m, l.Salary));
            Assert.Equal(252.00m, result.TotalSalary);
        }

        /// <summary>
        /// An unassigned staffing slot is paid the same share as a named cleaner, so the
        /// rounded-total split has to reach it too — otherwise an under-staffed order's
        /// reported labour cost would disagree with the identical fully-staffed one.
        /// </summary>
        [Fact]
        public void UnassignedSlots_UseTheSameDisplayedTotalSplit()
        {
            var result = CleanerPayrollCalculator.Build(
                totalDuration: 710m, maidsCount: 2, hasCleanerService: false,
                orderHourlyRate: 21m, tips: 0m, assignments: Assignments(1));

            Assert.Equal(1, result.UnassignedCount);
            Assert.Equal(126.00m, result.UnassignedSalaryEach);
            Assert.Equal(252.00m, result.TotalSalary);
        }

        // ===== The hours a cleaner is TOLD must be the hours they are PAID (2026-08) =====

        private static Order OrderWith(
            decimal totalDuration, int maidsCount, string? relationType = null,
            string serviceKey = "bedrooms", params OrderCleaner[] cleaners)
        {
            var order = new Order
            {
                Id = 1,
                TotalDuration = totalDuration,
                MaidsCount = maidsCount,
                CleanerHourlyRate = 21m,
                Tips = 0m
            };

            order.OrderServices.Add(new DreamCleaningBackend.Models.OrderService
            {
                Service = new Service { Name = "S", ServiceKey = serviceKey, ServiceRelationType = relationType }
            });

            foreach (var c in cleaners) order.OrderCleaners.Add(c);
            return order;
        }

        private static OrderCleaner Assigned(int id, decimal? minutes = null) =>
            new() { Id = id, CleanerId = id, SalaryBillableMinutes = minutes };

        /// <summary>
        /// The assignment mail/SMS quotes the payroll line, not a fresh split off MaidsCount.
        ///
        /// The work is spread over max(MaidsCount, assigned), so a third cleaner turning up on
        /// a job priced for two is paid a THIRD of it. Re-deriving the hours at the
        /// notification site divided by MaidsCount and told all three of them a half — 6h each
        /// against the 4h each the Outgoing Payments page would pay.
        /// </summary>
        [Fact]
        public void AssignmentNotification_QuotesTheHoursTheCleanerIsActuallyPaidFor()
        {
            var order = OrderWith(720m, 2, cleaners: new[] { Assigned(1), Assigned(2), Assigned(3) });

            var payroll = CleanerPayrollCalculator.Build(
                order, CleanerPayrollCalculator.HasCleanerHoursService(order), order.OrderCleaners);

            Assert.Equal(3, payroll.SplitCount);
            Assert.Equal(240m, payroll.AutomaticBillableMinutes);

            // What the mail now says — the same 4h, for every one of the three.
            foreach (var id in new[] { 1, 2, 3 })
                Assert.Equal(240m, CleanerPayrollCalculator.ResolveBillableMinutesForCleaner(order, id));
        }

        /// <summary>
        /// A per-cleaner hours override is a figure the owner signed off on. A resent
        /// assignment mail must repeat it, not the automatic split it replaced.
        /// </summary>
        [Fact]
        public void AssignmentNotification_HonoursAPerCleanerHoursOverride()
        {
            var order = OrderWith(720m, 2, cleaners: new[] { Assigned(1, minutes: 300m), Assigned(2) });

            Assert.Equal(300m, CleanerPayrollCalculator.ResolveBillableMinutesForCleaner(order, 1));
            Assert.Equal(360m, CleanerPayrollCalculator.ResolveBillableMinutesForCleaner(order, 2));

            // Not yet assigned (notification racing the row): the automatic split is right.
            Assert.Equal(360m, CleanerPayrollCalculator.ResolveBillableMinutesForCleaner(order, null));
        }

        /// <summary>
        /// "Is TotalDuration already per-cleaner?" is read off ServiceRelationType, the column
        /// the pricing calculator and the payments page use. EmailService used to ask whether
        /// ServiceKey CONTAINED "cleaner" instead — the two agree on the seeded row (key
        /// "cleaners", relation "cleaner") and so never diverged in dev, but they are
        /// independent admin-editable fields. Whenever they disagreed the mail divided a
        /// duration the payroll did not, quoting half or double the real hours.
        /// </summary>
        [Fact]
        public void CleanerHoursServiceIsReadFromTheRelationType_NotTheServiceKey()
        {
            // Relation says cleaner-hours; the key does not mention cleaners at all.
            var byRelation = OrderWith(360m, 2, relationType: "cleaner", serviceKey: "hourly_team");
            Assert.True(CleanerPayrollCalculator.HasCleanerHoursService(byRelation));
            Assert.Equal(360m, CleanerPayrollCalculator.ResolveBillableMinutesForCleaner(byRelation, null));

            // The key mentions cleaners; the relation says this is an ordinary priced service.
            var byKeyOnly = OrderWith(360m, 2, relationType: null, serviceKey: "extra_cleaners");
            Assert.False(CleanerPayrollCalculator.HasCleanerHoursService(byKeyOnly));
            Assert.Equal(180m, CleanerPayrollCalculator.ResolveBillableMinutesForCleaner(byKeyOnly, null));
        }
    }
}
