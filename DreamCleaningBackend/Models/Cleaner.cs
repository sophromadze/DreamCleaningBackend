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

        // Borough selection (Brooklyn / Manhattan / Queens). Stored in a new "Borough" column.
        [StringLength(50)]
        [Column("Borough")]
        public string? Location { get; set; }

        // Recurring weekly days the cleaner is unavailable, stored as a CSV of
        // System.DayOfWeek integers (0=Sunday … 6=Saturday), e.g. "1,2" = Mon & Tue.
        // No hours — a marked weekday means the cleaner is busy that whole day.
        [StringLength(50)]
        public string? BusyDaysOfWeek { get; set; }

        public bool AlreadyWorkedWithUs { get; set; } = false;

        [StringLength(100)]
        public string? Nationality { get; set; }

        public CleanerRanking Ranking { get; set; } = CleanerRanking.Standard;

        [StringLength(500)]
        public string? RestrictedReason { get; set; }

        [StringLength(1000)]
        public string? Allergies { get; set; }

        [StringLength(1000)]
        public string? Restrictions { get; set; }

        [StringLength(2000)]
        public string? MainNote { get; set; }

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

        public virtual ICollection<CleanerNote> Notes { get; set; } = new List<CleanerNote>();

        // Date ranges (inclusive) the cleaner is on vacation / away.
        public virtual ICollection<CleanerVacation> Vacations { get; set; } = new List<CleanerVacation>();
    }
}
