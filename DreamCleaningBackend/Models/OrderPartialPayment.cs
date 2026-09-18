using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    /// <summary>Where one requested slice of an order's total is in its conversation with Stripe.</summary>
    public enum OrderPartialPaymentStatus
    {
        /// <summary>The admin has asked for this amount; nothing has been charged yet.</summary>
        Pending = 0,

        /// <summary>Settled. <see cref="OrderPartialPayment.PaidAmount"/> was added to
        /// <c>Order.AmountPaid</c> in the same transaction.</summary>
        Paid = 1,

        /// <summary>Withdrawn by an admin before it was paid. Kept rather than deleted so the
        /// order's payment history still shows what was asked for and dropped.</summary>
        Cancelled = 2
    }

    /// <summary>
    /// ONE part-payment an admin asked a customer for, against an order that is not yet paid —
    /// "$1,000 now, the rest before the cleaning".
    ///
    /// <b>Why this is not the order-edit top-up (<see cref="OrderUpdateHistory"/>).</b> A top-up is
    /// money owed ON TOP of a settled order because its price went up. This is a slice OF the
    /// original total, taken before the order has been paid at all. The two answer different
    /// questions and an order can carry both: three deposits that settled it, then a top-up when an
    /// admin later adds an extra.
    ///
    /// <b>Why a row rather than a number on the order.</b> The payment page has to charge the amount
    /// the admin actually agreed with the customer, not an amount the page computes — so the request
    /// has to exist server-side before the link is opened. Storing the requests also makes the
    /// history answerable: who asked for what, when, and which Stripe charge settled it.
    ///
    /// <b>At most ONE Pending row per order</b> (enforced in <c>OrderPartialPaymentService</c>).
    /// Two live requests would let the same money be charged twice by opening the link in two tabs,
    /// and there is no ordering rule that would tell the payment page which to charge first.
    ///
    /// <b><see cref="PaymentIntentId"/> carries a UNIQUE index</b> (filtered to non-null). That is
    /// the real idempotency guard: Stripe retries webhook deliveries, and the browser's confirm can
    /// race the webhook for the same intent. Two settlements of one charge collide at the database
    /// rather than crediting the customer's money twice.
    /// </summary>
    public class OrderPartialPayment
    {
        public int Id { get; set; }

        public int OrderId { get; set; }

        [ForeignKey("OrderId")]
        public virtual Order? Order { get; set; }

        /// <summary>What the admin asked for. Validated at creation against the order's live
        /// balance, and re-checked when the intent is created — an order edited downward between
        /// the two must not be able to charge more than is still owed.</summary>
        [Column(TypeName = "decimal(18,2)")]
        public decimal RequestedAmount { get; set; }

        /// <summary>
        /// What actually arrived, null until paid. Stored separately from
        /// <see cref="RequestedAmount"/> because the customer may choose "pay the full balance
        /// instead" on the payment page — the row then settles for MORE than was asked, and the
        /// difference between the two is exactly the fact an admin reading the history needs.
        /// </summary>
        [Column(TypeName = "decimal(18,2)")]
        public decimal? PaidAmount { get; set; }

        public OrderPartialPaymentStatus Status { get; set; } = OrderPartialPaymentStatus.Pending;

        /// <summary>UNIQUE (filtered to non-null) — see the class remarks. Stamped when the intent
        /// is created, so a webhook arriving before the browser's confirm can still find the row.</summary>
        [StringLength(100)]
        public string? PaymentIntentId { get; set; }

        public DateTime? PaidAt { get; set; }

        /// <summary>The admin who asked for this amount. NULL only on a row the settlement
        /// path had to invent because money arrived for this order with no request to match it —
        /// recording it under nobody beats not recording that the money exists.</summary>
        public int? RequestedByUserId { get; set; }

        [ForeignKey("RequestedByUserId")]
        public virtual User? RequestedByUser { get; set; }

        /// <summary>Admin-facing note — "deposit agreed on the phone", "balance before the job".
        /// Never shown to the customer.</summary>
        [StringLength(500)]
        public string? Note { get; set; }

        /// <summary>When the request's payment-link email/SMS last went out. Null means the link
        /// was created but never sent — the panel says so rather than implying the customer knows.</summary>
        public DateTime? NotificationSentAt { get; set; }

        public int? CancelledByUserId { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime? UpdatedAt { get; set; }

        /// <summary>
        /// How this SLICE was paid. Default Normal means "through the Stripe card link", exactly
        /// like <see cref="Order.PaymentMethod"/>'s default — so every row settled the original
        /// way needs no backfill. Set to Cash/Zelle/Check/Other/Invoice when an admin records that
        /// this particular slice was collected outside Stripe.
        ///
        /// Deliberately does NOT touch <see cref="Order.PaymentMethod"/>, which stays Normal for
        /// an order that is part card, part manual — mirroring how one <see cref="OrderUpdateHistory"/>
        /// row can be paid manually without relabeling the whole order. Statistics/finances do not
        /// yet fold a manually-settled slice's tax into the "retained tax" reporting the way a
        /// manually-paid update-history row does (see OrderRevenueMath) — that is a deliberate,
        /// documented gap: a part-payment slice is a proportional share of the order's tax, not an
        /// exact recorded difference the way a top-up's OriginalTax/NewTax is, and getting that
        /// allocation right needs its own pass rather than a guess bolted on here.
        /// </summary>
        public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.Normal;

        /// <summary>Zelle confirmation #, check #, receipt # — admin-entered, optional.</summary>
        [StringLength(255)]
        public string? PaymentReference { get; set; }

        /// <summary>Admin-facing note about the manual payment. Never shown to the customer.</summary>
        [StringLength(1000)]
        public string? PaymentNotes { get; set; }

        /// <summary>When an admin recorded this slice as paid outside Stripe. Null for a slice
        /// paid through the card link (or not yet paid at all).</summary>
        public DateTime? ManualPaymentRecordedAt { get; set; }

        public int? ManualPaymentRecordedByUserId { get; set; }

        [ForeignKey("ManualPaymentRecordedByUserId")]
        public virtual User? ManualPaymentRecordedByUser { get; set; }
    }
}
