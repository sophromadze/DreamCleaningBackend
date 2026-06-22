using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.Models
{
    /// <summary>
    /// A CRM retention-automation rule. v1 is intentionally NON-sending: when a rule triggers it
    /// creates an <see cref="AutomationAlert"/> (an admin review item) — it never emails/SMSes a
    /// customer. The action mode is fixed to "AdminTask" for now; an auto-send mode can be added
    /// later behind an explicit per-rule opt-in.
    /// </summary>
    public class AutomationRule
    {
        [Key]
        public int Id { get; set; }

        /// <summary>Stable rule identifier, e.g. "winback". Unique.</summary>
        [Required]
        [StringLength(40)]
        public string Key { get; set; } = string.Empty;

        [Required]
        [StringLength(120)]
        public string Name { get; set; } = string.Empty;

        [StringLength(400)]
        public string? Description { get; set; }

        /// <summary>Ships OFF — an admin enables it from the Automation tab.</summary>
        public bool IsEnabled { get; set; } = false;

        /// <summary>Days since last order before a customer is flagged (win-back boundary). Default 21 (3 weeks).</summary>
        public int ThresholdDays { get; set; } = 21;

        /// <summary>Don't re-flag the same customer for this rule within this many days. Default 40.</summary>
        public int CooldownDays { get; set; } = 40;

        /// <summary>What the rule does on trigger. Fixed to "AdminTask" in v1 (no sending).</summary>
        [StringLength(20)]
        public string Action { get; set; } = AutomationAction.AdminTask;

        public DateTime? LastRunAt { get; set; }

        /// <summary>How many alerts the most recent evaluation created (for the UI's run feedback).</summary>
        public int LastRunCreatedCount { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    public static class AutomationRuleKeys
    {
        public const string Winback = "winback";
    }

    public static class AutomationAction
    {
        /// <summary>Create an admin review alert. Never contacts the customer.</summary>
        public const string AdminTask = "AdminTask";
    }
}
