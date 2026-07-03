using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    /// <summary>
    /// SuperAdmin-only reassignment of an order from one user to another (e.g. moving a
    /// cash-customer order booked under a staff account onto the customer's own account).
    /// SnapshotJson stores everything needed for an exact undo: the order's pre-transfer
    /// owner/contact/apartment fields, the reward deltas applied to both users, the ids of
    /// moved BubblePointsHistory rows and photos, and any apartment created on the target.
    /// </summary>
    public class OrderTransfer
    {
        public int Id { get; set; }

        public int OrderId { get; set; }
        [ForeignKey("OrderId")]
        public virtual Order Order { get; set; } = null!;

        public int FromUserId { get; set; }
        [ForeignKey("FromUserId")]
        public virtual User FromUser { get; set; } = null!;

        public int ToUserId { get; set; }
        [ForeignKey("ToUserId")]
        public virtual User ToUser { get; set; } = null!;

        public int TransferredByUserId { get; set; }

        [StringLength(500)]
        public string? Notes { get; set; }

        /// <summary>JSON snapshot (OrderTransferSnapshot) captured before the transfer ran.</summary>
        [Required]
        public string SnapshotJson { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public bool IsUndone { get; set; } = false;
        public DateTime? UndoneAt { get; set; }
        public int? UndoneByUserId { get; set; }
    }
}
