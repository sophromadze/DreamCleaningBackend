using System.Globalization;

namespace DreamCleaningBackend.Helpers.Commercial
{
    /// <summary>The label and the text that describe what cleanings an invoice covers.</summary>
    public readonly record struct ServiceDateDisplay(string Label, string Text)
    {
        public static readonly ServiceDateDisplay None = new(string.Empty, string.Empty);
        public bool HasValue => !string.IsNullOrWhiteSpace(Text);
    }

    /// <summary>
    /// THE ONE PLACE an invoice's service dates are turned into words, shared by the customer web
    /// page, the PDF, the email and the admin panel so the four cannot describe the same invoice
    /// differently.
    ///
    /// What it exists to prevent, in order of how badly each one reads to a client:
    ///
    ///  - <b>An invented range.</b> "September 8-12" for a single Monday cleaning is not a period
    ///    anyone agreed to; when there is nothing recorded this returns NOTHING, and the surfaces
    ///    print no service line at all rather than filling the gap.
    ///  - <b>A range for one visit.</b> One cleaning is a DATE - "September 7, 2026" - not
    ///    "September 7 - September 7".
    ///  - <b>A range that hides the visits.</b> An invoice covering four Wednesdays lists them:
    ///    "October 7, 14, 21, 28, 2026". The client can check that against their own diary; a bare
    ///    "October 1-31" cannot be checked against anything.
    ///
    /// The LABEL changes with the shape, because "Service period" over a single date is the sort
    /// of small wrongness that makes a client doubt the rest of the document.
    /// </summary>
    public static class ServiceDateFormatter
    {
        private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

        public const string SingleLabel = "Service date";
        public const string ListLabel = "Service dates";
        public const string PeriodLabel = "Service period";

        /// <summary>
        /// Builds the display from the three things an invoice can record: an explicit list of
        /// visit dates, and/or the period bounds.
        ///
        /// The explicit list wins when it has more than one date, because it says strictly more
        /// than the bounds do. A single-date list and a start==end range are the same statement
        /// and both render as one date.
        /// </summary>
        public static ServiceDateDisplay Describe(
            IEnumerable<DateTime>? serviceDates, DateTime? start, DateTime? end)
        {
            var dates = (serviceDates ?? Enumerable.Empty<DateTime>())
                .Select(d => d.Date)
                .Distinct()
                .OrderBy(d => d)
                .ToList();

            if (dates.Count == 1) return new ServiceDateDisplay(SingleLabel, LongDate(dates[0]));
            if (dates.Count > 1) return new ServiceDateDisplay(ListLabel, JoinDates(dates));

            if (start == null && end == null) return ServiceDateDisplay.None;

            var from = (start ?? end!.Value).Date;
            var to = (end ?? start!.Value).Date;
            if (to < from) (from, to) = (to, from);

            if (from == to) return new ServiceDateDisplay(SingleLabel, LongDate(from));

            return new ServiceDateDisplay(PeriodLabel, FormatRange(from, to));
        }

        /// <summary>
        /// "October 7, 14, 21, 28, 2026" - the month and year are stated once, which is how a
        /// person writes a list of dates inside one month. A list that crosses a month or a year
        /// spells each date out, because the compact form would be ambiguous.
        /// </summary>
        public static string JoinDates(IReadOnlyList<DateTime> dates)
        {
            if (dates.Count == 0) return string.Empty;
            if (dates.Count == 1) return LongDate(dates[0]);

            var sameMonth = dates.All(d => d.Year == dates[0].Year && d.Month == dates[0].Month);

            if (sameMonth)
            {
                var days = string.Join(", ", dates.Select(d => d.Day.ToString(Us)));
                return $"{dates[0].ToString("MMMM", Us)} {days}, {dates[0].Year}";
            }

            return string.Join(", ", dates.Select(LongDate));
        }

        /// <summary>
        /// "October 1-31, 2026" inside one month; "October 28 - November 4, 2026" across two;
        /// both years spelled out when it straddles a new year.
        /// </summary>
        public static string FormatRange(DateTime from, DateTime to)
        {
            if (from.Year == to.Year && from.Month == to.Month)
                return $"{from.ToString("MMMM", Us)} {from.Day}-{to.Day}, {from.Year}";

            if (from.Year == to.Year)
                return $"{from.ToString("MMMM d", Us)} - {to.ToString("MMMM d", Us)}, {from.Year}";

            return $"{LongDate(from)} - {LongDate(to)}";
        }

        public static string LongDate(DateTime date) => date.ToString("MMMM d, yyyy", Us);
    }
}
