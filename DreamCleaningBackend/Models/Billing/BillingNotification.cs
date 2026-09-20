using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.Models.Billing
{
    public enum BillingNotificationType
    {
        /// <summary>Automatic payment succeeded on the first card tried.</summary>
        AutoPaySucceeded = 0,

        /// <summary>The Primary card failed, the authorised Backup paid. Owner's rule: the customer
        /// is told even though nothing is owed, because their Primary card needs attention.</summary>
        PrimaryFailedBackupSucceeded = 1,

        /// <summary>No card could be charged — the obligation is still owed.</summary>
        AutoPayFailed = 2,

        /// <summary>The bank asked for authentication, which needs the customer present.</summary>
        AuthenticationRequired = 3,

        /// <summary>A saved card disappeared on Stripe's side (detached externally).</summary>
        CardRemoved = 4,

        /// <summary>AutoPay switched itself off because no usable card remains.</summary>
        AutoPayPaused = 5
    }

    public enum BillingNotificationSeverity
    {
        Info = 0,
        Warning = 1,
        Critical = 2
    }

    public enum BillingDeliveryStatus
    {
        /// <summary>This channel is not used for this notification.</summary>
        NotRequired = 0,
        Pending = 1,
        Sent = 2,

        /// <summary>Gave up after the retry budget.</summary>
        Failed = 3,

        /// <summary>Deliberately not sent (opted out, no address, obligation already resolved).</summary>
        Skipped = 4
    }

    /// <summary>
    /// A billing message for one customer — the persistent in-app notice AND a small durable
    /// outbox for its email and SMS.
    ///
    /// Written in the same unit of work as the payment outcome it describes, then delivered by the
    /// AutoPay worker with retries. A mail server being down therefore delays a message; it never
    /// loses one and it never touches the payment state.
    ///
    /// <see cref="DedupeKey"/> is UNIQUE: a repeated webhook, a retried sweep or a second
    /// reconciliation of the same outcome collides here instead of texting the customer twice.
    /// </summary>
    public class BillingNotification
    {
        public int Id { get; set; }

        public int UserId { get; set; }

        public BillingNotificationType Type { get; set; }

        public BillingNotificationSeverity Severity { get; set; }

        [Required, StringLength(200)]
        public string Title { get; set; } = string.Empty;

        [Required, StringLength(2000)]
        public string Message { get; set; } = string.Empty;

        /// <summary>Site-relative link for the in-app notice (e.g. /order/123/pay).</summary>
        [StringLength(500)]
        public string? ActionUrl { get; set; }

        [StringLength(80)]
        public string? ActionLabel { get; set; }

        [StringLength(40)]
        public string? ObligationKey { get; set; }

        public int? OrderId { get; set; }

        public int? CommercialInvoiceId { get; set; }

        public int? PaymentAttemptId { get; set; }

        [Required, StringLength(120)]
        public string DedupeKey { get; set; } = string.Empty;

        public bool ShowInApp { get; set; } = true;

        public DateTime? ReadAt { get; set; }

        /// <summary>
        /// Set once the obligation it chases is settled by ANY path. A resolved notice is shown as
        /// "paid", never as "pay now" — a stale failure notice is exactly how a customer is invited
        /// to pay twice.
        /// </summary>
        public DateTime? ResolvedAt { get; set; }

        public BillingDeliveryStatus EmailStatus { get; set; } = BillingDeliveryStatus.NotRequired;

        [StringLength(254)]
        public string? EmailTo { get; set; }

        [StringLength(200)]
        public string? EmailSubject { get; set; }

        public string? EmailHtml { get; set; }

        public int EmailAttempts { get; set; }

        [StringLength(300)]
        public string? EmailLastError { get; set; }

        public BillingDeliveryStatus SmsStatus { get; set; } = BillingDeliveryStatus.NotRequired;

        [StringLength(20)]
        public string? SmsTo { get; set; }

        [StringLength(640)]
        public string? SmsBody { get; set; }

        public int SmsAttempts { get; set; }

        [StringLength(300)]
        public string? SmsLastError { get; set; }

        public DateTime? NextDeliveryAttemptAt { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
