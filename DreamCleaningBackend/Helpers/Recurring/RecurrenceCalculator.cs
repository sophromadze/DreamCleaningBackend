using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Helpers.Recurring
{
    /// <summary>
    /// WHICH DATES a recurring series lands on. Pure, clock-injected and database-free, so every
    /// rule below is asserted directly rather than through a generated order.
    ///
    /// Two decisions are load-bearing:
    ///
    /// <b>1. Occurrences are measured from the ANCHOR, never from the previous one.</b>
    /// "Every 1 month" from January 31 gives Feb 28, then <b>March 31</b> — not March 28. Walking
    /// forward from the last generated date would let a single February clamp permanently shorten
    /// the schedule, and the customer who booked the 31st would silently end up on the 28th
    /// forever. <see cref="AddInterval"/> therefore always adds `n × interval` to the anchor.
    ///
    /// <b>2. Month arithmetic clamps to the end of the target month.</b> January 31 + 1 month is
    /// February 28 (or 29 in a leap year), never March 3. `DateTime.AddMonths` already does
    /// exactly this; it is named here because it is the behaviour the requirement asks for and
    /// somebody re-implementing the loop by hand would get it wrong.
    /// </summary>
    public static class RecurrenceCalculator
    {
        /// <summary>How far ahead orders are materialized. The "rolling 30-day horizon".</summary>
        public const int HorizonDays = 30;

        /// <summary>
        /// A hard ceiling on how many occurrences one sweep will produce for one series. A 2-day
        /// series over 30 days is 15; anything approaching this means the interval is nonsense or
        /// the anchor is years in the past, and grinding out thousands of rows is worse than
        /// stopping. Never reached by a valid configuration.
        /// </summary>
        public const int MaxOccurrencesPerSweep = 200;

        /// <summary>
        /// Why a recurrence configuration was refused, or null when it is valid.
        ///
        /// DAILY IS REFUSED HERE, once, so the endpoint, the generator and the admin form cannot
        /// disagree about it. `Days` with an interval of 1 means a cleaning every single day, which
        /// this system has no staffing, billing or reminder behaviour for — accepting it would
        /// produce thirty plausible-looking orders a month that nobody had designed for.
        /// Two days and up are ordinary and allowed.
        /// </summary>
        public static string? Validate(RecurrenceIntervalUnit unit, int intervalValue)
        {
            if (intervalValue < 1)
                return "The interval must be at least 1.";

            if (unit == RecurrenceIntervalUnit.Days && intervalValue == 1)
                return "Daily recurrence is not supported yet. Choose an interval of 2 days or more, "
                       + "or use weeks or months.";

            var max = unit switch
            {
                RecurrenceIntervalUnit.Days => 90,
                RecurrenceIntervalUnit.Weeks => 26,
                RecurrenceIntervalUnit.Months => 12,
                _ => 1
            };

            if (intervalValue > max)
                return $"An interval of {intervalValue} {unit.ToString().ToLowerInvariant()} is too long "
                       + $"(maximum {max}).";

            return null;
        }

        public static bool IsValid(RecurrenceIntervalUnit unit, int intervalValue) =>
            Validate(unit, intervalValue) == null;

        /// <summary>
        /// The date <paramref name="steps"/> intervals after the anchor. Always computed from the
        /// anchor — see the class comment for why walking forward is wrong.
        /// </summary>
        public static DateTime AddInterval(
            DateTime anchor, RecurrenceIntervalUnit unit, int intervalValue, int steps)
        {
            var offset = intervalValue * steps;

            return unit switch
            {
                RecurrenceIntervalUnit.Days => anchor.Date.AddDays(offset),
                RecurrenceIntervalUnit.Weeks => anchor.Date.AddDays(offset * 7),
                // AddMonths clamps: Jan 31 + 1 => Feb 28/29, and because the offset is always
                // measured from the anchor, the NEXT step returns to the 31st.
                RecurrenceIntervalUnit.Months => anchor.Date.AddMonths(offset),
                _ => anchor.Date
            };
        }

        /// <summary>
        /// Every occurrence date strictly required to fill the horizon, in ascending order.
        ///
        /// Deliberately returns dates the caller may already have: the caller de-duplicates
        /// against what exists (and the unique index has the final word), because a generator that
        /// decided what was new from its own memory would double-book after a restart.
        /// </summary>
        /// <param name="anchor">First service date of the series.</param>
        /// <param name="today">Today in NEW YORK — a service date is a wall-clock date, and
        /// reading UTC after 8pm NY would roll the horizon a day forward every evening.</param>
        /// <param name="horizonDays">How far ahead to fill. 30 in production.</param>
        /// <param name="endDate">Series end, inclusive. Null = open-ended.</param>
        public static List<DateTime> OccurrencesWithinHorizon(
            DateTime anchor,
            RecurrenceIntervalUnit unit,
            int intervalValue,
            DateTime today,
            int horizonDays = HorizonDays,
            DateTime? endDate = null)
        {
            var dates = new List<DateTime>();
            if (!IsValid(unit, intervalValue)) return dates;

            var from = today.Date;
            var to = from.AddDays(horizonDays);
            if (endDate.HasValue && endDate.Value.Date < to) to = endDate.Value.Date;
            if (to < from) return dates;

            // Start from the first occurrence that is not in the past. Found by stepping rather
            // than by division because month intervals are not a fixed number of days.
            var step = FirstStepOnOrAfter(anchor, unit, intervalValue, from);

            for (var guard = 0; guard < MaxOccurrencesPerSweep; guard++, step++)
            {
                var date = AddInterval(anchor, unit, intervalValue, step);
                if (date > to) break;
                if (date < from) continue;   // only possible on the very first step
                dates.Add(date);
            }

            return dates;
        }

        /// <summary>
        /// The smallest step count whose occurrence falls on or after <paramref name="from"/>.
        ///
        /// Estimated first (cheap division on the average length of the unit) and then corrected
        /// in both directions, so a series anchored years ago costs a handful of iterations rather
        /// than one per interval since.
        /// </summary>
        public static int FirstStepOnOrAfter(
            DateTime anchor, RecurrenceIntervalUnit unit, int intervalValue, DateTime from)
        {
            if (from.Date <= anchor.Date) return 0;

            var elapsedDays = (from.Date - anchor.Date).TotalDays;
            var approxUnitDays = unit switch
            {
                RecurrenceIntervalUnit.Days => 1d,
                RecurrenceIntervalUnit.Weeks => 7d,
                RecurrenceIntervalUnit.Months => 30.4375d,
                _ => 1d
            };

            var step = (int)Math.Floor(elapsedDays / (approxUnitDays * Math.Max(1, intervalValue)));
            if (step < 0) step = 0;

            // Correct downwards, then upwards. Both loops are bounded: the estimate is never more
            // than a couple of steps out for any supported interval.
            while (step > 0 && AddInterval(anchor, unit, intervalValue, step - 1) >= from.Date)
                step--;

            while (AddInterval(anchor, unit, intervalValue, step) < from.Date)
                step++;

            return step;
        }

        /// <summary>Human wording for the admin panel and audit payloads: "Every 2 weeks".</summary>
        public static string Describe(RecurrenceIntervalUnit unit, int intervalValue)
        {
            var noun = unit switch
            {
                RecurrenceIntervalUnit.Days => "day",
                RecurrenceIntervalUnit.Weeks => "week",
                RecurrenceIntervalUnit.Months => "month",
                _ => "interval"
            };

            return intervalValue == 1 ? $"Every {noun}" : $"Every {intervalValue} {noun}s";
        }

        /// <summary>"Every week on Sun, Mon, Tue" / "Every 2 months on the 5th and 20th".</summary>
        public static string Describe(RecurrenceRule rule)
        {
            var text = Describe(rule.Unit, rule.IntervalValue);
            if (rule.UsesWeekdays)
                text += " on " + string.Join(", ", OrderedFrom(rule.DaysOfWeek, DayOfWeek.Sunday)
                    .Select(d => d.ToString()[..3]));
            else if (rule.UsesMonthDays)
                text += " on the " + string.Join(", ", rule.DaysOfMonth.Select(Ordinal));
            return text;
        }

        // ══ Multi-day patterns and the upcoming-count target (2026-10) ═════════════════════════
        //
        // A plan may name SEVERAL weekdays ("every week, Sunday to Friday") or several days of the
        // month ("the 1st, 15th and 30th"), and may keep a fixed NUMBER of upcoming cleanings
        // instead of a 30-day window. A plan with neither list keeps the single anchor-relative
        // date exactly as before — the old shape is never reinterpreted.

        /// <summary>
        /// The most upcoming cleanings a plan may keep materialized. Ten weeks of a six-visit
        /// commercial schedule, or more than a year of a weekly one — enough for any real
        /// arrangement, small enough that a typo cannot create hundreds of bookings.
        /// </summary>
        public const int MaxUpcomingOccurrenceTarget = 60;

        /// <summary>
        /// How many candidate dates one search may examine. A month-day pattern like "the 31st
        /// every 12 months" anchored in a 30-day month skips for a long time; this guarantees the
        /// search ends even for a pattern that can never land.
        /// </summary>
        public const int MaxCandidateScan = 2000;

        public static List<DayOfWeek> ParseDaysOfWeek(string? csv) =>
            ParseInts(csv).Where(i => i >= 0 && i <= 6).Select(i => (DayOfWeek)i).Distinct()
                .OrderBy(d => (int)d).ToList();

        public static string? FormatDaysOfWeek(IEnumerable<DayOfWeek>? days)
        {
            var list = days?.Distinct().OrderBy(d => (int)d).ToList();
            return list == null || list.Count == 0 ? null : string.Join(",", list.Select(d => (int)d));
        }

        public static List<int> ParseDaysOfMonth(string? csv) =>
            ParseInts(csv).Where(i => i >= 1 && i <= 31).Distinct().OrderBy(i => i).ToList();

        public static string? FormatDaysOfMonth(IEnumerable<int>? days)
        {
            var list = days?.Distinct().OrderBy(d => d).ToList();
            return list == null || list.Count == 0 ? null : string.Join(",", list);
        }

        /// <summary>
        /// Why a multi-day selection or count was refused, or null. Lists are OPTIONAL — null means
        /// the old single-date behaviour — but a list that is sent must be valid for its unit.
        /// </summary>
        public static string? ValidatePattern(
            RecurrenceIntervalUnit unit, IReadOnlyCollection<int>? daysOfWeek,
            IReadOnlyCollection<int>? daysOfMonth, int? upcomingTarget)
        {
            if (daysOfWeek != null)
            {
                if (unit != RecurrenceIntervalUnit.Weeks)
                    return "Service days can only be chosen for a weekly schedule.";
                if (daysOfWeek.Count == 0)
                    return "Choose at least one service day.";
                if (daysOfWeek.Any(d => d < 0 || d > 6))
                    return "A service day must be Sunday through Saturday.";
            }

            if (daysOfMonth != null)
            {
                if (unit != RecurrenceIntervalUnit.Months)
                    return "Days of the month can only be chosen for a monthly schedule.";
                if (daysOfMonth.Count == 0)
                    return "Choose at least one day of the month.";
                if (daysOfMonth.Any(d => d < 1 || d > 31))
                    return "A day of the month must be between 1 and 31.";
            }

            if (upcomingTarget.HasValue
                && (upcomingTarget.Value < 1 || upcomingTarget.Value > MaxUpcomingOccurrenceTarget))
                return $"Generate between 1 and {MaxUpcomingOccurrenceTarget} upcoming cleanings.";

            return null;
        }

        /// <summary>
        /// Every occurrence of the rule on or after <paramref name="from"/> (and never before the
        /// anchor), ascending, stopping at the end date. Lazy: callers take what they need.
        /// </summary>
        public static IEnumerable<DateTime> Occurrences(RecurrenceRule rule, DateTime from)
        {
            if (!IsValid(rule.Unit, rule.IntervalValue)) yield break;

            var anchor = rule.Anchor.Date;
            var start = from.Date < anchor ? anchor : from.Date;
            var end = rule.EndDate?.Date;
            var scanned = 0;

            if (rule.UsesWeekdays)
            {
                // Cycles are whole weeks counted from the week the anchor falls in, so "every 2
                // weeks, Monday and Wednesday" is Mon+Wed of week 0, week 2, week 4 … — the days
                // of one cycle always travel together.
                var cycleLength = 7 * rule.IntervalValue;
                var firstWeek = StartOfWeek(anchor, rule.WeekStart);
                var cycle = Math.Max(0, (int)((start - firstWeek).TotalDays / cycleLength));
                var offsets = OrderedFrom(rule.DaysOfWeek, rule.WeekStart)
                    .Select(d => ((int)d - (int)rule.WeekStart + 7) % 7).ToList();

                while (scanned < MaxCandidateScan)
                {
                    var weekStart = firstWeek.AddDays((double)cycle * cycleLength);
                    if (end.HasValue && weekStart > end.Value) yield break;

                    foreach (var offset in offsets)
                    {
                        scanned++;
                        var date = weekStart.AddDays(offset);
                        if (date < start) continue;
                        if (end.HasValue && date > end.Value) yield break;
                        yield return date;
                    }
                    cycle++;
                }
                yield break;
            }

            if (rule.UsesMonthDays)
            {
                // Cycles are calendar months counted from the anchor's month. A day the month does
                // not have is SKIPPED for that month — never moved to the 30th or to the 1st.
                var firstMonth = new DateTime(anchor.Year, anchor.Month, 1);
                var elapsedMonths = (start.Year - firstMonth.Year) * 12 + start.Month - firstMonth.Month;
                var cycle = Math.Max(0, elapsedMonths / rule.IntervalValue);
                var monthDays = rule.DaysOfMonth.Where(d => d is >= 1 and <= 31).Distinct().OrderBy(d => d).ToList();
                if (monthDays.Count == 0) yield break;

                while (scanned < MaxCandidateScan)
                {
                    var month = firstMonth.AddMonths(cycle * rule.IntervalValue);
                    if (end.HasValue && month > end.Value) yield break;
                    var length = DateTime.DaysInMonth(month.Year, month.Month);

                    foreach (var day in monthDays)
                    {
                        scanned++;
                        if (day > length) continue;
                        var date = month.AddDays(day - 1);
                        if (date < start) continue;
                        if (end.HasValue && date > end.Value) yield break;
                        yield return date;
                    }
                    cycle++;
                }
                yield break;
            }

            // The original single anchor-relative date, unchanged.
            for (var step = FirstStepOnOrAfter(anchor, rule.Unit, rule.IntervalValue, start);
                 scanned < MaxCandidateScan; step++, scanned++)
            {
                var date = AddInterval(anchor, rule.Unit, rule.IntervalValue, step);
                if (end.HasValue && date > end.Value) yield break;
                if (date >= start) yield return date;
            }
        }

        /// <summary>The rolling-horizon dates for a rule — what a plan with no count target uses.</summary>
        public static List<DateTime> OccurrencesWithinHorizon(
            RecurrenceRule rule, DateTime today, int horizonDays = HorizonDays)
        {
            var to = today.Date.AddDays(horizonDays);
            return Occurrences(rule, today)
                .TakeWhile(d => d <= to)
                .Take(MaxOccurrencesPerSweep)
                .ToList();
        }

        /// <summary>
        /// The next <paramref name="count"/> occurrence dates that do not exist yet, earliest first.
        ///
        /// <paramref name="taken"/> is every occurrence date the plan already holds (whatever its
        /// status — a skipped visit keeps its slot), and the caller has already subtracted the
        /// upcoming cleanings that exist from the target, so running this twice in a row asks for
        /// nothing the second time. The unique index remains the final duplicate guard.
        /// </summary>
        public static List<DateTime> NextMissingOccurrences(
            RecurrenceRule rule, DateTime today, IEnumerable<DateTime> taken, int count, DateTime? generateAfter = null)
        {
            if (count <= 0) return new List<DateTime>();
            var existing = taken.Select(d => d.Date).ToHashSet();

            return Occurrences(rule, today)
                .Where(d => !generateAfter.HasValue || d > generateAfter.Value.Date)
                .Where(d => !existing.Contains(d))
                .Take(Math.Min(count, MaxUpcomingOccurrenceTarget))
                .ToList();
        }

        /// <summary>The first day of the 7-day block containing <paramref name="date"/>.</summary>
        public static DateTime StartOfWeek(DateTime date, DayOfWeek weekStart) =>
            date.Date.AddDays(-(((int)date.DayOfWeek - (int)weekStart + 7) % 7));

        private static IEnumerable<DayOfWeek> OrderedFrom(IEnumerable<DayOfWeek> days, DayOfWeek first) =>
            days.Distinct().OrderBy(d => ((int)d - (int)first + 7) % 7);

        private static IEnumerable<int> ParseInts(string? csv) =>
            string.IsNullOrWhiteSpace(csv)
                ? Enumerable.Empty<int>()
                : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(p => int.TryParse(p, out var n) ? n : -1);

        private static string Ordinal(int day) => day + (day is 11 or 12 or 13 ? "th" : (day % 10) switch
        {
            1 => "st",
            2 => "nd",
            3 => "rd",
            _ => "th"
        });
    }

    /// <summary>
    /// Everything that decides a plan's dates, in one value. Built from a series by
    /// <see cref="From"/>; empty day lists mean the original single-date behaviour.
    /// </summary>
    public sealed record RecurrenceRule(
        DateTime Anchor,
        RecurrenceIntervalUnit Unit,
        int IntervalValue,
        IReadOnlyList<DayOfWeek> DaysOfWeek,
        IReadOnlyList<int> DaysOfMonth,
        DayOfWeek WeekStart = DayOfWeek.Sunday,
        DateTime? EndDate = null)
    {
        public bool UsesWeekdays => Unit == RecurrenceIntervalUnit.Weeks && DaysOfWeek.Count > 0;
        public bool UsesMonthDays => Unit == RecurrenceIntervalUnit.Months && DaysOfMonth.Count > 0;

        /// <summary>
        /// <paramref name="weekStart"/> is where a weekly cycle begins: the linked contract's
        /// service-week start, so a fortnightly plan's cycles line up with its billing weeks, and
        /// Sunday otherwise (the order the panel lists the days in).
        /// </summary>
        public static RecurrenceRule From(RecurringOrderSeries series, DayOfWeek weekStart = DayOfWeek.Sunday) =>
            new(series.AnchorDate, series.IntervalUnit, series.IntervalValue,
                RecurrenceCalculator.ParseDaysOfWeek(series.ServiceDaysOfWeek),
                RecurrenceCalculator.ParseDaysOfMonth(series.ServiceDaysOfMonth),
                weekStart, series.EndDate);
    }
}
