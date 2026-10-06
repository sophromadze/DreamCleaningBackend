using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    /// <summary>
    /// ONE PAYMENT TO ONE CLEANER, and exactly which order lines it covered (2026-10).
    ///
    /// Before this existed, "paid" lived only on <see cref="OrderCleaner"/> as a flag and a running
    /// total, so the page could say a line was settled but not WHEN it was settled together with
    /// what else, by whom, or by which transfer — and undoing a payment simply erased it. A payout
    /// is that missing record: a Zelle transfer to Ana on the 14th covering six cleanings is one
    /// row here and six <see cref="CleanerPayoutLine"/>s.
    ///
    /// Rules that hold it together:
    ///  - <b>Every payment creates one</b>, whichever screen it came from: the Unpaid cleaners
    ///    bulk action, or a single line / whole order on the Orders tab. One ledger, so the three
    ///    tabs cannot disagree.
    ///  - <b>History is never deleted.</b> A mistake is REVERTED (<see cref="RevertedAt"/>, and
    ///    per line on <see cref="CleanerPayoutLine.RevertedAt"/>), which moves the amounts back to
    ///    unpaid and leaves this row readable forever.
    ///  - <b><see cref="OrderCleaner.PaidAmount"/> is a cache of the active lines.</b> It stays
    ///    because the payroll settlement rule (<c>CleanerPayoutSettlement</c>) and every existing
    ///    reader use it, but it is only ever rewritten from these rows, inside the same
    ///    transaction that changes them.
    ///
    /// Amounts are what was HANDED OVER at the time, frozen like every payment record: a later
    /// change to the order's hours produces a new top-up, never a rewrite of this row.
    /// </summary>
    public class CleanerPayout
    {
        public int Id { get; set; }

        /// <summary>No cascade: a payout outlives a cleaner being deactivated, and must.</summary>
        public int CleanerId { get; set; }

        [ForeignKey("CleanerId")]
        public virtual Cleaner? Cleaner { get; set; }

        /// <summary>Who was paid, as the name read at the time — history stays readable after a rename.</summary>
        [Required, StringLength(200)]
        public string CleanerName { get; set; } = string.Empty;

        /// <summary>The sum of every line as recorded. What is still standing is the sum of the ACTIVE lines.</summary>
        [Column(TypeName = "decimal(18,2)")]
        public decimal Amount { get; set; }

        /// <summary>
        /// The day the money went out, as the admin states it (defaults to today, New York).
        /// Date-only in intent. <see cref="CreatedAt"/> is when it was RECORDED, which is not
        /// necessarily the same day.
        /// </summary>
        public DateTime PaidOn { get; set; }

        /// <summary>How it was sent. Null means "each cleaner's own saved method" was not known.</summary>
        public CleanerPaymentMethod? PaidVia { get; set; }

        /// <summary>The transfer's own reference — a Zelle confirmation, a check number.</summary>
        [StringLength(200)]
        public string? Reference { get; set; }

        [StringLength(500)]
        public string? Note { get; set; }

        /// <summary>The admin who recorded it. Null on an imported historical payout with nobody on record.</summary>
        public int? CreatedByUserId { get; set; }

        [ForeignKey("CreatedByUserId")]
        public virtual User? CreatedByUser { get; set; }

        /// <summary>The recorder's name, captured so a deleted account does not blank out history.</summary>
        [StringLength(200)]
        public string? CreatedByName { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Rebuilt from payment data that existed before payouts were recorded (the startup
        /// backfill). Its lines are what the order lines said they had been paid, not a
        /// reconstruction of individual transfers.
        /// </summary>
        public bool IsImported { get; set; }

        /// <summary>
        /// Set when the WHOLE payout was reverted (privileged, audit-logged). A partly reverted
        /// payout — one line undone from the Orders tab — keeps this null; its lines say which.
        /// </summary>
        public DateTime? RevertedAt { get; set; }

        public int? RevertedByUserId { get; set; }

        [ForeignKey("RevertedByUserId")]
        public virtual User? RevertedByUser { get; set; }

        [StringLength(500)]
        public string? RevertReason { get; set; }

        /// <summary>
        /// UNIQUE (filtered to non-null). The confirmation dialog's one-time key, suffixed with the
        /// cleaner: a double-click or a retried request after a timeout finds the payout it already
        /// made instead of recording the same money twice.
        /// </summary>
        [StringLength(100)]
        public string? IdempotencyKey { get; set; }

        public virtual ICollection<CleanerPayoutLine> Lines { get; set; } = new List<CleanerPayoutLine>();
    }

    /// <summary>
    /// One order line a payout covered, and how much of it. Several lines for the same
    /// <see cref="OrderCleanerId"/> are normal over time: the first payment, then a top-up after
    /// the hours grew.
    /// </summary>
    public class CleanerPayoutLine
    {
        public int Id { get; set; }

        public int CleanerPayoutId { get; set; }

        [ForeignKey("CleanerPayoutId")]
        public virtual CleanerPayout? Payout { get; set; }

        /// <summary>
        /// The assignment paid. Nulled (not cascaded) if the cleaner is later removed from the
        /// order: the payment happened, and the snapshot columns below still say for what.
        /// </summary>
        public int? OrderCleanerId { get; set; }

        [ForeignKey("OrderCleanerId")]
        public virtual OrderCleaner? OrderCleaner { get; set; }

        /// <summary>Snapshot, deliberately without a foreign key — see <see cref="OrderCleanerId"/>.</summary>
        public int OrderId { get; set; }

        public int CleanerId { get; set; }

        /// <summary>The cleaning's date, so history reads without joining orders that may have moved.</summary>
        public DateTime ServiceDate { get; set; }

        /// <summary>What was handed over for this line in THIS payout — a top-up records only the difference.</summary>
        [Column(TypeName = "decimal(18,2)")]
        public decimal Amount { get; set; }

        /// <summary>True when the line had already been paid once and this was the shortfall.</summary>
        public bool IsTopUp { get; set; }

        /// <summary>
        /// Set when this line was reverted — by reverting the whole payout, or by "Undo payment"
        /// on the Orders tab. A reverted line no longer counts towards what the order line has had.
        /// </summary>
        public DateTime? RevertedAt { get; set; }

        public int? RevertedByUserId { get; set; }
    }
}
