using DreamCleaningBackend.Models.Contracts;

namespace DreamCleaningBackend.Helpers.Commercial
{
    /// <summary>What the caller knows about a contract's schedule and its last invoice.</summary>
    public class ServiceScheduleInput
    {
        /// <summary>
        /// The weekdays the premises are actually cleaned. Empty is legitimate - a monthly
        /// single-visit contract is scheduled by date, not by weekday.
        /// </summary>
        public List<DayOfWeek> ServiceDays { get; set; } = new();

        /// <summary>The contract's own service frequency unit, e.g. "calendar week".</summary>
        public string FrequencyUnit { get; set; } = "calendar week";

        public int VisitsPerPeriod { get; set; } = 1;

        public ContractBillingFrequency BillingFrequency { get; set; } = ContractBillingFrequency.Monthly;

        /// <summary>N in "every N weeks" / "every N months" / "every N days". Always at least 1.</summary>
        public int BillingIntervalCount { get; set; } = 1;

        /// <summary>
        /// The end of the period the previous FINALIZED invoice covered. The next period starts
        /// the day after, which is what makes consecutive invoices tile without a gap or an
        /// overlap.
        /// </summary>
        public DateTime? PreviousPeriodEnd { get; set; }

        /// <summary>
        /// The first service date the previous invoice covered. Only used for the monthly
        /// single-visit shape, where the day OF THE MONTH is the schedule and the weekday is not.
        /// </summary>
        public DateTime? PreviousFirstServiceDate { get; set; }

        /// <summary>
        /// Where to start counting when there is no previous invoice: the billing anchor, or the
        /// contract's effective date.
        /// </summary>
        public DateTime? AnchorDate { get; set; }

        /// <summary>Business "today", in New York. Never read from the clock inside here.</summary>
        public DateTime Today { get; set; }

        /// <summary>Exhibit B's "payment due N hours before service". Drives the due date.</summary>
        public int PaymentDeadlineHours { get; set; } = 48;
    }

    /// <summary>The generated period, its service dates, and the due date they imply.</summary>
    public class ServiceScheduleResult
    {
        /// <summary>
        /// False when the contract does not carry enough schedule information to name a
        /// legitimate service date. The caller must then leave the invoice's dates BLANK and make
        /// the admin choose - inventing a plausible-looking range is the failure this exists to
        /// prevent.
        /// </summary>
        public bool HasSchedule { get; set; }

        /// <summary>Why not, when <see cref="HasSchedule"/> is false. Shown to the admin.</summary>
        public string? Reason { get; set; }

        public DateTime? PeriodStart { get; set; }
        public DateTime? PeriodEnd { get; set; }

        /// <summary>Every expected visit inside the period, ascending. Never empty when HasSchedule.</summary>
        public List<DateTime> ServiceDates { get; set; } = new();

        /// <summary>
        /// <see cref="ServiceScheduleInput.PaymentDeadlineHours"/> before the FIRST service date,
        /// date-only. Exhibit B promises payment ahead of service, so it is anchored to the first
        /// visit the invoice covers, never to the last one or to the period end.
        /// </summary>
        public DateTime? DueDate { get; set; }

        public DateTime? FirstServiceDate => ServiceDates.Count > 0 ? ServiceDates[0] : null;
    }

    /// <summary>
    /// Works out WHICH CLEANINGS the next invoice covers, and when it is due.
    ///
    /// The whole point of this file is that the SERVICE schedule and the BILLING schedule are two
    /// different questions. "Every Wednesday" answers when we clean; "once a month" answers how
    /// often we invoice; and a monthly invoice for weekly cleaning legitimately covers four or
    /// five visits. Conflating them is what produced invented ranges like "September 8-12" that no
    /// actual schedule supported.
    ///
    /// Two schedule shapes are recognised, and the discriminator is the contract's own frequency
    /// unit rather than a guess:
    ///
    ///  - <b>Weekday-driven</b> (the normal case). The visits are the selected weekdays that fall
    ///    inside the billing period.
    ///  - <b>Monthly single visit</b>. One cleaning per calendar month is scheduled by DAY OF THE
    ///    MONTH - the 7th - and advancing it by weekday would be wrong. September 7 becomes
    ///    October 7, not "the first Monday in October".
    ///
    /// Pure and static, with "today" passed in, so every rule below is asserted directly in
    /// <c>RecurringInvoiceScheduleTests</c> without a database or a clock.
    /// </summary>
    public static class ServiceScheduleCalculator
    {
        /// <summary>The weekday names the contract form and the snapshot use, in ISO order.</summary>
        public static readonly string[] WeekdayNames =
        {
            "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"
        };

