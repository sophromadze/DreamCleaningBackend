using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    /// <summary>Where a batch is in its conversation with Stripe.</summary>
    public enum OrderPaymentBatchStatus
    {
        /// <summary>Intent created, nothing charged yet.</summary>
        Pending = 0,

        /// <summary>Settled. Every covered order was marked paid in the same transaction.</summary>
        Paid = 1,

        /// <summary>Stripe declined it. Nothing was charged and no order moved.</summary>
        Failed = 2,

        /// <summary>Abandoned or superseded. Frees its orders to be paid another way.</summary>
        Canceled = 3,

        /// <summary>Stripe confirms submitted funds are processing or awaiting capture.</summary>
        Processing = 4
    }

    /// <summary>
    /// ONE Stripe charge covering SEVERAL of a customer's upcoming recurring cleanings —
    /// "Pay all upcoming".
    ///
    /// Deliberately NOT the commercial invoice system. A residential customer prepaying three of
    /// their own fortnightly cleanings is not being invoiced: there is no client entity, no net
    /// terms, no PO number, no document to send, and no ACH. Forcing it through
    /// <c>CommercialInvoice</c> would drag all of that in to express "she paid for three visits at
    /// once", and would put residential money into a ledger built for commercial billing. The two
    /// stay separate.
    ///
    /// What this row exists to answer, and what the audit requirement asks for: <b>which payment
    /// covered which orders</b>. Without it, three orders would simply carry the same
    /// PaymentIntentId and nothing would record that they were deliberately paid together, what
    /// each one's share was, or what the customer was shown before they authorized.
    ///
    /// <b>The amount is derived server-side, never accepted.</b> There is no amount field on the
    /// request DTO — the endpoint reads the customer's own unpaid upcoming occurrences, sums their
    /// stored totals, and writes the result here before it ever reaches Stripe.
    /// </summary>
    public class OrderPaymentBatch
    {
        public int Id { get; set; }

        /// <summary>The customer. Every covered order must belong to them — re-checked
        /// server-side, so a request naming somebody else's order is refused rather than
        /// filtered.</summary>
        public int UserId { get; set; }

        [ForeignKey("UserId")]
        public virtual User? User { get; set; }

        /// <summary>
        /// The series the batch was raised from, when it was raised from one. Null for a batch an
        /// admin or a future caller assembles from unrelated orders — the items are the truth
        /// about what is covered, this is only provenance.
        /// </summary>
        public int? RecurringSeriesId { get; set; }

        [ForeignKey("RecurringSeriesId")]
        public virtual RecurringOrderSeries? RecurringSeries { get; set; }

        /// <summary>
        /// UNIQUE (filtered to non-null). THE IDEMPOTENCY GUARD, in exactly the shape the
        /// commercial ledger uses: Stripe retries deliveries and can send the same event twice
        /// concurrently, so the settlement path cannot rely on an in-memory flag or on the
        /// best-effort WebhookEvents table. Two settlements for one intent collide at the database.
        /// </summary>
        [StringLength(100)]
        public string? PaymentIntentId { get; set; }

        /// <summary>What the customer authorized. Sum of the item amounts, computed server-side.</summary>
        [Column(TypeName = "decimal(18,2)")]
        public decimal Amount { get; set; }

        public OrderPaymentBatchStatus Status { get; set; } = OrderPaymentBatchStatus.Pending;

        public DateTime? PaidAt { get; set; }

        /// <summary>Why a batch failed or was abandoned. Admin-facing.</summary>
        [StringLength(500)]
        public string? FailureReason { get; set; }

        /// <summary>
        /// Set when settlement found one of the covered orders had already been paid by another
        /// route between authorization and capture. The money genuinely arrived, so it is
        /// recorded; the discrepancy is flagged for a person rather than silently absorbed —
        /// the same choice the commercial ledger makes on an overpayment.
        /// </summary>
        [StringLength(500)]
        public string? SettlementWarning { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public virtual ICollection<OrderPaymentBatchItem> Items { get; set; }
            = new List<OrderPaymentBatchItem>();
    }

    /// <summary>
    /// One order inside a combined payment, and its share of it. Unique on
    /// (OrderPaymentBatchId, OrderId).
    /// </summary>
    public class OrderPaymentBatchItem
    {
        public int Id { get; set; }

        public int OrderPaymentBatchId { get; set; }

        [ForeignKey("OrderPaymentBatchId")]
        public virtual OrderPaymentBatch? Batch { get; set; }

        public int OrderId { get; set; }

        [ForeignKey("OrderId")]
        public virtual Order? Order { get; set; }

        /// <summary>The order's total at the moment the batch was assembled, frozen. Re-reading it
        /// at settlement would let an admin edit between authorization and capture change what the
        /// customer is treated as having paid.</summary>
        [Column(TypeName = "decimal(18,2)")]
        public decimal Amount { get; set; }

        /// <summary>False when settlement found this order already paid by another route and so
        /// did not mark it again. The item stays in the batch: the customer's money covered it,
        /// and deleting the row would hide the duplicate rather than record it.</summary>
        public bool AppliedToOrder { get; set; }

        // ── Post-payment follow-up (2026-09) ──────────────────────────────────────────────────
        // What a single payment does after it settles an order — loyalty consumption,
        // subscription activation, the first-order flag, and the booking confirmation email/SMS —
        // must happen for each order a combined payment settles too, and EXACTLY once however
        // many times the webhook is delivered or settlement is retried. These columns are that
        // guarantee (CombinedPaymentFollowUpService). Rows that existed before the columns are
        // stamped done by the migration, so no historical order is re-mailed.

        /// <summary>Lease: set when a worker claims this item's follow-up. A claim older than the
        /// lease is taken over (the worker died); a fresh one is left alone.</summary>
        public DateTime? FollowUpClaimedAt { get; set; }

        /// <summary>How many times the follow-up was claimed. It stops being retried after a few.</summary>
        public int FollowUpAttempts { get; set; }

        /// <summary>Loyalty / subscription / first-order bookkeeping done, in ONE transaction with
        /// this stamp — so it can never run twice.</summary>
        public DateTime? BookkeepingAppliedAt { get; set; }

        /// <summary>Confirmation email/SMS handled (sent, or deliberately skipped). Null = still owed.</summary>
        public DateTime? ConfirmationSentAt { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
