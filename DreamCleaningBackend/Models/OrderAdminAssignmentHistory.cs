using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    // Audit trail of every change to Order.AssignedAdminId. The current assignee on the
    // Order row determines bonus credit; this table exists so statistics and disputes can
    // see who held an order over time even after a reassignment.
    public class OrderAdminAssignmentHistory
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public int OrderId { get; set; }

        [ForeignKey("OrderId")]
        public virtual Order Order { get; set; } = null!;

        // Null when the order had no assignee before this change (first assignment).
        public int? PreviousAdminId { get; set; }

        [ForeignKey("PreviousAdminId")]
        public virtual User? PreviousAdmin { get; set; }

        // Null when the assignee was cleared (set back to unassigned).
        public int? NewAdminId { get; set; }

        [ForeignKey("NewAdminId")]
        public virtual User? NewAdmin { get; set; }

        [Required]
        public int ChangedByUserId { get; set; }

        [ForeignKey("ChangedByUserId")]
        public virtual User ChangedByUser { get; set; } = null!;

        public DateTime ChangedAt { get; set; } = DateTime.UtcNow;

        // Snapshot of the bonus rate in effect at the time of this change. Lets future
        // statistics reconstruct what an admin was entitled to earn without re-reading
        // the current setting (which may have changed since).
        [Column(TypeName = "decimal(18,2)")]
        public decimal BonusRateAtChange { get; set; }
    }
}
