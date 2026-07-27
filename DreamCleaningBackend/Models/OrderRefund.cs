using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    /// <summary>
    /// Audit record of one admin-initiated refund against one Stripe charge. This table is a LOG,
    /// not the accounting authority: the remaining-refundable ceiling is always read live from
    /// Stripe (amount_received − amount_refunded), because refunds issued straight from the Stripe
    /// Dashboard — which is how they were done before this feature — never produce a row here.
    /// Summing these rows to decide what is still refundable would double-refund those orders.
    ///
    /// One admin click can produce SEVERAL rows: an order accumulates a separate PaymentIntent per
    /// paid order-edit (Order.PaymentIntentId plus each OrderUpdateHistory.PaymentIntentId), and
    /// Stripe refunds one intent at a time, so a refund spanning charges is allocated across them.
    /// </summary>
    public class OrderRefund
    {
        public int Id { get; set; }

        public int OrderId { get; set; }
        public virtual Order Order { get; set; }

        /// <summary>Dollars refunded against this one charge (not the whole admin request).</summary>
        [Column(TypeName = "decimal(18,2)")]
        public decimal Amount { get; set; }

        /// <summary>The Stripe charge this row refunded. Recorded because an order can have
        /// several, and a failed row needs to say which charge it failed against.</summary>
        [StringLength(100)]
        public string? PaymentIntentId { get; set; }

        /// <summary>Stripe's refund id (re_...). Null while Pending and on Failed rows.</summary>
        [StringLength(100)]
        public string? StripeRefundId { get; set; }

        /// <summary>"Pending" until Stripe answers, then Stripe's own status ("succeeded",
        /// "pending", "failed", "canceled") or "Failed" when the call itself threw.</summary>
        [Required]
        [StringLength(50)]
        public string Status { get; set; } = "Pending";

        /// <summary>INTERNAL admin note. Never shown to the customer and never sent to Stripe.</summary>
        [StringLength(500)]
        public string? Reason { get; set; }

        /// <summary>Admin-facing detail when Status is Failed. Sanitized before it reaches the UI.</summary>
        [StringLength(500)]
        public string? FailureReason { get; set; }

        /// <summary>Where this refund came from. Stored as int so existing rows land on Crm (0)
        /// via the column default — no backfill statement needed.</summary>
        public RefundSource Source { get; set; } = RefundSource.Crm;

        /// <summary>The admin who issued it. NULL for Source = Stripe: a refund made in the Stripe
        /// Dashboard has no CRM admin behind it, and inventing one would falsify the audit trail.</summary>
        public int? RefundedByUserId { get; set; }

        [ForeignKey("RefundedByUserId")]
        public virtual User? RefundedByUser { get; set; }

        public DateTime CreatedAt { get; set; }

        /// <summary>Whether the customer refund-confirmation email went out for this refund.
        /// False when the admin skipped it AND when sending failed — a failed email never
        /// rolls back a completed refund.</summary>
        public bool EmailSent { get; set; }
    }
}
