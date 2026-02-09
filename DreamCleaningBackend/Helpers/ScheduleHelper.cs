using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>Default timezone for scheduled mail/SMS: New York (America/New_York).</summary>
    public static class ScheduleHelper
    {
        public const string DefaultTimezone = "America/New_York";

        /// <summary>Single occurrence: date + time in the given timezone, converted to UTC.</summary>
        public static DateTime? FirstScheduledUtc(DateTime date, TimeSpan time, string timezone)
        {
            try
            {
                var tzi = TimeZoneInfo.FindSystemTimeZoneById(timezone);
                var local = DateTime.SpecifyKind(date.Date.Add(time), DateTimeKind.Unspecified);
                return TimeZoneInfo.ConvertTimeToUtc(local, tzi);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Next run for recurring: after "once" we use this. Weekly = next occurrence of dayOfWeek (0=Sunday..6=Saturday) at time in tz, strictly after afterUtc. Monthly = next occurrence of dayOfMonth (1-31) at time in tz.</summary>
        public static DateTime? NextRecurringUtc(MailFrequency frequency, int? dayOfWeek, int? dayOfMonth, TimeSpan time, string timezone, DateTime afterUtc)
        {
            if (frequency == MailFrequency.Once) return null;
            try
            {
                var tzi = TimeZoneInfo.FindSystemTimeZoneById(timezone);
                var afterLocal = TimeZoneInfo.ConvertTimeFromUtc(afterUtc, tzi);

                if (frequency == MailFrequency.Weekly && dayOfWeek.HasValue)
                {
                    // dayOfWeek: 0=Sunday, 1=Monday, ... 6=Saturday
                    var dow = dayOfWeek.Value;
                    if (dow < 0 || dow > 6) return null;
                    var candidate = afterLocal.Date.Add(time);
                    // DayOfWeek: Sunday=0, Monday=1, ...
                    var currentDow = (int)candidate.DayOfWeek;
                    var daysToAdd = (dow - currentDow + 7) % 7;
                    if (daysToAdd == 0 && candidate <= afterLocal)
                        daysToAdd = 7;
                    candidate = candidate.AddDays(daysToAdd);
                    return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(candidate, DateTimeKind.Unspecified), tzi);
                }

                if (frequency == MailFrequency.Monthly && dayOfMonth.HasValue)
                {
                    var dom = dayOfMonth.Value;
                    if (dom < 1 || dom > 31) return null;
                    var year = afterLocal.Year;
                    var month = afterLocal.Month;
                    DateTime candidate;
                    if (DateTime.DaysInMonth(year, month) >= dom)
                    {
                        candidate = new DateTime(year, month, dom, 0, 0, 0, DateTimeKind.Unspecified).Add(time);
                    }
                    else
                    {
                        candidate = new DateTime(year, month, DateTime.DaysInMonth(year, month), 0, 0, 0, DateTimeKind.Unspecified).Add(time);
                    }
                    if (candidate <= afterLocal)
                    {
                        month++;
                        if (month > 12) { month = 1; year++; }
                        var maxDay = DateTime.DaysInMonth(year, month);
                        var d = Math.Min(dom, maxDay);
                        candidate = new DateTime(year, month, d, 0, 0, 0, DateTimeKind.Unspecified).Add(time);
                    }
                    return TimeZoneInfo.ConvertTimeToUtc(candidate, tzi);
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Initial next run when creating/updating a schedule. Once = single occurrence. Weekly = next occurrence of dayOfWeek at time. Monthly = next occurrence of dayOfMonth at time. Uses scheduledDate for Once; for Weekly/Monthly can use scheduledDate as fallback for "first run" or derive from now.</summary>
        public static DateTime? ComputeNextScheduled(DateTime? scheduledDate, TimeSpan? scheduledTime, MailFrequency? frequency, int? dayOfWeek, int? dayOfMonth, string timezone)
        {
            if (!scheduledTime.HasValue || string.IsNullOrWhiteSpace(timezone)) return null;

            var tz = timezone.Trim();
            var now = DateTime.UtcNow;

            if (!frequency.HasValue || frequency == MailFrequency.Once)
            {
                if (scheduledDate.HasValue)
                    return FirstScheduledUtc(scheduledDate.Value, scheduledTime.Value, tz);
                return null;
            }

            // Recurring: we need the next occurrence at or after now
            if (frequency == MailFrequency.Weekly && dayOfWeek.HasValue)
            {
                // Start from today in the schedule's timezone, find next occurrence of dayOfWeek at time
                return NextRecurringUtc(MailFrequency.Weekly, dayOfWeek, null, scheduledTime.Value, tz, now);
            }

            if (frequency == MailFrequency.Monthly && dayOfMonth.HasValue)
            {
                return NextRecurringUtc(MailFrequency.Monthly, null, dayOfMonth, scheduledTime.Value, tz, now);
            }

            // Fallback: if they had scheduledDate (e.g. from old UI), derive day from it
            if (scheduledDate.HasValue)
            {
                if (frequency == MailFrequency.Weekly)
                {
                    var d = scheduledDate.Value;
                    var dow = (int)d.DayOfWeek;
                    return NextRecurringUtc(MailFrequency.Weekly, dow, null, scheduledTime.Value, tz, now);
                }
                if (frequency == MailFrequency.Monthly)
                {
                    var dom = scheduledDate.Value.Day;
                    return NextRecurringUtc(MailFrequency.Monthly, null, dom, scheduledTime.Value, tz, now);
                }
            }

            return null;
        }
    }
}
