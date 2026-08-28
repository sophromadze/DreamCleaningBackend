using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Services
{
    /// <summary>
    /// Turns an order plus the cleaners ASSIGNED to it into what each of them is owed, and into
    /// the order's total salary cost.
    ///
    /// This is the single source for that split (2026-08). The Outgoing Payments page is the
    /// authority on cleaner pay, so <see cref="Order.CleanerTotalSalary"/> — which Statistics and
    /// Finances read directly as the company's labour cost — is the SUM of the per-cleaner lines
    /// whenever anybody is assigned. Two cleaners on the same job may be paid different rates or
    /// different hours, and the order's single CleanerHourlyRate cannot express that; it survives
    /// as the DEFAULT each line falls back to.
    ///
    /// Three rules that must hold:
    ///
    /// 1. **The work is split across <c>max(MaidsCount, assigned)</c> people, not across the
    ///    assignment list.** Order.TotalDuration is TOTAL cleaner-minutes; an 18-hour job staffed
    ///    for 3 is 6 hours each, and it stays 6 hours each when only 2 of those people exist in
    ///    the system — the third cleaner still worked their 6 hours, they are just not recorded.
    ///    Dividing by the assignment count instead produced 9 hours each, which never happened
    ///    and overpaid the two who were recorded (found in production, 2026-08). Taking the
    ///    LARGER of the two counts also keeps the arithmetic honest the other way round: three
    ///    people on a job priced for two split it three ways rather than being paid 1.5× the job.
    /// 2. **A staffing slot with nobody assigned is still a payout.** The shortfall is reported as
    ///    <see cref="Result.UnassignedCount"/> lines at the same hours and rate, and they count
    ///    toward the order's total. That is what keeps Order.CleanerTotalSalary equal to the
    ///    labour cost of the job (3 × 6h × $25 = $450) whether or not the paperwork is complete —
    ///    dropping them would understate cost on every under-assigned order and quietly inflate
    ///    reported net income.
    /// 3. Overrides are NULL by default and null means "track the order". An override that
    ///    happens to equal the automatic figure is NOT the same thing — it stays put when the
    ///    order is re-priced.
    /// </summary>
    public static class CleanerPayrollCalculator
    {
        /// <summary>One assignment's stored payroll state — the two nullable overrides.</summary>
        public class AssignmentInput
        {
            public int OrderCleanerId { get; set; }
            public int CleanerId { get; set; }
            public decimal? SalaryHourlyRate { get; set; }
            public decimal? SalaryBillableMinutes { get; set; }
        }

        /// <summary>What one cleaner is owed for one order.</summary>
        public class CleanerLine
        {
            public int OrderCleanerId { get; set; }
            public int CleanerId { get; set; }

            /// <summary>Minutes this cleaner is paid for — the override, or the automatic split.</summary>
            public decimal BillableMinutes { get; set; }
            public bool HoursOverridden { get; set; }

            public decimal HourlyRate { get; set; }
            public bool RateOverridden { get; set; }

            /// <summary>BillableMinutes / 60 × HourlyRate, rounded to cents.</summary>
            public decimal Salary { get; set; }

            /// <summary>This cleaner's share of Order.Tips. Shares re-add to the order's tips exactly.</summary>
            public decimal Tips { get; set; }

            /// <summary>Salary + Tips — what actually gets handed over.</summary>
            public decimal Payout => OrderPricingCalculator.Round2(Salary + Tips);
        }

        public class Result
        {
            /// <summary>One line per ASSIGNED cleaner — the people who can actually be paid.</summary>
            public List<CleanerLine> Lines { get; set; } = new();

            /// <summary>
            /// What goes on Order.CleanerTotalSalary: every line PLUS every unassigned staffing
            /// slot, so it equals the labour cost of the job whether or not the paperwork is
            /// complete. Tips are NOT included — they are the customer's money passing through
            /// and are reported separately everywhere.
            /// </summary>
            public decimal TotalSalary { get; set; }

            /// <summary>The even split every line falls back to, before any override.</summary>
            public decimal AutomaticBillableMinutes { get; set; }

            /// <summary>How many assignment rows the order has.</summary>
            public int AssignedCount { get; set; }

            /// <summary>
            /// How many people the work was actually split across — <c>max(MaidsCount, assigned)</c>.
            /// This, not the assignment count, is what "· 6h each cleaner" is derived from.
            /// </summary>
            public int SplitCount { get; set; }

            /// <summary>
            /// Staffing slots with nobody assigned (<c>SplitCount − AssignedCount</c>). Somebody
            /// worked those hours; they are simply not in the system, so their pay is reported
            /// rather than payable.
            /// </summary>
            public int UnassignedCount { get; set; }

            /// <summary>What ONE unassigned slot is owed in wages.</summary>
            public decimal UnassignedSalaryEach { get; set; }

            /// <summary>Tips belonging to the unassigned slots, in total.</summary>
            public decimal UnassignedTips { get; set; }
        }

        /// <summary>
        /// Builds every line and the order total. <paramref name="assignments"/> must already be
        /// in a stable order (assignment id) — the leftover tip cents land on the first lines, so
        /// an unstable order would move a cent between cleaners on every reload.
        /// </summary>
        public static Result Build(
            decimal totalDuration,
            int maidsCount,
            bool hasCleanerService,
            decimal orderHourlyRate,
            decimal tips,
            IReadOnlyList<AssignmentInput> assignments)
        {
            var list = assignments ?? (IReadOnlyList<AssignmentInput>)Array.Empty<AssignmentInput>();
            var assignedCount = list.Count;

            var splitCount = ResolveSplitCount(maidsCount, assignedCount);

            var automaticMinutes = OrderPricingCalculator.CalculatePerCleanerBillableMinutes(
                totalDuration, splitCount, hasCleanerService);

            // Tips are shared by everyone who worked, so the shares are cut over the SPLIT count.
            // Anything past the assigned lines belongs to the unassigned slots.
            var tipShares = SplitTips(tips, splitCount);

            var lines = new List<CleanerLine>(assignedCount);
            for (var i = 0; i < assignedCount; i++)
            {
                var a = list[i];
                var minutes = a.SalaryBillableMinutes ?? automaticMinutes;
                var rate = a.SalaryHourlyRate ?? orderHourlyRate;

                lines.Add(new CleanerLine
                {
                    OrderCleanerId = a.OrderCleanerId,
                    CleanerId = a.CleanerId,
                    BillableMinutes = minutes,
                    HoursOverridden = a.SalaryBillableMinutes.HasValue,
                    HourlyRate = rate,
                    RateOverridden = a.SalaryHourlyRate.HasValue,
                    Salary = OrderPricingCalculator.Round2(minutes / 60m * rate),
                    Tips = tipShares[i]
                });
            }

            var unassignedCount = Math.Max(0, splitCount - assignedCount);
            var unassignedSalaryEach = OrderPricingCalculator.Round2(automaticMinutes / 60m * orderHourlyRate);
            var unassignedTips = OrderPricingCalculator.Round2(tipShares.Skip(assignedCount).Sum());

            return new Result
            {
                Lines = lines,
                AssignedCount = assignedCount,
                SplitCount = splitCount,
                UnassignedCount = unassignedCount,
                UnassignedSalaryEach = unassignedCount > 0 ? unassignedSalaryEach : 0m,
                UnassignedTips = unassignedTips,
                AutomaticBillableMinutes = automaticMinutes,
                // The unassigned slots count toward the order's labour cost — somebody worked
                // those hours. Dropping them would understate every under-assigned order.
                TotalSalary = OrderPricingCalculator.Round2(
                    lines.Sum(l => l.Salary) + unassignedCount * unassignedSalaryEach)
            };
        }

        /// <summary>
        /// How many people the work is spread across: the count the job was STAFFED for,
        /// widened if more people turned out to be assigned than it was priced for. See rule 1
        /// in the class comment for why it is not simply the assignment count.
        ///
        /// SINGLE source, mirrored by <c>resolveCleanerSplitCount</c> in
        /// order-pricing.calculator.ts. Every surface that divides a duration or a salary
        /// across cleaners must use it — the admin orders panel used bare MaidsCount, so an
        /// order priced for 2 but staffed with 3 showed "6h per cleaner" next to an Outgoing
        /// Payments page paying each of them for 4h.
        /// </summary>
        public static int ResolveSplitCount(int maidsCount, int assignedCount) =>
            Math.Max(1, Math.Max(maidsCount, assignedCount));

        /// <summary>
        /// Does this order carry a cleaner-hours service (cleaners × hours picked by the
        /// customer)? SINGLE in-memory implementation of that test — it decides whether
        /// Order.TotalDuration is already per-cleaner or has to be divided, so two callers
        /// answering it differently is a 2× error in somebody's hours.
        ///
        /// It reads <c>ServiceRelationType</c>, which is what the pricing calculator, the
        /// scheduling conflict math, OrderDtoMapper and the Outgoing Payments page all read.
        /// EmailService used to ask a DIFFERENT column here — <c>ServiceKey</c> containing
        /// "cleaner" — which happens to agree on the seeded row (key "cleaners", relation
        /// "cleaner") and therefore never showed up in dev, but the two are independent
        /// admin-editable fields and production catalogue keys are known to drift from the
        /// seed. Whenever they disagreed, the cleaner assignment email would divide a duration
        /// the payroll page did not (or the reverse) and tell the cleaner half or double the
        /// hours they were actually being paid for.
        ///
        /// EF cannot translate this into SQL — call sites that need it inside a query keep
        /// their own <c>ServiceRelationType == "cleaner"</c> predicate expression.
        /// Requires OrderServices → Service to be loaded.
        /// </summary>
        public static bool HasCleanerHoursService(Order order) =>
            order.OrderServices?.Any(os => os.Service?.ServiceRelationType == "cleaner") ?? false;

        /// <summary>
        /// The billable minutes ONE named cleaner is owed on this order — their per-cleaner
        /// override when an admin set one, otherwise the automatic split.
        ///
        /// This is what a cleaner must be TOLD in their assignment email/SMS, because it is
        /// exactly what the Outgoing Payments page will pay them. Computing the hours straight
        /// from <see cref="OrderPricingCalculator.CalculatePerCleanerBillableMinutes"/> at the
        /// notification site instead was wrong in two reachable ways (2026-08): it divided by
        /// <c>MaidsCount</c> rather than the <c>max(MaidsCount, assigned)</c> the payroll
        /// splits by, so a third cleaner assigned to a job priced for two was told half the
        /// job when they would be paid a third of it; and it could not see a per-cleaner hours
        /// override, so a resent mail contradicted the figure the owner had already signed off.
        ///
        /// Requires OrderServices → Service and OrderCleaners to be loaded. A cleaner with no
        /// assignment row (or a null id) gets the automatic split, which is the right answer
        /// for a notification going out before the row exists.
        /// </summary>
        public static decimal ResolveBillableMinutesForCleaner(Order order, int? cleanerId)
        {
            var payroll = Build(order, HasCleanerHoursService(order), order.OrderCleaners);

            var line = cleanerId.HasValue
                ? payroll.Lines.FirstOrDefault(l => l.CleanerId == cleanerId.Value)
                : null;

            return line?.BillableMinutes ?? payroll.AutomaticBillableMinutes;
        }

        /// <summary>
        /// Convenience overload taking the order and its loaded OrderCleaners.
        /// <paramref name="hasCleanerService"/> must be resolved by the caller — it needs
        /// OrderServices → Service, which is not always loaded.
        /// </summary>
        public static Result Build(Order order, bool hasCleanerService, IEnumerable<OrderCleaner>? assignments)
        {
            var inputs = (assignments ?? Enumerable.Empty<OrderCleaner>())
                .OrderBy(oc => oc.Id)
                .Select(oc => new AssignmentInput
                {
                    OrderCleanerId = oc.Id,
                    CleanerId = oc.CleanerId,
                    SalaryHourlyRate = oc.SalaryHourlyRate,
                    SalaryBillableMinutes = oc.SalaryBillableMinutes
                })
                .ToList();

            return Build(order.TotalDuration, order.MaidsCount, hasCleanerService,
                order.CleanerHourlyRate, order.Tips, inputs);
        }

        /// <summary>
        /// Writes the payroll-aware total onto the order and returns it. EVERY place that used to
        /// call OrderPricingCalculator.CalculateCleanerTotalSalary to refresh
        /// Order.CleanerTotalSalary goes through here instead, so a re-price (order edit, duration
        /// change, maids change) can never wipe out per-cleaner figures an owner already signed
        /// off on.
        ///
        /// Pass the assignments the caller actually loaded. Passing null/empty when the order DOES
        /// have cleaners silently reverts it to the MaidsCount estimate, which is exactly the bug
        /// this method exists to prevent — load OrderCleaners at the call site.
        /// </summary>
        public static decimal ApplyOrderTotalSalary(Order order, bool hasCleanerService, IEnumerable<OrderCleaner>? assignments)
        {
            order.CleanerTotalSalary = Build(order, hasCleanerService, assignments).TotalSalary;
            return order.CleanerTotalSalary;
        }

        /// <summary>
        /// Splits Order.Tips evenly across the assigned cleaners, in whole cents, with the
        /// leftover cents handed out one at a time from the first line. The shares therefore
        /// re-add to the order's tips EXACTLY — a payout sheet that does not add up is the whole
        /// reason this page exists.
        ///
        /// (EmailService splits tips by MaidsCount for the cleaner's own assignment email. That
        /// answers a different question — roughly what to expect, before the job — and is left
        /// alone; this is the figure that actually gets paid.)
        /// </summary>
        public static List<decimal> SplitTips(decimal tips, int count)
        {
            if (count <= 0) return new List<decimal>();

            var total = OrderPricingCalculator.Round2(tips);
            if (total <= 0) return Enumerable.Repeat(0m, count).ToList();

            var totalCents = (int)Math.Round(total * 100m, MidpointRounding.AwayFromZero);
            var baseCents = totalCents / count;
            var leftover = totalCents - baseCents * count;

            var shares = new List<decimal>(count);
            for (var i = 0; i < count; i++)
                shares.Add((baseCents + (i < leftover ? 1 : 0)) / 100m);

            return shares;
        }
    }
}
