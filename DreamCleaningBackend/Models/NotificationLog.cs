namespace DreamCleaningBackend.Models
{
    public class NotificationLog
    {
        public int Id { get; set; }
        // Nullable so loyalty reminders (which aren't tied to a single order) can use this table
        // as the idempotency source. Order-scoped notifications (Assignment, TwoDayReminder,
        // FourHourReminder) still set OrderId. Loyalty reminders set OrderId=null, CustomerId=user.
        public int? OrderId { get; set; }
        public int? CleanerId { get; set; }
        public int? CustomerId { get; set; }
        public string NotificationType { get; set; } = string.Empty;
        public DateTime SentAt { get; set; }
        public Order? Order { get; set; }
        public Cleaner? Cleaner { get; set; }
        public User? Customer { get; set; }
    }

    // Canonical string constants for NotificationLog.NotificationType. Keep these stable —
    // they're queried by the background services for idempotency checks and would orphan
    // existing log rows if renamed.
    public static class NotificationTypes
    {
        public const string Assignment = "Assignment";
        public const string TwoDayReminder = "TwoDayReminder";
        public const string FourHourReminder = "FourHourReminder";
        public const string LoyaltyReminder30 = "LoyaltyReminder30";
        public const string LoyaltyReminder60 = "LoyaltyReminder60";
        public const string LoyaltyReminder90 = "LoyaltyReminder90";
        // Admin-triggered "we miss you" reminder (Send Reminder button in the users panel).
        // Same copy as LoyaltyReminder30; logged separately so we know who/what sent it.
        public const string ManualReminder = "ManualReminder";
    }
}