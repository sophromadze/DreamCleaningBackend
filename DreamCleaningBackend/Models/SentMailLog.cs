namespace DreamCleaningBackend.Models
{
    public class SentMailLog
    {
        public int Id { get; set; }
        public int ScheduledMailId { get; set; }
        public virtual ScheduledMail ScheduledMail { get; set; } = null!;
        public string RecipientEmail { get; set; } = "";
        public string RecipientName { get; set; } = "";
        public string RecipientRole { get; set; } = "";
        public DateTime SentAt { get; set; }
        public bool IsDelivered { get; set; }
        /// <summary>Empty string when no error.</summary>
        public string ErrorMessage { get; set; } = "";
    }
}
