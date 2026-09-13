using System;
using System.Linq;
using DreamCleaningBackend.Helpers;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// The booking window, which is not one window but two.
    ///
    /// A customer books 8:00 AM - 6:00 PM, and no earlier than 9:30 AM at the weekend. An
    /// Admin/SuperAdmin books 8:00 AM - 8:00 PM on every day, weekend included, because they
    /// enter jobs agreed by phone that the self-service window cannot express.
    ///
    /// This file is the backend half of a mirrored pair — the frontend half is
    /// <c>shared/booking/service-time-slots.spec.ts</c>, and the two assert the same boundaries
    /// deliberately. A window that differs between the picker and the API is a crew sent at an
    /// hour nobody agreed to.
    /// </summary>
    public class ServiceTimeSlotsTests
    {
        // 2026-09-14 is a Monday; 2026-09-19 a Saturday; 2026-09-20 a Sunday.
        private static readonly DateTime Weekday = new DateTime(2026, 9, 14);
        private static readonly DateTime Saturday = new DateTime(2026, 9, 19);
        private static readonly DateTime Sunday = new DateTime(2026, 9, 20);

        [Fact]
        public void Customer_Weekday_RunsFromEightToSix()
        {
            var slots = ServiceTimeSlots.BuildForDate(Weekday, isAdmin: false);

            Assert.Equal("08:00", slots.First());
            Assert.Equal("18:00", slots.Last());
            Assert.DoesNotContain("18:30", slots);
            // Half-hour steps, nothing skipped: 8:00 through 18:00 inclusive is 21 slots.
            Assert.Equal(21, slots.Count);
        }

        [Theory]
        [InlineData(19)] // Saturday
        [InlineData(20)] // Sunday
        public void Customer_Weekend_StartsAtNineThirty(int dayOfMonth)
        {
            var slots = ServiceTimeSlots.BuildForDate(new DateTime(2026, 9, dayOfMonth), isAdmin: false);

            Assert.Equal("09:30", slots.First());
            Assert.DoesNotContain("09:00", slots);
            Assert.DoesNotContain("08:00", slots);
            Assert.Equal("18:00", slots.Last());
        }

        [Fact]
        public void Admin_RunsToEightPm()
        {
            var slots = ServiceTimeSlots.BuildForDate(Weekday, isAdmin: true);

            Assert.Equal("08:00", slots.First());
            // The four half-hours a customer never sees.
            Assert.Contains("18:30", slots);
            Assert.Contains("19:00", slots);
            Assert.Contains("19:30", slots);
            Assert.Equal("20:00", slots.Last());
            // 8:00 PM is a START time, not a half-hour before closing — nothing follows it.
            Assert.DoesNotContain("20:30", slots);
        }

        [Fact]
        public void Admin_IsNotHeldToTheWeekendFloor()
        {
            // The 9:30 floor is a customer rule. An admin taking a Saturday morning job by phone
            // must be able to enter the 8:00 AM the customer agreed to.
            foreach (var weekend in new[] { Saturday, Sunday })
            {
                var slots = ServiceTimeSlots.BuildForDate(weekend, isAdmin: true);
                Assert.Equal("08:00", slots.First());
                Assert.Equal("20:00", slots.Last());
            }
        }

        [Fact]
        public void BuildAll_IgnoresTheDayButNotTheAudience()
        {
            // Used where a whole day has to be enumerated (marking a fully blocked date busy),
            // so it carries the audience's ceiling and no weekend floor.
            Assert.Equal("18:00", ServiceTimeSlots.BuildAll(isAdmin: false).Last());
            Assert.Equal("20:00", ServiceTimeSlots.BuildAll(isAdmin: true).Last());
            Assert.Equal("08:00", ServiceTimeSlots.BuildAll(isAdmin: false).First());
        }

        [Fact]
        public void EverySlotIsOnAHalfHour()
        {
            foreach (var slot in ServiceTimeSlots.BuildAll(isAdmin: true))
            {
                var minute = int.Parse(slot.Split(':')[1]);
                Assert.True(minute == 0 || minute == 30, $"{slot} is not on a half hour");
            }
        }
    }
}
