namespace DreamCleaningBackend.Helpers.Commercial
{
    /// <summary>
    /// Which CONTRACT SERVICE WEEK a cleaning belongs to. Pure and clock-free.
    ///
    /// A weekly flat fee is charged once per service week, so an invoice covering selected
    /// cleanings bills one fee per DISTINCT week those cleanings fall in — never one per cleaning,
    /// and never by a rolling seven-day window counted from whichever cleaning happens to be first.
    ///
    /// The week boundary is the contract's own: <c>ScheduleSnapshot.WeekDefinition</c>, the
    /// "Monday through Sunday" sentence Section 5(a) of the agreement quotes (it decides which week
    /// a makeup visit settles). That field is free text, so the first weekday it names is the week
    /// start; text that names none falls back to Monday, the template default.
    /// </summary>
    public static class ServiceWeekCalculator
    {
        public const DayOfWeek DefaultWeekStart = DayOfWeek.Monday;

        /// <summary>The weekday a contract's service week starts on.</summary>
        public static DayOfWeek ParseWeekStart(string? weekDefinition)
        {
            if (string.IsNullOrWhiteSpace(weekDefinition)) return DefaultWeekStart;

            var text = weekDefinition.Trim();
            DayOfWeek? first = null;
            var firstIndex = int.MaxValue;

            foreach (DayOfWeek day in Enum.GetValues(typeof(DayOfWeek)))
            {
                var index = text.IndexOf(day.ToString(), StringComparison.OrdinalIgnoreCase);
                if (index >= 0 && index < firstIndex)
                {
                    firstIndex = index;
                    first = day;
                }
            }

            return first ?? DefaultWeekStart;
        }

        /// <summary>The first day of the service week <paramref name="date"/> falls in.</summary>
        public static DateTime WeekStartOf(DateTime date, DayOfWeek weekStart)
        {
            var offset = ((int)date.DayOfWeek - (int)weekStart + 7) % 7;
            return date.Date.AddDays(-offset);
        }

        /// <summary>Groups dates by service week, earliest week first. Keys are week starts.</summary>
        public static List<IGrouping<DateTime, T>> GroupByWeek<T>(
            IEnumerable<T> items, Func<T, DateTime> dateOf, DayOfWeek weekStart) =>
            items.GroupBy(i => WeekStartOf(dateOf(i), weekStart))
                 .OrderBy(g => g.Key)
                 .ToList();
    }
}
