using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.Models
{
    /// <summary>
    /// Scheduled or sent SMS campaign by target roles. Reuses ScheduleType, MailFrequency, MailStatus.
    /// Only users with a valid 10+ digit phone (NormalizePhoneToE164 != null) receive SMS.
    /// </summary>
    public class ScheduledSms
    {
        public int Id { get; set; }

        [Required]
        [StringLength(1600)]
        public string Content { get; set; } = "";

        /// <summary>JSON array of role names, e.g. ["Customer","Cleaner","Admin"].</summary>
        [Required]
        public string TargetRoles { get; set; } = "[]";

        public ScheduleType ScheduleType { get; set; }
        public DateTime? ScheduledDate { get; set; }
        public TimeSpan? ScheduledTime { get; set; }
        public int? DayOfWeek { get; set; }
        public int? DayOfMonth { get; set; }
        public int? WeekOfMonth { get; set; }
        public MailFrequency? Frequency { get; set; }

        [Required]
        [StringLength(100)]
        public string ScheduleTimezone { get; set; } = "UTC";

        public MailStatus Status { get; set; }
        public int CreatedById { get; set; }
        public virtual User CreatedBy { get; set; } = null!;
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? SentAt { get; set; }
        public DateTime? LastSentAt { get; set; }
        public DateTime? NextScheduledAt { get; set; }
        /// <summary>Number of users with valid phone (10+ digits) in target roles who can receive.</summary>
        public int RecipientCount { get; set; }
        public int TimesSent { get; set; }
        public bool IsActive { get; set; } = true;
    }
}
