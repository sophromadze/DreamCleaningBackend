using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    /// <summary>
    /// A review item produced by an <see cref="AutomationRule"/>. This is the "admin task" the
    /// automation creates instead of messaging the customer — an admin reviews it and decides
    /// whether to reach out. Nothing is sent to the customer by the system.
    /// </summary>
    public class AutomationAlert
    {
        [Key]
        public int Id { get; set; }

        [Required]
        [StringLength(40)]
        public string RuleKey { get; set; } = string.Empty;

        public int UserId { get; set; }

        [ForeignKey("UserId")]
        public virtual User User { get; set; } = null!;

        /// <summary>Snapshot so the feed survives user renames/soft-deletes.</summary>
        [StringLength(200)]
        public string CustomerName { get; set; } = string.Empty;

        /// <summary>Human-readable reason, e.g. "No order in 74 days (last: 2026-04-09).".</summary>
        [StringLength(400)]
        public string Reason { get; set; } = string.Empty;

        /// <summary>Open / Snoozed / Done / Dismissed — see <see cref="AutomationAlertStatus"/>.</summary>
        [Required]
        [StringLength(20)]
        public string Status { get; set; } = AutomationAlertStatus.Open;

        /// <summary>When Status is Snoozed, the date this alert should automatically return to Open
        /// (e.g. the customer asked us to call back in July). Null otherwise.</summary>
        public DateTime? RemindAt { get; set; }

        /// <summary>How many times an admin tried to reach the customer but got no answer.
        /// The alert stays Open so it can be retried; this just records the attempts.</summary>
        public int Attempts { get; set; } = 0;

        /// <summary>When the last "no answer" attempt was logged.</summary>
        public DateTime? LastAttemptAt { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? ResolvedAt { get; set; }
        public int? ResolvedByAdminId { get; set; }

        [StringLength(200)]
        public string? ResolvedByAdminName { get; set; }
    }

    public static class AutomationAlertStatus
    {
        public const string Open = "Open";
        public const string Snoozed = "Snoozed";
        public const string Done = "Done";
        public const string Dismissed = "Dismissed";

        public static readonly string[] All = { Open, Snoozed, Done, Dismissed };
        public static bool IsValid(string? s) => s != null && All.Contains(s);
    }
}
