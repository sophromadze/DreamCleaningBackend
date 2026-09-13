using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models.Commercial
{
    /// <summary>
    /// One cleaning covered by one invoice, and what the invoice allocates to it.
    ///
    /// An explicit join entity rather than a many-to-many shortcut, because the relationship
    /// CARRIES MONEY: four visits billed at an agreed $3,500 are $875.00 each, and that figure has
    /// to be stored, audited and reproducible. A bare link table would leave the allocation
    /// implicit and re-derived on every read, which is how a draft an admin approved on Monday
    /// silently becomes a different set of numbers on Thursday.
    ///
    /// <b>Draft rows are a PROPOSAL; committed rows are a fact.</b> While the invoice is a Draft
    /// the row records what WOULD be allocated and the orders are untouched — an admin who types
    /// $3,500, thinks better of it and abandons the draft must not have re-priced four real
    /// bookings on the way out. <see cref="CommittedAt"/> is stamped when the invoice is finalized
    /// (sent), and only then are the orders' totals written. See
    /// <c>InvoiceOrderLinkService.CommitAllocationsAsync</c>.
    ///
    /// <b>Void rows are kept.</b> A voided invoice's links stay in the table so the trail can still
    /// answer "what did DCI-2026-… cover?" years later; they simply stop counting as an active
    /// claim on the order, which is what frees the order to be billed again.
    /// </summary>
    public class CommercialInvoiceOrder
    {
        public int Id { get; set; }

        public int CommercialInvoiceId { get; set; }

        [ForeignKey("CommercialInvoiceId")]
        public virtual CommercialInvoice? Invoice { get; set; }

        public int OrderId { get; set; }

        [ForeignKey("OrderId")]
        public virtual Order? Order { get; set; }

        /// <summary>
        /// What this invoice allocates to this order — the order's final agreed price under the
        /// invoice. Equal to the order's own total unless the admin negotiated a group figure.
        /// </summary>
        [Column(TypeName = "decimal(10,2)")]
        public decimal AllocatedAmount { get; set; }

        /// <summary>
        /// The order's total BEFORE this invoice touched it, captured when the row is created and
        /// never rewritten.
        ///
        /// This is the auditable half of the price override: without it, a negotiated allocation
        /// would overwrite the only record of what the job was priced at, and "what did this cost
        /// before we agreed the monthly figure?" would be unanswerable.
        /// </summary>
        [Column(TypeName = "decimal(10,2)")]
        public decimal OriginalOrderTotal { get; set; }

        /// <summary>
        /// The order's payment method before this invoice adopted it, as the underlying int.
        ///
        /// Sending an invoice switches every cleaning it covers to the Invoice method — the client
        /// is being billed, not charged — and voiding the invoice has to be able to put that back.
        /// Stored as an <c>int?</c> rather than the residential <c>PaymentMethod</c> enum so no
        /// order-side type reaches this namespace (see <c>IOrderInvoiceAllocationService</c>).
        ///
        /// NULL on rows committed before this was recorded, and on every uncommitted draft row.
        /// Null means "nothing to restore", never "restore to Normal".
        /// </summary>
        public int? OriginalPaymentMethod { get; set; }

        /// <summary>
        /// The commercial client the order was billed to before this invoice adopted it — usually
        /// null, because the common case is a cleaning that was never linked to a client at all.
        /// Restored alongside <see cref="OriginalPaymentMethod"/> and only when that is set.
        /// </summary>
        public int? OriginalContractClientId { get; set; }

        /// <summary>
        /// When the allocation was actually written onto the order. Null while the invoice is a
        /// Draft — see the class comment.
        /// </summary>
        public DateTime? CommittedAt { get; set; }

        /// <summary>The admin who finalized the invoice, and therefore the allocation.</summary>
        public int? CommittedByUserId { get; set; }

        [ForeignKey("CommittedByUserId")]
        public virtual User? CommittedByUser { get; set; }

        /// <summary>
        /// When this invoice's payment activated the order. Null until the invoice is fully Paid.
        /// Stamped inside the same transaction as the activation, so it doubles as the
        /// idempotency marker — a webhook retry finds it already set and does nothing.
        /// </summary>
        public DateTime? ActivatedOrderAt { get; set; }

        /// <summary>Line description as it appears on the invoice, e.g. "Commercial Cleaning — Oct 4".</summary>
        [StringLength(500)]
        public string? LineDescription { get; set; }

        public int SortOrder { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
