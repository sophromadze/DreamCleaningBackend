using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models.Billing
{
    public enum CustomerPaymentMethodStatus
    {
        /// <summary>Attached to the customer's Stripe Customer and usable.</summary>
        Active = 0,

        /// <summary>Removed by the customer (or detached on Stripe's side). Kept for history.</summary>
        Removed = 1,

        /// <summary>
        /// The issuer said never to try this card again (lost / stolen / revoked authorization).
        /// Still shown to the customer so they understand why it is not used, never charged.
        /// </summary>
        Blocked = 2
    }

    /// <summary>
    /// One saved card. A customer may hold any number of them (2026-09 billing upgrade — the old
    /// one-card-per-user columns on <see cref="User"/> are kept only as a mirror of the Primary).
    ///
    /// ONLY STRIPE REFERENCES AND DISPLAY METADATA. No card number, no CVC, no raw credential ever
    /// reaches this table: the card is collected by Stripe Elements and all we are handed back is
    /// the <c>pm_…</c> id plus brand / last four / expiry, which Stripe itself calls display data.
    ///
    /// The Primary/Backup ROLES do not live here. They are two pointer columns on the user
    /// (<see cref="User.PrimaryPaymentMethodId"/> / <see cref="User.BackupPaymentMethodId"/>), so
    /// "at most one Primary, at most one Backup" holds by construction instead of by a filtered
    /// unique index MariaDB does not have, and a CHECK constraint keeps the two different.
    /// </summary>
    public class CustomerPaymentMethod
    {
        public int Id { get; set; }

        public int UserId { get; set; }

        [ForeignKey("UserId")]
        public virtual User? User { get; set; }

        /// <summary>The Stripe Customer the card is attached to (cus_…).</summary>
        [Required, StringLength(100)]
        public string StripeCustomerId { get; set; } = string.Empty;

        /// <summary>The Stripe PaymentMethod (pm_…). Unique across the table.</summary>
        [Required, StringLength(100)]
        public string StripePaymentMethodId { get; set; } = string.Empty;

        [StringLength(20)]
        public string? Brand { get; set; }

        [StringLength(4)]
        public string? Last4 { get; set; }

        public int? ExpMonth { get; set; }

        public int? ExpYear { get; set; }

        /// <summary>credit / debit / prepaid / unknown, as Stripe reports it.</summary>
        [StringLength(20)]
        public string? Funding { get; set; }

        /// <summary>apple_pay / google_pay / link when the card came from a wallet.</summary>
        [StringLength(30)]
        public string? Wallet { get; set; }

        public CustomerPaymentMethodStatus Status { get; set; } = CustomerPaymentMethodStatus.Active;

        /// <summary>Why a card is Blocked — the decline code, never the processor's raw message.</summary>
        [StringLength(100)]
        public string? StatusReason { get; set; }

        /// <summary>How it was saved: setup_intent / checkout_payment / legacy_migration.</summary>
        [StringLength(30)]
        public string? Source { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? RemovedAt { get; set; }
        public DateTime? LastUsedAt { get; set; }
        public DateTime? LastFailedAt { get; set; }

        [StringLength(100)]
        public string? LastFailureCode { get; set; }

        /// <summary>
        /// Past the last day of its expiry month, in New York. Unknown expiry is NOT expired —
        /// legacy rows migrated from the one-card columns never stored it, and treating "don't
        /// know" as "expired" would silently switch AutoPay off for every one of them.
        /// </summary>
        public bool IsExpiredAt(DateTime nowNy)
        {
            if (ExpMonth == null || ExpMonth < 1 || ExpMonth > 12) return false;
            if (ExpYear == null || ExpYear < 2000) return false;
            var firstOfFollowingMonth = new DateTime(ExpYear.Value, ExpMonth.Value, 1).AddMonths(1);
            return nowNy.Date >= firstOfFollowingMonth;
        }

        [NotMapped]
        public bool IsActive => Status == CustomerPaymentMethodStatus.Active;
    }
}
