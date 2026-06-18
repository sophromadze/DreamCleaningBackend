namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// Business timezone for the whole app: New York (America/New_York).
    /// All scheduling comparisons (reminders, "today", time-of-day windows) must use
    /// this instead of DateTime.UtcNow, because Order.ServiceDate / ServiceTime are
    /// stored as NY wall-clock values while CreatedAt-style audit timestamps are UTC.
    /// </summary>
    public static class NyTimeHelper
    {
        // IANA id works on Linux (production VPS); the Windows id is a fallback for dev boxes
        // without ICU-based timezone data. Same pattern as LoyaltyReengagementService.
        private static readonly Lazy<TimeZoneInfo> Ny = new(() =>
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
            catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
        });

        public static TimeZoneInfo TimeZone => Ny.Value;

        /// <summary>Current wall-clock time in New York.</summary>
        public static DateTime NowNy => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Ny.Value);

        /// <summary>Converts a UTC timestamp (Kind Utc or Unspecified-but-UTC, as stored in the DB) to NY wall-clock time.</summary>
        public static DateTime ToNy(DateTime utc) =>
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Ny.Value);

        /// <summary>Converts a NY wall-clock time to UTC.</summary>
        public static DateTime ToUtc(DateTime nyLocal) =>
            TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(nyLocal, DateTimeKind.Unspecified), Ny.Value);
    }
}
