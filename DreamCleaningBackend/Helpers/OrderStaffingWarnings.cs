using DreamCleaningBackend.Services;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// Everything about an order's STAFFING that somebody should look at before acting on it —
    /// the wrong hourly rate, cleaners who worked but are not on file, more people on the job
    /// than it was priced for, no duration to pay against, a customer who has not paid.
    ///
    /// These NEVER block anything. The job happened, the cleaner is owed either way, and the
    /// order can still be edited, staffed and paid. The warning exists so a mistake is noticed
    /// at the moment somebody is looking at the order, which is the one moment it reliably gets
    /// fixed.
    ///
    /// Single source since 2026-08-31, when the admin Orders panel grew the same block. Outgoing
    /// Payments (where they started) and the Orders panel are two views of the same job, so two
    /// implementations would have been free to disagree about it — and an admin comparing the
    /// screens would have no way to tell which one was right. The text is written to read on
    /// both: it names what is wrong and what it costs, never "before paying" or "on this page".
    /// </summary>
    public static class OrderStaffingWarnings
    {
        /// <summary>
        /// The raw order columns the warnings are derived FROM, before any payroll math.
        ///
        /// This is the entry point for a caller that has an order but not a payroll result — the
        /// Orders tab's bulk endpoint, which projects these columns for many orders at once rather
        /// than loading an entity graph per row. <see cref="BuildFromFacts"/> then runs the same
        /// CleanerPayrollCalculator and the same expected-rate lookup Outgoing Payments runs, so
        /// arriving from either direction produces identical text.
        /// </summary>
        public class OrderFacts
        {
            /// <summary>Display name — a custom order's own label, already resolved.</summary>
            public string ServiceTypeName { get; set; } = "Cleaning";

            /// <summary>Any OrderService whose Service.ServiceRelationType is "cleaner".</summary>
            /// <remarks>
            /// Read from the RELATION TYPE, never the service key — same rule as
            /// CleanerPayrollCalculator.HasCleanerHoursService. A wrong answer here is a 2x error
            /// in the hours every warning below is measured against.
            /// </remarks>
            public bool HasCleanerHoursService { get; set; }

            /// <summary>Any extra whose name contains "deep cleaning" — "Super Deep" included,
            /// deliberately, exactly as the payouts page matches it. Moves the expected rate.</summary>
            public bool HasDeepCleaningExtra { get; set; }

            public decimal TotalDuration { get; set; }
            public int MaidsCount { get; set; }
            public decimal CleanerHourlyRate { get; set; }
            public decimal Tips { get; set; }

            /// <summary>False only when the customer genuinely still owes for this order.</summary>
            public bool IsPaidByCustomer { get; set; }

            /// <summary>The order's assignment rows, in a stable order (assignment id).</summary>
            public IReadOnlyList<CleanerPayrollCalculator.AssignmentInput> Assignments { get; set; }
                = new List<CleanerPayrollCalculator.AssignmentInput>();
        }

        /// <summary>
        /// Runs the payroll and the expected-rate lookup for one order, then builds its warnings.
        /// The only route a caller without a payroll result should take — doing the two steps by
        /// hand at a call site is how an expected rate ends up resolved one way on one screen and
        /// another way on the next.
        /// </summary>
        public static List<string> BuildFromFacts(OrderFacts facts)
        {
            var payroll = CleanerPayrollCalculator.Build(
                facts.TotalDuration,
                facts.MaidsCount,
                facts.HasCleanerHoursService,
                facts.CleanerHourlyRate,
                facts.Tips,
                facts.Assignments);

            var expectedRate = OrderPricingCalculator.GetDefaultCleanerHourlyRate(
                facts.HasDeepCleaningExtra ? 1m : 0m, facts.ServiceTypeName);

            var unassignedTipEach = payroll.UnassignedCount == 0
                ? 0m
                : OrderPricingCalculator.Round2(payroll.UnassignedTips / payroll.UnassignedCount);

            return Build(new Input
            {
                ServiceTypeName = facts.ServiceTypeName,
                ExpectedHourlyRate = expectedRate,
                AssignedHourlyRates = payroll.Lines.Select(l => l.HourlyRate).ToList(),
                SplitCount = payroll.SplitCount,
                UnassignedCount = payroll.UnassignedCount,
                TotalSalary = payroll.TotalSalary,
                UnassignedPayoutEach = OrderPricingCalculator.Round2(
                    payroll.UnassignedSalaryEach + unassignedTipEach),
                MaidsCount = facts.MaidsCount,
                TotalDuration = facts.TotalDuration,
                IsPaidByCustomer = facts.IsPaidByCustomer
            });
        }

        /// <summary>What the two callers already know about the order, in the terms the messages use.</summary>
        public class Input
        {
            /// <summary>Display service-type name — a custom order's own label. Names the rate default.</summary>
            public string ServiceTypeName { get; set; } = "Cleaning";

            /// <summary>The rate this service type should default to, from the shared calculator.</summary>
            public decimal ExpectedHourlyRate { get; set; }

            /// <summary>The effective hourly rate of each ASSIGNED cleaner — override or order default.</summary>
            public IReadOnlyList<decimal> AssignedHourlyRates { get; set; } = new List<decimal>();

            /// <summary>How many people the work was split across — max(MaidsCount, assigned).</summary>
            public int SplitCount { get; set; }

            /// <summary>Staffing slots with nobody assigned (SplitCount − assigned).</summary>
            public int UnassignedCount { get; set; }

            /// <summary>Wages for the whole job, assigned lines plus unstaffed slots.</summary>
            public decimal TotalSalary { get; set; }

            /// <summary>What ONE unstaffed slot is owed, wages plus its share of tips.</summary>
            public decimal UnassignedPayoutEach { get; set; }

            /// <summary>Order.MaidsCount — what the order was priced/staffed for.</summary>
            public int MaidsCount { get; set; }

            /// <summary>Order.TotalDuration in minutes. Zero means nothing to pay against.</summary>
            public decimal TotalDuration { get; set; }

            /// <summary>False only when the customer genuinely still owes for this order.</summary>
            public bool IsPaidByCustomer { get; set; }
        }

        public static List<string> Build(Input input)
        {
            var warnings = new List<string>();
            var assignedCount = input.AssignedHourlyRates.Count;

            // The rate warning is per DISTINCT rate: with mixed rates on one job, naming each one
            // is what tells the reader whether the odd one out was deliberate.
            var offRates = input.AssignedHourlyRates
                .Where(r => r != input.ExpectedHourlyRate)
                .Distinct()
                .OrderBy(r => r)
                .ToList();

            if (offRates.Count > 0)
            {
                var listed = string.Join(", ", offRates.Select(r => $"${r:0.##}/hr"));
                warnings.Add($"Hourly rate is {listed}, but {input.ServiceTypeName} should default to "
                    + $"${input.ExpectedHourlyRate:0.##}/hr. Check whether this was intentional before paying.");
            }

            // Under-staffed on paper: those people worked and are owed, but there is no cleaner
            // record naming them. The per-cleaner HOURS are unaffected — the split follows the
            // count the job was staffed for — so this is about who, not about how much.
            if (input.UnassignedCount > 0)
            {
                warnings.Add(assignedCount == 0
                    ? $"Nobody on this order is in the system. It was staffed for {input.SplitCount} cleaner(s), "
                      + $"so ${input.TotalSalary:0.00} of wages is owed — the payouts can still be recorded, "
                      + "but not against a name."
                    : $"{assignedCount} of {input.SplitCount} cleaner(s) are in the system. The other "
                      + $"{input.UnassignedCount} payout(s) of ${input.UnassignedPayoutEach:0.00} each can be "
                      + "recorded, but not against a name — add a note saying who was paid.");
            }

            // Over-assigned: more people on the job than it was priced for, so the work divides
            // further and everyone's share drops. On an ordinary service type assigning cleaners
            // now raises MaidsCount to match, so this is left for the two kinds of order where
            // the count is a priced selection and deliberately does NOT follow the assignments —
            // cleaner+hours types and Custom ("Pre-Arranged").
            if (assignedCount > input.MaidsCount && input.MaidsCount > 0)
            {
                warnings.Add($"{assignedCount} cleaner(s) are assigned but the order was priced for "
                    + $"{input.MaidsCount}. The hours are split {assignedCount} ways, so each share is smaller "
                    + "than the booking assumed.");
            }

            if (input.TotalDuration <= 0)
                warnings.Add("This order has no duration recorded, so every cleaner's pay calculates to $0.");

            if (!input.IsPaidByCustomer)
                warnings.Add("The customer has not paid for this order yet.");

            return warnings;
        }
    }
}
