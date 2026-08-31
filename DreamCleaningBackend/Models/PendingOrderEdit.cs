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

        /// <summary>
        /// JSON-serialized SuperAdminUpdateOrderDto — the payload that is actually APPLIED when
        /// the request is approved. Kept: the approve path replays it through
        /// <c>SuperAdminFullUpdateOrder</c>, so it is the executable half of the request.
        /// </summary>
        [Required]
        public string ProposedChangesJson { get; set; } = "{}";

        /// <summary>
        /// The READABLE half: a field-level list of what was asked for, each entry carrying the
        /// field label, the value AT SUBMIT TIME and the requested value. JSON array of
        /// <c>PendingOrderEditFieldChangeDto</c>.
        ///
        /// Why this exists alongside ProposedChangesJson (2026-08-31): the DTO blob is only
        /// meaningful when diffed against an order, so a reviewer could only ever be shown "the
        /// request versus the order as it is NOW". If the order moved between submission and
        /// review — somebody else edited it, or a re-price ran — the reviewer silently saw a
        /// different request from the one that was made. Storing the submit-time values makes
        /// that drift detectable and lets the request be read back after it is decided, when the
        /// order has moved on for good.
        ///
        /// NULLABLE on purpose. Rows submitted before this column existed (order #296's among
        /// them) have no payload, and the review screen must degrade to "the original request
        /// could not be read" rather than fail.
        /// </summary>
        [Column(TypeName = "LONGTEXT")]
        public string? FieldChangesJson { get; set; }

        /// <summary>Why the requesting admin says the change is needed. Optional, free text.</summary>
        [StringLength(1000)]
        public string? Reason { get; set; }

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
