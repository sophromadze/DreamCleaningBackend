using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models.Billing
{
    /// <summary>
    /// WHAT a customer has authorised us to charge their saved card for. Four separate scopes on
    /// purpose — a global toggle must never silently authorise every future charge across
    /// unrelated arrangements (owner's rule, 2026-09).
    /// </summary>
    public enum PaymentAuthorizationScope
    {
        /// <summary>
        /// The Billing tab's "Automatic Payments" master switch: the customer's general agreement
        /// to automatic payments. On its own it authorises NOTHING — every charge also needs one of
        /// the arrangement scopes below. Turning it off pauses all of them.
        /// </summary>
        General = 0,

        /// <summary>One recurring cleaning series, one visit at a time, at the moment the existing
        /// automatic payment request would have gone out.</summary>
        RecurringSeries = 1,

        /// <summary>
        /// Orders the office books for the customer (phone bookings, recreated orders) — the ONLY
        /// thing that lets an admin press "Charge saved card". Carries the three booking consents
        /// (SMS, $70 cancellation fee, Terms) explicitly, because the customer never saw the
        /// booking form those orders skip; generic AutoPay consent is not acceptance of them.
        /// </summary>
        OfficeBookedOrders = 2,

        /// <summary>Commercial invoices of one business client linked to this account, charged on
        /// each invoice's due date.</summary>
        CommercialClient = 3
    }

    public enum PaymentAuthorizationStatus
    {
        Active = 0,
        Revoked = 1
    }

    /// <summary>
    /// A durable record of one authorisation: who agreed, to what scope, under which wording, when
    /// and from where — and, once withdrawn, when and by whom. Rows are never updated back to
    /// Active; re-authorising writes a new row, so the history of what the customer agreed to is
    /// complete.
    ///
    /// <see cref="ActiveScopeKey"/> equals <see cref="ScopeKey"/> while the row is active and is
    /// NULL once revoked. The unique index on (UserId, ActiveScopeKey) is therefore "at most one
    /// active authorisation per scope" — MariaDB lets any number of revoked (NULL) rows coexist.
    /// </summary>
    public class PaymentAuthorization
    {
        public int Id { get; set; }

        public int UserId { get; set; }

        [ForeignKey("UserId")]
        public virtual User? User { get; set; }

        public PaymentAuthorizationScope Scope { get; set; }

        public int? RecurringSeriesId { get; set; }

        public int? ContractClientId { get; set; }

        /// <summary>general / office / series:{id} / client:{id}</summary>
        [Required, StringLength(60)]
        public string ScopeKey { get; set; } = string.Empty;

        [StringLength(60)]
        public string? ActiveScopeKey { get; set; }

        /// <summary>Whether a failed Primary may be followed by the Backup card for this scope.</summary>
        public bool AllowBackupFallback { get; set; }

        [Required, StringLength(20)]
        public string TermsVersion { get; set; } = string.Empty;

        /// <summary>SHA-256 of <see cref="TermsSnapshot"/>, hex.</summary>
        [Required, StringLength(64)]
        public string TermsHash { get; set; } = string.Empty;

        /// <summary>The exact wording shown and accepted, frozen with the row.</summary>
        [Required]
        public string TermsSnapshot { get; set; } = string.Empty;

        // Only meaningful for OfficeBookedOrders — the three booking consents.
        public bool SmsConsentAccepted { get; set; }
        public bool CancellationFeeAccepted { get; set; }
        public bool TermsOfServiceAccepted { get; set; }

        public DateTime AcceptedAt { get; set; } = DateTime.UtcNow;

        [StringLength(45)]
        public string? AcceptedIp { get; set; }

        [StringLength(300)]
        public string? AcceptedUserAgent { get; set; }

        public PaymentAuthorizationStatus Status { get; set; } = PaymentAuthorizationStatus.Active;

        public DateTime? RevokedAt { get; set; }

        public int? RevokedByUserId { get; set; }

        [StringLength(200)]
        public string? RevokedReason { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        [NotMapped]
        public bool IsActive => Status == PaymentAuthorizationStatus.Active;

        public static string BuildScopeKey(PaymentAuthorizationScope scope, int? seriesId, int? clientId) => scope switch
        {
            PaymentAuthorizationScope.General => "general",
            PaymentAuthorizationScope.OfficeBookedOrders => "office",
            PaymentAuthorizationScope.RecurringSeries => $"series:{seriesId}",
            PaymentAuthorizationScope.CommercialClient => $"client:{clientId}",
            _ => throw new ArgumentOutOfRangeException(nameof(scope))
        };
    }
}