        public static DayOfWeek? ParseWeekday(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            return Enum.TryParse<DayOfWeek>(name.Trim(), true, out var day) ? day : null;
        }

        /// <summary>True when the contract's frequency unit names a month rather than a week.</summary>
        public static bool IsMonthlyUnit(string? frequencyUnit) =>
            frequencyUnit?.Contains("month", StringComparison.OrdinalIgnoreCase) == true;

        public static ServiceScheduleResult Next(ServiceScheduleInput input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            var interval = Math.Max(1, input.BillingIntervalCount);
            var result = new ServiceScheduleResult();

            // -- Shape 2: one visit per calendar month, scheduled by day of the month --
            //
            // Checked first because a monthly single-visit contract still carries a weekday in the
            // snapshot (the form has always had one), and treating it as weekday-driven is exactly
            // how "the 7th" turns into "every Monday".
            if (IsMonthlyUnit(input.FrequencyUnit) && Math.Max(1, input.VisitsPerPeriod) == 1)
            {
                var previous = input.PreviousFirstServiceDate ?? input.AnchorDate;
                if (previous == null)
                {
                    result.Reason =
                        "This contract bills monthly but has no previous service date and no effective "
                        + "date to count from. Choose the service date for this invoice.";
                    return result;
                }

                // Months to advance: from a previous INVOICE it is the billing interval; from the
                // effective date it is zero, because the first invoice covers the first visit.
                var months = input.PreviousFirstServiceDate.HasValue
                    ? MonthsPerBillingPeriod(input, interval)
                    : 0;

                var next = AddMonthsClamped(previous.Value.Date, months);

                // An anchor already in the past (a contract signed months ago, invoiced for the
                // first time today) is rolled forward rather than dating a service behind us.
                var guard = 0;
                while (input.PreviousFirstServiceDate == null && next < input.Today.Date && guard++ < 240)
                    next = AddMonthsClamped(next, 1);

                result.HasSchedule = true;
                result.ServiceDates.Add(next);
                result.PeriodStart = next;
                result.PeriodEnd = next;
                result.DueDate = ResolveDueDate(next, input.PaymentDeadlineHours);
                return result;
            }

            // -- Shape 1: weekday-driven --
            var days = input.ServiceDays.Distinct().OrderBy(IsoIndex).ToList();
            if (days.Count == 0)
            {
                result.Reason =
                    "This contract has no regular service days recorded, so the service dates for "
                    + "this invoice cannot be worked out. Choose them before sending.";
                return result;
            }

            var start = ResolvePeriodStart(input);
            var (periodStart, periodEnd) = ResolvePeriodWindow(input, start, interval);
            var dates = EnumerateServiceDates(periodStart, periodEnd, days);

            if (dates.Count == 0)
            {
                result.Reason =
                    "No regular service day falls inside the next billing period. Choose the "
                    + "service dates for this invoice.";
                return result;
            }

            // Per-visit billing invoices ONE cleaning, so the period collapses onto that date
            // rather than spanning a week nobody agreed to bill for.
            if (input.BillingFrequency == ContractBillingFrequency.PerServiceVisit)
            {
                var single = dates[0];
                result.HasSchedule = true;
                result.ServiceDates.Add(single);
                result.PeriodStart = single;
                result.PeriodEnd = single;
                result.DueDate = ResolveDueDate(single, input.PaymentDeadlineHours);
                return result;
            }

            result.HasSchedule = true;
            result.ServiceDates = dates;
            result.PeriodStart = periodStart;
            result.PeriodEnd = periodEnd;
            result.DueDate = ResolveDueDate(dates[0], input.PaymentDeadlineHours);
            return result;
        }

