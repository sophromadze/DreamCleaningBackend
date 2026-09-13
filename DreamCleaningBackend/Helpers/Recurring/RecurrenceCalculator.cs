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
    }
}
