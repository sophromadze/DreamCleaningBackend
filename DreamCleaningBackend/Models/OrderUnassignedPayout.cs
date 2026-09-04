using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    /// <summary>
    /// The payout record for a staffing slot on an order that has NO cleaner assigned to it.
    ///
    /// A job staffed for 3 people is 3 payouts, and it stays 3 payouts when only 2 of those people
    /// exist in the system — the third worked their hours and got paid, they are simply not on
    /// file. <see cref="OrderCleaner"/> cannot carry that record: it requires a real Cleaner FK.
    /// So the slot gets its own row here, and the Outgoing Payments page can mark it paid exactly
    /// like any other line.
    ///
    /// Rows are created LAZILY — only when somebody actually acts on a slot. An order with three
    /// unassigned slots nobody has touched has no rows here at all; the slots are still shown and
    /// still counted, because <see cref="Services.CleanerPayrollCalculator"/> derives them from
    /// the staffing count rather than from this table. That keeps the table a record of decisions
    /// rather than a duplicate of arithmetic.
    ///
    /// The figures themselves are NOT stored (bar the frozen paid amount): the hours and rate come
    /// from the order, the same as any un-overridden cleaner line, so changing the order's rate
    /// moves an unpaid slot with it.
    /// </summary>
    public class OrderUnassignedPayout
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public int OrderId { get; set; }

        [ForeignKey("OrderId")]
        public virtual Order Order { get; set; } = null!;

        /// <summary>
        /// Which unassigned slot this is, 0-based, counting after the assigned cleaners. With 3
        /// staffed and 2 assigned there is one slot, index 0.
        ///
        /// If the order's cleaner count later drops, slots at or beyond the new count simply stop
        /// being rendered — the row is left in place rather than deleted, so a payout that really
        /// happened is never silently erased by an unrelated edit.
        /// </summary>
        public int SlotIndex { get; set; }

        public bool IsPaid { get; set; } = false;

        /// <summary>
        /// What has been handed over, frozen at the moment of payment — same rule as
        /// <see cref="OrderCleaner.PaidAmount"/>, top-ups included. Never re-derived on read.
        /// </summary>
        [Column(TypeName = "decimal(18,2)")]
        public decimal? PaidAmount { get; set; }

        public CleanerPaymentMethod? PaidVia { get; set; }

        public DateTime? PaidAt { get; set; }

        public int? PaidByUserId { get; set; }

        [ForeignKey("PaidByUserId")]
        public virtual User? PaidByUser { get; set; }

        /// <summary>
        /// Free text — in practice this is where the person's NAME goes, since there is no cleaner
        /// record to read it from. That is the whole reason this field matters more here than on
        /// an assigned line.
        /// </summary>
        [StringLength(500)]
        public string? PaymentNote { get; set; }
    }
}