        /// <summary>
        /// Where the next period begins: the day after the last one billed, or the anchor when
        /// nothing has been billed yet.
        /// </summary>
        private static DateTime ResolvePeriodStart(ServiceScheduleInput input)
        {
            if (input.PreviousPeriodEnd.HasValue) return input.PreviousPeriodEnd.Value.Date.AddDays(1);

            // No history: start from the anchor, or from today when the anchor is already past -
            // a first invoice must not be dated into a period that has already finished.
            if (input.AnchorDate.HasValue)
                return input.AnchorDate.Value.Date > input.Today.Date
                    ? input.AnchorDate.Value.Date
                    : input.Today.Date;

            return input.Today.Date;
        }

        /// <summary>
        /// The billing window. A monthly period that starts on the 1st ends on the last day of
        /// that month, so it reads as "October 1-31" rather than drifting a day on a 31-day month.
        /// </summary>
        private static (DateTime Start, DateTime End) ResolvePeriodWindow(
            ServiceScheduleInput input, DateTime start, int interval)
        {
            switch (input.BillingFrequency)
            {
                case ContractBillingFrequency.Weekly:
                    return (start, start.AddDays(7 * interval - 1));

                case ContractBillingFrequency.Monthly:
                    if (start.Day == 1)
                    {
                        var lastMonth = start.AddMonths(interval - 1);
                        return (start, new DateTime(lastMonth.Year, lastMonth.Month,
                            DateTime.DaysInMonth(lastMonth.Year, lastMonth.Month)));
                    }
                    return (start, AddMonthsClamped(start, interval).AddDays(-1));

                case ContractBillingFrequency.CustomDays:
                    return (start, start.AddDays(Math.Max(1, interval) - 1));

                case ContractBillingFrequency.PerServiceVisit:
                default:
                    // Wide enough that the next visit is certainly inside it; the caller collapses
                    // the window onto that single date.
                    return (start, start.AddDays(120));
            }
        }

        private static int MonthsPerBillingPeriod(ServiceScheduleInput input, int interval) =>
            input.BillingFrequency == ContractBillingFrequency.Monthly ? interval : 1;

        /// <summary>Every occurrence of the selected weekdays inside the window, ascending.</summary>
        public static List<DateTime> EnumerateServiceDates(
            DateTime periodStart, DateTime periodEnd, IReadOnlyCollection<DayOfWeek> days)
        {
            var dates = new List<DateTime>();
            if (days.Count == 0 || periodEnd < periodStart) return dates;

            for (var day = periodStart.Date; day <= periodEnd.Date; day = day.AddDays(1))
                if (days.Contains(day.DayOfWeek)) dates.Add(day);

            return dates;
        }

        /// <summary>
        /// N hours before the first service date, as a DATE. 48 hours before October 7 is
        /// October 5, which is the figure Exhibit B and the previous invoice both quote.
        /// </summary>
        public static DateTime ResolveDueDate(DateTime firstServiceDate, int paymentDeadlineHours) =>
            firstServiceDate.Date.AddHours(-Math.Max(0, paymentDeadlineHours)).Date;

        /// <summary>
        /// Adds months keeping the day of the month, clamped to the target month's length - the
        /// 31st of January bills on the 28th of February, not the 3rd of March.
        /// </summary>
        public static DateTime AddMonthsClamped(DateTime date, int months)
        {
            if (months == 0) return date.Date;
            var shifted = date.Date.AddMonths(months);
            var wanted = Math.Min(date.Day, DateTime.DaysInMonth(shifted.Year, shifted.Month));
            return new DateTime(shifted.Year, shifted.Month, wanted);
        }

        /// <summary>Monday-first ordering, so a Mon/Wed/Fri list reads in the order people say it.</summary>
        private static int IsoIndex(DayOfWeek day) => day == DayOfWeek.Sunday ? 6 : (int)day - 1;
    }
}
