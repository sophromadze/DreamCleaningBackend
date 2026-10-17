using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    /// <summary>
    /// A previewed change waiting to be confirmed (2026-10). Every write the Claude connector
    /// makes — and every Revert, from the connector or the Audits tab — is two calls: a preview
    /// that writes nothing and hands back a short-lived token, and an apply that takes the token.
    /// See <see cref="Services.ChangeSets.ChangeSetService"/>.
    ///
    /// The token itself is never stored: <see cref="TokenHash"/> is its SHA-256, same rule as the
    /// connector's OAuth tokens. A row is single-use (<see cref="ConsumedAt"/> is claimed with a
    /// conditional UPDATE before anything runs), so a rejected apply still uses the token up and a
    /// new preview is needed.
    /// </summary>
    public class PendingChangeSet
    {
        public long Id { get; set; }

        [Required, StringLength(64)]
        public string TokenHash { get; set; } = string.Empty;

        /// <summary>Who previewed it; only the same person may apply it.</summary>
        public int UserId { get; set; }

        /// <summary>The connector grant it was previewed through; null = the admin panel. Must match on apply.</summary>
        public int? McpGrantId { get; set; }

        /// <summary><see cref="Services.ChangeSets.ChangeSetKinds"/>.</summary>
        [Required, StringLength(32)]
        public string Kind { get; set; } = string.Empty;

        /// <summary>The request, replayed verbatim on apply.</summary>
        [Column(TypeName = "LONGTEXT")]
        public string RequestJson { get; set; } = "{}";

        /// <summary>What the preview showed: the records it touches, their fingerprints and the expected changes.</summary>
        [Column(TypeName = "LONGTEXT")]
        public string PreviewJson { get; set; } = "{}";

        /// <summary>The audit BatchId the applied change will be written under (also shown in the preview).</summary>
        [Required, StringLength(36)]
        public string BatchId { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public DateTime? ConsumedAt { get; set; }

        /// <summary>applied / rejected: … — what happened when the token was used.</summary>
        [StringLength(500)]
        public string? Outcome { get; set; }
    }
}
