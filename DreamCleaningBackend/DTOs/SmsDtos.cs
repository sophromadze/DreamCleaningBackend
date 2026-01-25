namespace DreamCleaningBackend.DTOs
{
    public class ScheduledSmsDto
    {
        public int Id { get; set; }
        public string Content { get; set; } = "";
        public string TargetRoles { get; set; } = "[]";
        public int ScheduleType { get; set; }
        public DateTime? ScheduledDate { get; set; }
        public TimeSpan? ScheduledTime { get; set; }
        public int? DayOfWeek { get; set; }
        public int? DayOfMonth { get; set; }
        public int? WeekOfMonth { get; set; }
        public int? Frequency { get; set; }
        public string ScheduleTimezone { get; set; } = "Eastern Standard Time";
        public int Status { get; set; }
        public int CreatedById { get; set; }
        public string? CreatedByEmail { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? SentAt { get; set; }
        public DateTime? LastSentAt { get; set; }
        public DateTime? NextScheduledAt { get; set; }
        public int RecipientCount { get; set; }
        public int TimesSent { get; set; }
        public bool IsActive { get; set; }
    }

    public class CreateScheduledSmsDto
    {
        public string Content { get; set; } = "";
        public string TargetRoles { get; set; } = "[]";
        public int ScheduleType { get; set; }
        public DateTime? ScheduledDate { get; set; }
        public string? ScheduledTime { get; set; }
        public int? DayOfWeek { get; set; }
        public int? DayOfMonth { get; set; }
        public int? WeekOfMonth { get; set; }
        public int? Frequency { get; set; }
        public string ScheduleTimezone { get; set; } = "Eastern Standard Time";
        public bool SendNow { get; set; }
    }

    public class UpdateScheduledSmsDto
    {
        public string? Content { get; set; }
        public string? TargetRoles { get; set; }
        public int? ScheduleType { get; set; }
        public DateTime? ScheduledDate { get; set; }
        public string? ScheduledTime { get; set; }
        public int? DayOfWeek { get; set; }
        public int? DayOfMonth { get; set; }
        public int? WeekOfMonth { get; set; }
        public int? Frequency { get; set; }
        public string? ScheduleTimezone { get; set; }
        public bool? IsActive { get; set; }
    }

    public class SmsStatsDto
    {
        public int DraftCount { get; set; }
        public int ScheduledCount { get; set; }
        public int SentCount { get; set; }
        public int TotalSmsSent { get; set; }
    }

    /// <summary>Per-role counts. WithValidPhone = users with valid 10+ digit phone who can receive SMS.</summary>
    public class SmsUserCountDto
    {
        public string Role { get; set; } = "";
        public int Total { get; set; }
        public int CanReceive { get; set; }
        public int WithValidPhone { get; set; }
    }
}
