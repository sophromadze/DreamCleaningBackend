using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    /// <summary>
    /// Admin-submitted order edit that requires SuperAdmin approval before being applied.
    /// </summary>
    public class PendingOrderEdit
    {
        public int Id { get; set; }

        public int OrderId { get; set; }
        public virtual Order Order { get; set; }

        /// <summary>Admin who requested the edit.</summary>
        public int RequestedByUserId { get; set; }
        public virtual User RequestedByUser { get; set; }

        public DateTime RequestedAt { get; set; }

        /// <summary>JSON-serialized SuperAdminUpdateOrderDto (proposed changes).</summary>
        [Required]
        public string ProposedChangesJson { get; set; } = "{}";

        /// <summary>Pending, Approved, Rejected</summary>
        [StringLength(20)]
        public string Status { get; set; } = "Pending";

        /// <summary>SuperAdmin who reviewed (when approved or rejected).</summary>
        public int? ReviewedByUserId { get; set; }
        public virtual User? ReviewedByUser { get; set; }

        public DateTime? ReviewedAt { get; set; }

        [StringLength(500)]
        public string? RejectReason { get; set; }
    }
}
