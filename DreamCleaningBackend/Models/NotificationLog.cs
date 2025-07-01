namespace DreamCleaningBackend.Models
{
    public class NotificationLog
    {
        public int Id { get; set; }
        public int OrderId { get; set; }
        public int? CleanerId { get; set; }
        public int? CustomerId { get; set; }
        public string NotificationType { get; set; } // "Assignment", "TwoDayReminder", "FourHourReminder"
        public DateTime SentAt { get; set; }
        public Order Order { get; set; } = null!;
        public User? Cleaner { get; set; }
        public User? Customer { get; set; }
    }
}