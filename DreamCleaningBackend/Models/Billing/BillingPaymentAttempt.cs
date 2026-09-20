using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models.Billing
{
    public enum BillingObligationType
    {
        Order = 0,
        CommercialInvoice = 1
    }

    /// <summary>Who or what started a saved-card charge.</summary>
    public enum BillingAttemptTrigger
    {
        /// <summary>An admin pressed "Charge saved card" under an office-booked-orders authorisation.</summary>
        AdminCharge = 0,

        /// <summary>The recurring sweep, at the moment the automatic payment request is due.</summary>
        AutoPayRecurring = 1,

        /// <summary>The commercial sweep, on an invoice's due date.</summary>
        AutoPayCommercial = 2,

        /// <summary>The signed-in customer paying an invoice with a saved card (recovery).</summary>
        CustomerSavedCard = 3
    }

    public enum BillingAttemptStatus
    {
        /// <summary>Row written and the lock held; the Stripe call has not answered yet.</summary>
        Pending = 0,
        Succeeded = 1,

        /// <summary>A definite decline or error — no money moved.</summary>
        Failed = 2,

        /// <summary>The bank asked for customer authentication. The intent was cancelled; nothing
        /// was charged, and the customer must pay while present.</summary>
        RequiresAction = 3,

        /// <summary>
        /// The call to Stripe did not come back (timeout, network). The money MAY have moved, so
        /// the lock stays held and NOTHING — not the Backup card, not a retry, not a customer
        /// payment — may start until the reconciler has resolved it.
        /// </summary>
        Unknown = 4,

        /// <summary>Stopped before any charge (something else is paying, the amount is gone...).</summary>
        Canceled = 5,

        /// <summary>Stripe processing (async). Treated exactly like Unknown for locking.</summary>
        Processing = 6
    }

    public enum BillingCardRole
    {
        Primary = 0,
        Backup = 1,

        /// <summary>A specific card the customer picked for this one payment.</summary>
        Selected = 2
    }

    /// <summary>
    /// ONE server-initiated charge attempt against a saved card — the durable record every
    /// Primary / Backup decision, every notification and every reconciliation reads from.
    ///
    /// ══ THE LOCK ══
    /// <see cref="ActiveLockKey"/> carries the obligation key (<c>order:123</c> / <c>invoice:45</c>)
    /// while the attempt could still move money (Pending / Unknown / Processing) and is cleared
    /// the moment it cannot. A UNIQUE index on it means a second attempt for the same obligation —
    /// a second admin, AutoPay racing an admin, a duplicate worker — fails at INSERT, before Stripe
    /// is ever called. That index, not a spinner and not an in-memory flag, is the guarantee.
    ///
    /// ══ IDEMPOTENCY ══
    /// <see cref="IdempotencyKey"/> is random and written once at INSERT — never derived from the
    /// row id, which restarts in every fresh database while Stripe remembers keys for 24 hours
    /// across all of them. It is stable for the life of the attempt: a retried Stripe call after a timeout returns the SAME PaymentIntent rather than a
    /// second charge. The Primary→Backup hop is a SECOND row with its own key but the same
    /// <see cref="RunKey"/> and the same obligation.
    /// </summary>
    public class BillingPaymentAttempt
    {
        public int Id { get; set; }

        [Required, StringLength(40)]
        public string ObligationKey { get; set; } = string.Empty;

        public BillingObligationType ObligationType { get; set; }

        public int? OrderId { get; set; }

        public int? CommercialInvoiceId { get; set; }

        /// <summary>Whose card was (or would have been) charged.</summary>
        public int UserId { get; set; }

        /// <summary>Groups a Primary attempt with the Backup attempt that followed it.</summary>
        [Required, StringLength(40)]
        public string RunKey { get; set; } = string.Empty;

        /// <summary>1 = first card tried in the run, 2 = the Backup.</summary>
        public int Sequence { get; set; } = 1;

        public BillingAttemptTrigger Trigger { get; set; }

        public BillingCardRole CardRole { get; set; }

        public int? CustomerPaymentMethodId { get; set; }

        [StringLength(100)]
        public string? StripePaymentMethodId { get; set; }

        [StringLength(20)]
        public string? CardBrand { get; set; }

        [StringLength(4)]
        public string? CardLast4 { get; set; }

        public int? PaymentAuthorizationId { get; set; }

        /// <summary>The admin (or customer) who pressed the button; null for the AutoPay sweeps.</summary>
        public int? InitiatedByUserId { get; set; }

        [Column(TypeName = "decimal(10,2)")]
        public decimal Amount { get; set; }

        [Required, StringLength(80)]
        public string IdempotencyKey { get; set; } = string.Empty;

        [StringLength(100)]
        public string? StripePaymentIntentId { get; set; }

        /// <summary>The commercial ledger's own attempt row, for an invoice obligation.</summary>
        public int? CommercialInvoicePaymentAttemptId { get; set; }

        public BillingAttemptStatus Status { get; set; } = BillingAttemptStatus.Pending;

        [StringLength(100)]
        public string? FailureCode { get; set; }

        [StringLength(100)]
        public string? DeclineCode { get; set; }

        /// <summary>For admins only; truncated. The customer is shown fixed wording.</summary>
        [StringLength(300)]
        public string? FailureMessage { get; set; }

        [StringLength(40)]
        public string? ActiveLockKey { get; set; }

        /// <summary>How many times the reconciler has asked Stripe about an Unknown outcome.</summary>
        public int ReconcileAttempts { get; set; }

        /// <summary>
        /// Set on the run's FIRST attempt once the run is finished — Backup tried or not needed,
        /// customer notified. A first attempt with a definite outcome but no stamp is a run whose
        /// process died half-way, and the reconciler finishes it.
        /// </summary>
        public DateTime? RunFinalizedAt { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? CompletedAt { get; set; }

        [NotMapped]
        public bool HoldsLock => Status is BillingAttemptStatus.Pending
                                           or BillingAttemptStatus.Unknown
                                           or BillingAttemptStatus.Processing;

        public static string OrderObligationKey(int orderId) => $"order:{orderId}";
        public static string InvoiceObligationKey(int invoiceId) => $"invoice:{invoiceId}";
    }
}
