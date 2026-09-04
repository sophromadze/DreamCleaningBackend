using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using DreamCleaningBackend.Helpers;

namespace DreamCleaningBackend.Models
{
    public class Cleaner
    {
        [Key]
        public int Id { get; set; }

        [Required]
        [StringLength(50)]
        public string FirstName { get; set; } = string.Empty;

        [Required]
        [StringLength(50)]
        public string LastName { get; set; } = string.Empty;

        public int? Age { get; set; }

        [StringLength(500)]
        public string? Experience { get; set; }

        public bool IsExperienced { get; set; } = false;

        private string? _phone;
        [StringLength(20)]
        public string? Phone
        {
            get => _phone;
            set => _phone = PhoneHelper.NormalizeToDigits(value);
        }

        [EmailAddress]
        [StringLength(100)]
        public string? Email { get; set; }

        // Free-text street address. Reuses the original "Location" column so existing
        // free-text location data is preserved as the address with no data migration.
        [StringLength(300)]
        [Column("Location")]
        public string? Address { get; set; }

        // Borough the cleaner LIVES in (single: Brooklyn / Manhattan / Queens). Stored in the "Borough" column.
        [StringLength(50)]
        [Column("Borough")]
        public string? Location { get; set; }

        // Boroughs the cleaner WORKS in (multi-select), stored as CSV e.g. "Brooklyn,Queens".
        // Distinct from Location (residence). Empty = treated as working everywhere.
        [StringLength(50)]
        public string? OperatingAreas { get; set; }

        // Recurring weekly days the cleaner is unavailable, stored as a CSV of
        // System.DayOfWeek integers (0=Sunday … 6=Saturday), e.g. "1,2" = Mon & Tue.
        // No hours — a marked weekday means the cleaner is busy that whole day.
        [StringLength(50)]
        public string? BusyDaysOfWeek { get; set; }

        public bool AlreadyWorkedWithUs { get; set; } = false;

        [StringLength(100)]
        public string? Nationality { get; set; }

        /// <summary>
        /// The language this cleaner has CHOSEN for their portal, or null to follow their
        /// nationality (CleanerLanguage.Resolve). Null rather than "en" is load-bearing: it is the
        /// difference between "has expressed no preference" and "asked for English", and only the
        /// first of those should start following a nationality that is corrected later.
        /// </summary>
        [StringLength(5)]
        public string? PortalLanguage { get; set; }

        public CleanerRanking Ranking { get; set; } = CleanerRanking.Standard;

        [StringLength(500)]
        public string? RestrictedReason { get; set; }

        [StringLength(1000)]
        public string? Allergies { get; set; }

        [StringLength(1000)]
        public string? Restrictions { get; set; }

        [StringLength(2000)]
        public string? MainNote { get; set; }

        /// <summary>
        /// How this cleaner is paid their wages (Zelle / Cash / Check / Other). Optional — a
        /// blank method is normal and never blocks a payout being recorded; it is a hint for
        /// whoever sends the money on the Outgoing Payments page.
        /// </summary>
        public CleanerPaymentMethod? PaymentMethod { get; set; }

        /// <summary>
        /// The destination that goes with <see cref="PaymentMethod"/> — a Zelle phone/email, the
        /// name a check is written out to, and so on. Free text on purpose: one field has to hold
        /// all four methods' details, and none of them share a format worth validating.
        /// </summary>
        [StringLength(200)]
        public string? PaymentDetails { get; set; }

        [StringLength(500)]
        public string? PhotoUrl { get; set; }

        [StringLength(500)]
        public string? DocumentUrl { get; set; }

        public CleanerDocumentType? DocumentType { get; set; }

        public bool IsActive { get; set; } = true;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? UpdatedAt { get; set; }

        public int? CreatedByAdminId { get; set; }

        [ForeignKey("CreatedByAdminId")]
        public virtual User? CreatedByAdmin { get; set; }

        public int? MigratedFromUserId { get; set; }

        /// <summary>
        /// The LOGIN ACCOUNT this cleaner uses to open the cleaner portal, when one exists. Null is
        /// the normal state - most cleaners have no account at all, and a cleaner is a cleaner
        /// whether or not they ever sign in.
        ///
        /// This is the authoritative link, deliberately stronger than the email match that creates
        /// it: a cleaner row may carry no email, or a stale one, and matching on a mutable string
        /// would silently re-point somebody's schedule the day an admin corrected a typo. The email
        /// match is only how a link is DISCOVERED (on registration, and by the one-time backfill);
        /// once this column is set it is what Helpers/CleanerAccountLink resolves through.
        ///
        /// Unique - one account per cleaner and one cleaner per account. SetNull on delete: if the
        /// account goes, the cleaner record stays and simply has nobody signing in for it.
        /// </summary>
        public int? UserId { get; set; }

        [ForeignKey("UserId")]
        public virtual User? User { get; set; }

        public virtual ICollection<CleanerNote> Notes { get; set; } = new List<CleanerNote>();

        // Date ranges (inclusive) the cleaner is on vacation / away.
        public virtual ICollection<CleanerVacation> Vacations { get; set; } = new List<CleanerVacation>();
    }
}
