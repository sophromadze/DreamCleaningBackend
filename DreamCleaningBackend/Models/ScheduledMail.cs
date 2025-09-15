// DreamCleaningBackend/Models/ScheduledMail.cs
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.Models
{
    public class ScheduledMail
    {
        public int Id { get; set; }
        
        [Required]
        [MaxLength(200)]
        public string Subject { get; set; }
        
        [Required]
        public string Content { get; set; }
        
        [Required]
        public string TargetRoles { get; set; } // JSON array of role names
        
        [Required]
        public ScheduleType ScheduleType { get; set; }
        
        public DateTime? ScheduledDate { get; set; }
        
        public TimeSpan? ScheduledTime { get; set; }
        
        public int? DayOfWeek { get; set; } // 0-6 (Sunday-Saturday)
        
        public int? DayOfMonth { get; set; } // 1-31
        
        public int? WeekOfMonth { get; set; } // 1-4
        
        public Frequency? Frequency { get; set; }
        
        [Required]
        public MailStatus Status { get; set; }
        
        [MaxLength(100)]
        public string ScheduleTimezone { get; set; } // Added timezone field (e.g., "America/New_York", "Asia/Tbilisi")
        
        public int CreatedById { get; set; }
        public User CreatedBy { get; set; }
        
        public DateTime CreatedAt { get; set; }
        
        public DateTime? SentAt { get; set; }
        
        public DateTime? LastSentAt { get; set; }
        
        public DateTime? NextScheduledAt { get; set; }
        
        public int RecipientCount { get; set; }
        
        public int TimesSent { get; set; }
        
        public DateTime UpdatedAt { get; set; }
        
        public bool IsActive { get; set; }
        
        // Navigation property for sent mail logs
        public ICollection<SentMailLog> SentMailLogs { get; set; }
    }
    
    public enum ScheduleType
    {
        Immediate,
        Scheduled
    }
    
    public enum Frequency
    {
        Once,
        Weekly,
        Monthly
    }
    
    public enum MailStatus
    {
        Draft,
        Scheduled,
        Sent,
        Cancelled,
        Failed
    }
    
    public class SentMailLog
    {
        public int Id { get; set; }
        
        public int ScheduledMailId { get; set; }
        public ScheduledMail ScheduledMail { get; set; }
        
        public string RecipientEmail { get; set; }
        
        public string RecipientName { get; set; }
        
        public string RecipientRole { get; set; }
        
        public DateTime SentAt { get; set; }
        
        public bool IsDelivered { get; set; }
        
        public string ErrorMessage { get; set; }
    }
}