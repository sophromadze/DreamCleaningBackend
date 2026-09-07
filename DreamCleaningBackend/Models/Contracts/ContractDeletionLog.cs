using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.Models.Contracts
{
    /// <summary>
    /// The record that a contract was PERMANENTLY deleted, written immediately before the row and
    /// everything under it are removed.
    ///
    /// It exists because the obvious place for that record cannot hold it: <see cref="ContractAuditLog"/>
    /// rows are keyed to the contract and cascade away with it, so the audit trail of a permanent
    /// deletion would be destroyed by the very act it describes. This table has no foreign key to
    /// the contract for the same reason — it deliberately outlives it.
    ///
    /// Kept minimal: enough to answer "was DC-2026-000X permanently deleted, and when", not a
    /// copy of the contract. Anyone needing the document itself should have restored it during
    /// the retention window.
    /// </summary>
    public class ContractDeletionLog
    {
        public long Id { get; set; }

        /// <summary>The human-facing identifier, which is how anyone would ask about it later.</summary>
        [Required, StringLength(20)]
        public string ContractNumber { get; set; } = string.Empty;

        /// <summary>The original row id, for correlating against anything that referenced it.</summary>
        public int ContractId { get; set; }

        [StringLength(200)]
        public string? ClientLegalName { get; set; }

        /// <summary>Status the contract was in when it was hidden — an executed contract being
        /// purged is a different fact from a draft being purged.</summary>
        [StringLength(40)]
        public string? StatusAtDeletion { get; set; }

        /// <summary>When it was soft-deleted, i.e. when the retention clock started.</summary>
        public DateTime? HiddenAt { get; set; }

        [StringLength(255)]
        public string? HiddenBy { get; set; }

        /// <summary>When the permanent deletion actually happened.</summary>
        public DateTime DeletedAt { get; set; } = DateTime.UtcNow;

        /// <summary>How many versions/signatures/files went with it, for a sanity check later.</summary>
        public int VersionCount { get; set; }
        public int SignatureCount { get; set; }
        public int FileCount { get; set; }

        /// <summary>Always the retention job today; a column so a manual purge could say so.</summary>
        [Required, StringLength(60)]
        public string DeletedBy { get; set; } = "Retention job";
    }
}
