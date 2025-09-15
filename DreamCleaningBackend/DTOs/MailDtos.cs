// DreamCleaningBackend/DTOs/MailDtos.cs
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.DTOs
{
    public class ScheduledMailDto
    {
        public int Id { get; set; }
        public string Subject { get; set; }
        public string Content { get; set; }
        public List<string> TargetRoles { get; set; }
        public string ScheduleType { get; set; }
        public DateTime? ScheduledDate { get; set; }
        public string ScheduledTime { get; set; }
        public int? DayOfWeek { get; set; }
        public int? DayOfMonth { get; set; }
        public int? WeekOfMonth { get; set; }
        public string Frequency { get; set; }
        public string Status { get; set; }
        public string ScheduleTimezone { get; set; } // Added timezone field
        public string CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? SentAt { get; set; }
        public int RecipientCount { get; set; }
        public int TimesSent { get; set; }
        public List<SentMailLogDto> SentLogs { get; set; }
    }

    public class CreateScheduledMailDto
    {
        [Required]
        [MaxLength(200)]
        public string Subject { get; set; }
        
        [Required]
        public string Content { get; set; }
        
        [Required]
        public List<string> TargetRoles { get; set; }
        
        [Required]
        public string ScheduleType { get; set; }
        
        public DateTime? ScheduledDate { get; set; }
        
        public string ScheduledTime { get; set; }
        
        public int? DayOfWeek { get; set; }
        
        public int? DayOfMonth { get; set; }
        
        public int? WeekOfMonth { get; set; }
        
        public string Frequency { get; set; }
        
        [Required]
        public string Status { get; set; }
        
        public string ScheduleTimezone { get; set; } // Added timezone field
    }

    public class UpdateScheduledMailDto
    {
        [Required]
        [MaxLength(200)]
        public string Subject { get; set; }
        
        [Required]
        public string Content { get; set; }
        
        [Required]
        public List<string> TargetRoles { get; set; }
        
        [Required]
        public string ScheduleType { get; set; }
        
        public DateTime? ScheduledDate { get; set; }
        
        public string ScheduledTime { get; set; }
        
        public int? DayOfWeek { get; set; }
        
        public int? DayOfMonth { get; set; }
        
        public int? WeekOfMonth { get; set; }
        
        public string Frequency { get; set; }
        
        [Required]
        public string Status { get; set; }
        
        public string ScheduleTimezone { get; set; } // Added timezone field
    }

    public class SentMailLogDto
    {
        public string RecipientEmail { get; set; }
        public string RecipientName { get; set; }
        public string RecipientRole { get; set; }
        public DateTime SentAt { get; set; }
        public bool IsDelivered { get; set; }
        public string ErrorMessage { get; set; }
    }

    public class MailStatsDto
    {
        public int TotalSent { get; set; }
        public int ScheduledCount { get; set; }
        public int DraftCount { get; set; }
        public List<RecipientByRoleDto> RecipientsByRole { get; set; }
        public List<SentByMonthDto> SentByMonth { get; set; }
    }

    public class RecipientByRoleDto
    {
        public string Role { get; set; }
        public int Count { get; set; }
    }

    public class SentByMonthDto
    {
        public string Month { get; set; }
        public int Count { get; set; }
    }
}