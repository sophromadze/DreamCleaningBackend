using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.DTOs
{
    public class AutomationRuleDto
    {
        public int Id { get; set; }
        public string Key { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public bool IsEnabled { get; set; }
        public int ThresholdDays { get; set; }
        public int CooldownDays { get; set; }
        public string Action { get; set; } = string.Empty;
        public DateTime? LastRunAt { get; set; }
        public int LastRunCreatedCount { get; set; }
        public int OpenAlertCount { get; set; }
    }

    public class UpdateAutomationRuleDto
    {
        public bool? IsEnabled { get; set; }

        [Range(1, 1000)]
        public int? ThresholdDays { get; set; }

        [Range(1, 1000)]
        public int? CooldownDays { get; set; }
    }

    public class AutomationAlertDto
    {
        public int Id { get; set; }
        public string RuleKey { get; set; } = string.Empty;
        public int UserId { get; set; }
        public string CustomerName { get; set; } = string.Empty;
        public string? CustomerEmail { get; set; }
        public string? CustomerPhone { get; set; }
        public decimal CustomerLifetimeValue { get; set; }
        public DateTime? LastOrderDate { get; set; }
        public string Reason { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public DateTime? RemindAt { get; set; }
        public int Attempts { get; set; }
        public DateTime? LastAttemptAt { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? ResolvedAt { get; set; }
        public string? ResolvedByAdminName { get; set; }
    }

    public class UpdateAutomationAlertDto
    {
        [Required]
        [StringLength(20)]
        public string Status { get; set; } = string.Empty;

        /// <summary>Required when Status is "Snoozed": the date the alert should reappear as Open.</summary>
        public DateTime? RemindAt { get; set; }
    }

    public class AutomationSummaryDto
    {
        public int OpenAlerts { get; set; }
    }
}
