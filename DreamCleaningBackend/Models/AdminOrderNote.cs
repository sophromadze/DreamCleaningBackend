using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.Models
{
    /// <summary>
    /// Free-text internal note an admin can attach to a single order (shown in the admin order
    /// details panel, above Assigned Cleaners).
    ///
    /// Stored in its own table rather than as an Order column for two reasons: the Order entity is
    /// re-priced and rewritten by several update paths that would have to learn to carry a field
    /// they have no business touching, and — like refunds — this is admin-only text that must never
    /// reach OrderDtoMapper.ToOrderDto, which is the SHARED shape behind the customer's own
    /// order-details page.
    /// </summary>
    public class AdminOrderNote
    {
        public int OrderId { get; set; }
        public virtual Order Order { get; set; } = null!;

        [StringLength(2000)]
        public string? Notes { get; set; }

        public DateTime? UpdatedAt { get; set; }

        /// <summary>Admin who last saved the note. Nullable so a deleted admin doesn't take the note with them.</summary>
        public int? UpdatedByUserId { get; set; }
    }
}
