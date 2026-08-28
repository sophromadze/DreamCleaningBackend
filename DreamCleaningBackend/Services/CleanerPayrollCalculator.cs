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
    /// 1. The split is over the ASSIGNED cleaner count, not Order.MaidsCount. MaidsCount is what
    ///    the job was priced for; the assignment list is who actually did it, and the payments
    ///    page has to add up to what really leaves the company. When the two disagree the page
    ///    warns rather than silently picking one.
    /// 2. With NOBODY assigned there is no per-cleaner truth to sum, so the total falls back to
    ///    the historical MaidsCount estimate. Returning 0 there would understate labour cost on
    ///    every done-but-unassigned order and quietly inflate reported net income.
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
            public List<CleanerLine> Lines { get; set; } = new();

            /// <summary>
            /// What goes on Order.CleanerTotalSalary: the sum of the lines when anyone is
            /// assigned, otherwise the MaidsCount estimate. Tips are NOT included — they are the
            /// customer's money passing through and are reported separately everywhere.
            /// </summary>
            public decimal TotalSalary { get; set; }

            /// <summary>The even split every line falls back to, before any override.</summary>
            public decimal AutomaticBillableMinutes { get; set; }

            /// <summary>How many cleaners the split was actually divided by.</summary>
            public int AssignedCount { get; set; }
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

            // Divide by who is actually assigned; with nobody assigned, fall back to the count the
            // job was priced for so the automatic figure still means something.
            var splitCount = assignedCount > 0 ? assignedCount : Math.Max(1, maidsCount);
            var automaticMinutes = OrderPricingCalculator.CalculatePerCleanerBillableMinutes(
                totalDuration, splitCount, hasCleanerService);

            var tipShares = SplitTips(tips, assignedCount);

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

            return new Result
            {
                Lines = lines,
                AssignedCount = assignedCount,
                AutomaticBillableMinutes = automaticMinutes,
                TotalSalary = assignedCount > 0
                    ? OrderPricingCalculator.Round2(lines.Sum(l => l.Salary))
                    : OrderPricingCalculator.CalculateCleanerTotalSalary(
                        totalDuration, maidsCount, hasCleanerService, orderHourlyRate)
            };
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
