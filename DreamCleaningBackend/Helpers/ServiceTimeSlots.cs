using System;
using System.Collections.Generic;
using System.Linq;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// Service start times — the one place that answers "which hours may this person book?".
    ///
    /// Two audiences, two different windows:
    ///
    /// - <b>Customers</b> start between 8:00 AM and 6:00 PM, and no earlier than 9:30 AM on
    ///   Saturday and Sunday. That is the public booking window.
    /// - <b>Admins / SuperAdmins</b> (never Moderators) run to <b>8:00 PM</b> and are not held to
    ///   the weekend 9:30 floor — they enter jobs agreed by phone that the self-service window
    ///   cannot express, including evening cleanings.
    ///
    /// Mirrored by <c>DreamCleaningNG/src/app/shared/booking/service-time-slots.ts</c>, which is
    /// what the booking page and the order-edit page actually render. <b>Change both together.</b>
    /// </summary>
    public static class ServiceTimeSlots
    {
        /// <summary>Earliest start on a weekday, and the floor admins get on every day.</summary>
        public const string EarliestStartTime = "08:00";

        /// <summary>Earliest start a customer gets on Saturday or Sunday.</summary>
        public const string WeekendEarliestStartTime = "09:30";

        /// <summary>Latest start a customer may pick, any day.</summary>
        public const string CustomerLatestStartTime = "18:00";

        /// <summary>Latest start an Admin/SuperAdmin may pick, any day.</summary>
        public const string AdminLatestStartTime = "20:00";

        private const int SlotIntervalMinutes = 30;

        /// <summary>The last start time this audience may pick.</summary>
        public static string GetLatestStartTime(bool isAdmin)
            => isAdmin ? AdminLatestStartTime : CustomerLatestStartTime;

        /// <summary>
        /// The first start time available on <paramref name="date"/>. The weekend 9:30 floor is a
        /// customer rule only — an admin booking a Saturday morning job gets 8:00 like any day.
        /// </summary>
        public static string GetEarliestStartTimeForDate(DateTime date, bool isAdmin)
        {
            if (isAdmin)
                return EarliestStartTime;

            var isWeekend = date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday;
            return isWeekend ? WeekendEarliestStartTime : EarliestStartTime;
        }

        /// <summary>
        /// Every 30-minute slot this audience may pick, ignoring the day of the week.
        /// </summary>
        public static List<string> BuildAll(bool isAdmin)
        {
            var slots = new List<string>();
            var last = ToMinutes(GetLatestStartTime(isAdmin));
            for (var minutes = ToMinutes(EarliestStartTime); minutes <= last; minutes += SlotIntervalMinutes)
                slots.Add(ToTimeString(minutes));
            return slots;
        }

        /// <summary>
        /// The slots offered for one date: the audience's window, narrowed by the weekend floor.
        /// </summary>
        public static List<string> BuildForDate(DateTime date, bool isAdmin)
        {
            var earliest = GetEarliestStartTimeForDate(date, isAdmin);
            return BuildAll(isAdmin)
                .Where(t => string.Compare(t, earliest, StringComparison.Ordinal) >= 0)
                .ToList();
        }

        private static int ToMinutes(string time)
        {
            var parts = time.Split(':');
            return int.Parse(parts[0]) * 60 + int.Parse(parts[1]);
        }

        private static string ToTimeString(int totalMinutes)
            => $"{totalMinutes / 60:D2}:{totalMinutes % 60:D2}";
    }
}
