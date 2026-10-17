using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.Models
{
    public class AuditLog
    {
        [Key]
        public long Id { get; set; }

        [Required]
        [StringLength(50)]
        public string EntityType { get; set; } // Like "User", "Order", "GiftCard"

        [Required]
        public long EntityId { get; set; } // The ID of the thing that changed

        [Required]
        [StringLength(128)]
        public string Action { get; set; } // "Create", "Update", "Delete"

        [Column(TypeName = "LONGTEXT")]
        public string? OldValues { get; set; } // What it was before (as JSON)

        [Column(TypeName = "LONGTEXT")]
        public string? NewValues { get; set; } // What it is now (as JSON)

        [Column(TypeName = "LONGTEXT")]
        public string? ChangedFields { get; set; } // Which fields changed (as JSON)

        public int? UserId { get; set; } // Who made the change
        public virtual User? User { get; set; }

        [StringLength(45)]
        public string? IpAddress { get; set; }

        [StringLength(500)]
        public string? UserAgent { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Undo/redo state. UndoneAt is set when an admin reverts this change; null = the change
        // is currently applied. Redo simply re-applies the change and clears UndoneAt. Stored as
        // a single nullable timestamp rather than a state enum because it doubles as "when".
        public DateTime? UndoneAt { get; set; }

        // ── Change history (2026-10) ─────────────────────────────────────────────────────────
        // Written by AuditCaptureInterceptor for the entity types in AuditCaptureScope (the
        // Services catalogue, subscriptions and expenses). Rows written before 2026-10 leave all
        // five null, and every reader treats null as "not recorded" rather than guessing.

        /// <summary>Where the change came from: <see cref="AuditSources"/> (mcp / admin / system).</summary>
        [StringLength(16)]
        public string? Source { get; set; }

        /// <summary>The actor's name AS IT WAS when the change was made (UserId still links the account).</summary>
        [StringLength(200)]
        public string? ActorName { get; set; }

        /// <summary>Groups the rows one confirmed change wrote (a batch of price edits, a cascade delete).</summary>
        [StringLength(36)]
        public string? BatchId { get; set; }

        /// <summary>On a "Revert" row: the history row whose change it reverted.</summary>
        public long? RevertOfId { get; set; }

        /// <summary>Optional note the person typed when confirming the change.</summary>
        [StringLength(500)]
        public string? Note { get; set; }
    }

    /// <summary>The values <see cref="AuditLog.Source"/> takes.</summary>
    public static class AuditSources
    {
        public const string Mcp = "mcp";
        public const string Admin = "admin";
        public const string System = "system";
    }
}
