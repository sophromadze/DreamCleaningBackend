using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models.Commercial
{
    /// <summary>Who processed a payment. Manual means it arrived out of band.</summary>
    public enum InvoicePaymentProvider
    {
        /// <summary>Recorded by an admin from a bank statement. No processor involved.</summary>
        Manual = 0,
        Stripe = 1
    }

    /// <summary>
    /// Lifecycle of one attempt to pay an invoice online.
    ///
    /// This is NOT the invoice's status and NOT a financial record — see
    /// <see cref="CommercialInvoicePaymentAttempt"/>. ACH is asynchronous, so an attempt spends
    /// days in <see cref="Processing"/> during which no money has moved and the invoice is still
    /// unpaid.
    /// </summary>
    public enum InvoicePaymentAttemptStatus
    {
        /// <summary>Row written, Checkout Session not yet created.</summary>
        Created = 0,

        /// <summary>Checkout Session exists; the customer is in Stripe's hosted flow.</summary>
        CheckoutOpen = 1,

        /// <summary>
        /// The customer authorized the debit and Stripe is moving the money. For ACH this lasts
        /// several business days. NOTHING HAS SETTLED YET.
        /// </summary>
        Processing = 2,

        /// <summary>Stripe confirmed settlement. Exactly one payment row exists for this attempt.</summary>
        Succeeded = 3,

        Failed = 4,

        /// <summary>The Checkout Session expired before the customer finished.</summary>
        Expired = 5,

        Canceled = 6
    }

    /// <summary>
    /// One attempt to pay a commercial invoice online.
    ///
    /// SEPARATE FROM <see cref="CommercialInvoicePayment"/> ON PURPOSE, and the distinction is the
    /// whole reason this table exists. A payment row is a FINANCIAL FACT — money that arrived, part
    /// of an immutable ledger. An attempt is a CONVERSATION WITH STRIPE that may take days and may
    /// end in nothing. ACH makes the gap real: a customer authorizes a debit on Monday and the
    /// funds settle on Thursday, and for those three days the invoice is genuinely still unpaid.
    ///
    /// Writing a payment row when Checkout starts would mark invoices paid for money that had not
    /// moved and might never arrive. So the attempt carries the in-flight state, and exactly one
    /// payment row is created at <c>payment_intent.succeeded</c>.
    ///
    /// Both Stripe id columns carry UNIQUE indexes. That is the idempotency guard that actually
    /// holds under webhook retries and concurrent deliveries — not an in-memory flag, and not the
    /// best-effort WebhookEvents check, which swallows its own errors.
    /// </summary>
    public class CommercialInvoicePaymentAttempt
    {
        public int Id { get; set; }

        public int CommercialInvoiceId { get; set; }
        [ForeignKey("CommercialInvoiceId")]
        public virtual CommercialInvoice? Invoice { get; set; }

        public InvoicePaymentProvider Provider { get; set; } = InvoicePaymentProvider.Stripe;

        /// <summary>
        /// What the customer chose. ACH and Card share this table and every downstream code path —
        /// the money pipeline must not be duplicated per method.
        /// </summary>
        public InvoicePaymentRecordMethod PaymentMethod { get; set; } = InvoicePaymentRecordMethod.AchBankTransfer;

        /// <summary>
        /// The balance at the moment the attempt was created, in dollars. Kept so the
        /// duplicate-attempt guard can ask "is the full balance already in flight?" without
        /// re-reading Stripe, and so a later balance change is visible against what was authorized.
        /// </summary>
        [Column(TypeName = "decimal(10,2)")]
        public decimal Amount { get; set; }

        [Required, StringLength(3)]
        public string Currency { get; set; } = "USD";

        /// <summary>UNIQUE. Null only between row insert and the Stripe call succeeding.</summary>
        [StringLength(255)]
        public string? StripeCheckoutSessionId { get; set; }

        /// <summary>
        /// UNIQUE. Arrives on <c>checkout.session.completed</c> and is what
        /// <c>payment_intent.processing</c> / <c>.succeeded</c> / <c>.payment_failed</c> resolve
        /// the attempt by.
        /// </summary>
        [StringLength(255)]
        public string? StripePaymentIntentId { get; set; }

        public InvoicePaymentAttemptStatus Status { get; set; } = InvoicePaymentAttemptStatus.Created;

        /// <summary>Stripe's machine code for a failure, e.g. <c>account_closed</c>.</summary>
        [StringLength(100)]
        public string? FailureCode { get; set; }

        /// <summary>
        /// Stripe's own message. ADMIN-FACING ONLY — the public page shows fixed wording instead,
        /// because a processor's internal message is not something to put in front of a customer.
        /// </summary>
        [StringLength(500)]
        public string? FailureMessage { get; set; }

        /// <summary>
        /// Safe display detail Stripe gives back about the funding source — e.g. "Wells Fargo
        /// ••••6789". Bank NAME and LAST FOUR only, which is all Stripe exposes and all anyone
        /// needs. The full account and routing numbers never touch this database.
        /// </summary>
        [StringLength(120)]
        public string? PaymentSourceLabel { get; set; }

        /// <summary>The payment row this attempt produced, once it settled.</summary>
        public int? CommercialInvoicePaymentId { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>Set when the attempt reached any terminal state, not only success.</summary>
        public DateTime? CompletedAt { get; set; }

        /// <summary>
        /// True while Stripe is moving money for this attempt. The public page's "payment
        /// processing" banner and the duplicate-payment guard both read this, so the definition
        /// lives here once rather than being re-expressed at each call site.
        /// </summary>
        [NotMapped]
        public bool IsInFlight =>
            Status is InvoicePaymentAttemptStatus.CheckoutOpen
                   or InvoicePaymentAttemptStatus.Processing;
    }
}
