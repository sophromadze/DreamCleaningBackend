using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Services;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// The staffing rules the admin Orders panel leans on (2026-08-31):
    ///
    /// 1. Assigning cleaners RAISES Order.MaidsCount to match, and never lowers it.
    /// 2. The staffing warnings an admin reads on an order are built once, so the Orders panel
    ///    and Outgoing Payments cannot describe the same job differently.
    /// 3. Unassigning a cleaner only notifies them if they were told about the job to begin with.
    /// </summary>
    public class CleanerStaffingRulesTests
    {
        // ===== 1. MaidsCount follows the assignment, upwards only =====

        [Theory]
        [InlineData(1, 2, 2)] // the case this was built for: default 1, two cleaners sent out
        [InlineData(1, 3, 3)]
        [InlineData(1, 4, 4)]
        [InlineData(2, 4, 4)]
        public void AssigningMoreCleanersThanTheOrderWasPricedFor_RaisesTheCount(
            int current, int assigned, int expected)
        {
            Assert.Equal(expected, CleanerService.ResolveMaidsCountAfterAssignment(
                current, assigned, hasExplicitCleanerCount: false));
        }

        /// <summary>
        /// An order priced for 3 and staffed with 2 keeps its 3. The third cleaner worked — they
        /// are just not on file, which is exactly what the payroll's unassigned slots pay for —
        /// and dropping the count to 2 would quietly cut the labour cost Statistics and Finances
        /// report for that job.
        /// </summary>
        [Theory]
        [InlineData(3, 2)]
        [InlineData(3, 1)]
        [InlineData(4, 0)] // every cleaner unassigned again
        [InlineData(2, 2)] // already in step
        public void FewerCleanersAssignedThanPricedFor_LeavesTheCountAlone(int current, int assigned)
        {
            Assert.Equal(current, CleanerService.ResolveMaidsCountAfterAssignment(
                current, assigned, hasExplicitCleanerCount: false));
        }

        /// <summary>
        /// Cleaner+hours service types and Custom ("Pre-Arranged") orders are opted out: their
        /// TotalDuration was DERIVED from the cleaner count, so moving the count on its own would
        /// leave the order's own duration unexplainable. Over-assigning one of those is reported
        /// by the warning below instead.
        /// </summary>
        [Theory]
        [InlineData(1, 3)]
        [InlineData(2, 5)]
        public void AnExplicitlyPricedCleanerCount_IsNeverMovedByAnAssignment(int current, int assigned)
        {
            Assert.Equal(current, CleanerService.ResolveMaidsCountAfterAssignment(
                current, assigned, hasExplicitCleanerCount: true));
        }

        // ===== 2. The shared staffing warnings =====

        private static OrderStaffingWarnings.Input Healthy() => new()
        {
            ServiceTypeName = "Deep Cleaning",
            ExpectedHourlyRate = 21m,
            AssignedHourlyRates = new List<decimal> { 21m, 21m },
            SplitCount = 2,
            UnassignedCount = 0,
            TotalSalary = 210m,
            UnassignedPayoutEach = 0m,
            MaidsCount = 2,
            TotalDuration = 600m,
            IsPaidByCustomer = true
        };

        [Fact]
        public void AWellFormedOrder_WarnsAboutNothing()
        {
            Assert.Empty(OrderStaffingWarnings.Build(Healthy()));
        }

        /// <summary>
        /// The rate warning is per DISTINCT rate: with mixed rates on one job, naming each one is
        /// what tells the reader whether the odd one out was deliberate.
        /// </summary>
        [Fact]
        public void MixedOffDefaultRates_AreEachNamedOnce()
        {
            var input = Healthy();
            input.AssignedHourlyRates = new List<decimal> { 21m, 25m, 25m, 20m };
            // Staffed for the four people who are on it, so the rate is the ONLY thing wrong.
            input.SplitCount = 4;
            input.MaidsCount = 4;

            var warning = Assert.Single(OrderStaffingWarnings.Build(input));

            Assert.Contains("$20/hr", warning);
            Assert.Contains("$25/hr", warning);
            Assert.Contains("$21/hr", warning);       // the expected default it is measured against
            Assert.Contains("Deep Cleaning", warning);
        }

        [Fact]
        public void NobodyOnFile_SaysHowMuchIsOwedRatherThanNaming()
        {
            var input = Healthy();
            input.AssignedHourlyRates = new List<decimal>();
            input.SplitCount = 3;
            input.UnassignedCount = 3;
            input.TotalSalary = 450m;
            input.UnassignedPayoutEach = 150m;
            input.MaidsCount = 3;

            var warning = Assert.Single(OrderStaffingWarnings.Build(input));

            Assert.Contains("Nobody on this order is in the system", warning);
            Assert.Contains("3 cleaner(s)", warning);
            Assert.Contains("$450.00", warning);
        }

        [Fact]
        public void PartlyOnFile_NamesTheShortfallAndWhatEachMissingSlotIsOwed()
        {
            var input = Healthy();
            input.AssignedHourlyRates = new List<decimal> { 21m };
            input.SplitCount = 3;
            input.UnassignedCount = 2;
            input.MaidsCount = 3;
            input.UnassignedPayoutEach = 126.50m;

            var warning = Assert.Single(OrderStaffingWarnings.Build(input));

            Assert.Contains("1 of 3 cleaner(s)", warning);
            Assert.Contains("$126.50", warning);
        }

        /// <summary>
        /// Only reachable now on the two kinds of order whose cleaner count deliberately does not
        /// follow the assignments — a cleaner+hours type, or a Custom order.
        /// </summary>
        [Fact]
        public void MoreCleanersThanTheOrderWasPricedFor_SaysEveryShareGotSmaller()
        {
            var input = Healthy();
            input.AssignedHourlyRates = new List<decimal> { 21m, 21m, 21m };
            input.SplitCount = 3;
            input.MaidsCount = 2;

            var warning = Assert.Single(OrderStaffingWarnings.Build(input));

            Assert.Contains("3 cleaner(s) are assigned", warning);
            Assert.Contains("priced for 2", warning);
        }

        [Fact]
        public void NoDurationRecorded_SaysEverybodysPayCalculatesToZero()
        {
            var input = Healthy();
            input.TotalDuration = 0m;

            Assert.Contains(OrderStaffingWarnings.Build(input), w => w.Contains("no duration recorded"));
        }

        [Fact]
        public void AnUnpaidCustomer_IsCalledOut()
        {
            var input = Healthy();
            input.IsPaidByCustomer = false;

            Assert.Contains(OrderStaffingWarnings.Build(input), w => w.Contains("has not paid"));
        }

        /// <summary>
        /// Several problems at once are reported as several lines, not collapsed into one — each
        /// is a separate thing to go and fix.
        /// </summary>
        [Fact]
        public void SeveralProblemsAtOnce_AreReportedSeparately()
        {
            var input = Healthy();
            input.AssignedHourlyRates = new List<decimal> { 25m };
            input.SplitCount = 2;
            input.UnassignedCount = 1;
            input.UnassignedPayoutEach = 105m;
            input.TotalDuration = 0m;
            input.IsPaidByCustomer = false;

            Assert.Equal(4, OrderStaffingWarnings.Build(input).Count);
        }

        // ===== 3. The facts entry point the Orders tab's bulk endpoint uses =====

        /// <summary>
        /// `BuildFromFacts` runs the payroll and the expected-rate lookup itself, so a caller with
        /// raw order columns and one with a payroll result reach the same text. Doing those two
        /// steps by hand at a call site is exactly how one screen ends up measuring against a
        /// different expected rate than the other.
        /// </summary>
        [Fact]
        public void FactsAndPayrollInput_ProduceTheSameText()
        {
            // 600 total minutes, 2 cleaners, deep cleaning → 5h each at the $21 deep default.
            var facts = new OrderStaffingWarnings.OrderFacts
            {
                ServiceTypeName = "Deep Cleaning",
                HasCleanerHoursService = false,
                HasDeepCleaningExtra = true,
                TotalDuration = 600m,
                MaidsCount = 2,
                CleanerHourlyRate = 25m,   // off the $21 default — the one thing wrong
                Tips = 0m,
                IsPaidByCustomer = true,
                Assignments = new List<CleanerPayrollCalculator.AssignmentInput>
                {
                    new() { OrderCleanerId = 1, CleanerId = 1 },
                    new() { OrderCleanerId = 2, CleanerId = 2 }
                }
            };

            var fromFacts = Assert.Single(OrderStaffingWarnings.BuildFromFacts(facts));

            var equivalent = Healthy();
            equivalent.AssignedHourlyRates = new List<decimal> { 25m, 25m };
            var fromInput = Assert.Single(OrderStaffingWarnings.Build(equivalent));

            Assert.Equal(fromInput, fromFacts);
        }

        /// <summary>
        /// The expected rate is resolved from the DISPLAY service-type name, so a Custom
        /// ("Pre-Arranged") order labelled "Deep" is measured against the deep rate even though it
        /// carries no deep extra — the same rule the payouts page applies.
        /// </summary>
        [Fact]
        public void ACustomOrderLabelledDeep_IsMeasuredAgainstTheDeepRate()
        {
            var facts = new OrderStaffingWarnings.OrderFacts
            {
                ServiceTypeName = "Deep Cleaning",
                HasDeepCleaningExtra = false,      // custom orders never carry the extra
                TotalDuration = 600m,
                MaidsCount = 1,
                CleanerHourlyRate = 21m,
                IsPaidByCustomer = true,
                Assignments = new List<CleanerPayrollCalculator.AssignmentInput>
                {
                    new() { OrderCleanerId = 1, CleanerId = 1 }
                }
            };

            // $21 IS the deep default, so nothing is wrong. Reading the fee alone would have
            // dropped this to the $20 base and warned about a correct order.
            Assert.Empty(OrderStaffingWarnings.BuildFromFacts(facts));
        }

        /// <summary>
        /// An unstaffed order still owes wages, and BuildFromFacts has to derive the slot count
        /// and what each slot is owed from the payroll rather than being told.
        /// </summary>
        [Fact]
        public void FactsWithNobodyAssigned_ReportTheUnstaffedSlots()
        {
            var facts = new OrderStaffingWarnings.OrderFacts
            {
                ServiceTypeName = "Regular Cleaning",
                TotalDuration = 720m,
                MaidsCount = 2,
                CleanerHourlyRate = 20m,
                IsPaidByCustomer = true,
                Assignments = new List<CleanerPayrollCalculator.AssignmentInput>()
            };

            var warning = Assert.Single(OrderStaffingWarnings.BuildFromFacts(facts));

            Assert.Contains("Nobody on this order is in the system", warning);
            Assert.Contains("2 cleaner(s)", warning);
            // 720 min over 2 = 6h each at $20 = $120 a head, $240 owed.
            Assert.Contains("$240.00", warning);
        }

        // ===== 3. A cleaner who was never told about the job is not told it was taken away =====

        /// <summary>
        /// The case this was built for. Assigning does not notify — the assignment mail is a
        /// separate admin action — so an admin who staffs an order, changes their mind and
        /// unassigns before sending anything has told the cleaner nothing. A removal email there
        /// would be the first and only thing that cleaner ever heard about the order.
        /// </summary>
        [Fact]
        public void RemovingACleanerWhoWasNeverSentTheAssignmentMail_NotifiesNobody()
        {
            Assert.False(CleanerService.ShouldNotifyOfRemoval(null, "cleaner@example.com"));
        }

        /// <summary>
        /// Once the assignment mail has gone out the cleaner is expecting the job, so being taken
        /// off it is news they need.
        /// </summary>
        [Fact]
        public void RemovingACleanerWhoWasSentTheAssignmentMail_StillNotifiesThem()
        {
            Assert.True(CleanerService.ShouldNotifyOfRemoval(
                new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc), "cleaner@example.com"));
        }

        /// <summary>
        /// The removal notice is email-only, so a cleaner with no address has nothing to receive
        /// even when they were notified of the assignment (by SMS, which is what a cleaner with a
        /// phone and no email gets).
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void ACleanerWithNoEmailAddress_IsNeverEmailedAboutTheRemoval(string? email)
        {
            Assert.False(CleanerService.ShouldNotifyOfRemoval(
                new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc), email));
        }
    }
}
