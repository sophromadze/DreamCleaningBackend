using System.ComponentModel.DataAnnotations;
using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.DTOs
{
    public class CleanerListItemDto
    {
        public int Id { get; set; }
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public int? Age { get; set; }
        public string? Experience { get; set; }
        public bool IsExperienced { get; set; }
        public string? Phone { get; set; }
        public string? Email { get; set; }
        public string? Address { get; set; }
        public string? Location { get; set; }
        public string? OperatingAreas { get; set; }

        // Recurring weekdays the cleaner is busy (System.DayOfWeek ints, 0=Sun … 6=Sat).
        public List<int> BusyDaysOfWeek { get; set; } = new();

        public bool AlreadyWorkedWithUs { get; set; }
        public string? Nationality { get; set; }
        public CleanerRanking Ranking { get; set; }
        public string? PhotoUrl { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }

        // How this cleaner is paid their wages. Both optional — the Outgoing Payments page
        // treats them as a hint for whoever sends the money, never as a requirement.
        public CleanerPaymentMethod? PaymentMethod { get; set; }
        public string? PaymentDetails { get; set; }

        // Included in the list so the dashboard doesn't need an N+1 detail fetch per cleaner.
        public string? MainNote { get; set; }
    }

    public class CleanerDetailDto : CleanerListItemDto
    {
        public string? RestrictedReason { get; set; }
        public string? Allergies { get; set; }
        public string? Restrictions { get; set; }
        public string? DocumentUrl { get; set; }
        public CleanerDocumentType? DocumentType { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public int? CreatedByAdminId { get; set; }
        public string? CreatedByAdminName { get; set; }
        public List<CleanerNoteDto> Notes { get; set; } = new();
        public List<CleanerAssignedOrderDto> AssignedOrders { get; set; } = new();
        public List<CleanerVacationDto> Vacations { get; set; } = new();

        // ── The login account behind this cleaner, if any (Cleaner.UserId) ──
        //
        // Present so the dashboard can say who signs in as this person AND lock the email field:
        // linking copies the account address onto the record, and editing it here afterwards would
        // send the assignment mail somewhere the person does not read while the portal kept working.

        public int? LinkedUserId { get; set; }
        public string? LinkedAccountName { get; set; }

        /// <summary>The address that account signs in with; null for a no-email account.</summary>
        public string? LinkedAccountEmail { get; set; }

        /// <summary>
        /// Whether Email is read-only, answered by CleanerAccountLink.EmailIsManagedByAccount. The
        /// SERVER decides and the client renders the answer - the same predicate rejects a write, so
        /// a second client-side copy of the rule could only disagree with what the API will accept.
        /// </summary>
        public bool IsEmailManagedByAccount { get; set; }
    }

    public class CleanerVacationDto
    {
        public int? Id { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }

        [StringLength(200)]
        public string? Note { get; set; }
    }

    public class CreateCleanerDto
    {
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

        [StringLength(20)]
        public string? Phone { get; set; }

        [EmailAddress]
        [StringLength(100)]
        public string? Email { get; set; }

        [StringLength(300)]
        public string? Address { get; set; }

        [StringLength(50)]
        public string? Location { get; set; }

        [StringLength(50)]
        public string? OperatingAreas { get; set; }

        // Recurring weekdays the cleaner is busy (System.DayOfWeek ints, 0=Sun … 6=Sat).
        public List<int> BusyDaysOfWeek { get; set; } = new();

        // Replaces the cleaner's vacation ranges wholesale on create/update.
        public List<CleanerVacationDto> Vacations { get; set; } = new();

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

        public CleanerDocumentType? DocumentType { get; set; }

        /// <summary>How this cleaner is paid their wages. Optional — blank is normal.</summary>
        public CleanerPaymentMethod? PaymentMethod { get; set; }

        /// <summary>Zelle number/email, who a check is written out to, and so on. Optional.</summary>
        [StringLength(200)]
        public string? PaymentDetails { get; set; }
    }

    public class UpdateCleanerDto : CreateCleanerDto
    {
        public bool IsActive { get; set; } = true;
    }

    public class CleanerNoteDto
    {
        public int Id { get; set; }
        public int CleanerId { get; set; }
        public int? AdminId { get; set; }
        public string? AdminDisplayName { get; set; }
        public string Text { get; set; } = string.Empty;
        public int? OrderId { get; set; }
        public string? OrderPerformance { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class CreateCleanerNoteDto
    {
        [Required]
        [StringLength(4000)]
        public string Text { get; set; } = string.Empty;

        public int? OrderId { get; set; }

        [StringLength(500)]
        public string? OrderPerformance { get; set; }
    }

    public class UpdateCleanerNoteDto
    {
        [Required]
        [StringLength(4000)]
        public string Text { get; set; } = string.Empty;
    }

    public class UpsertOrderPerformanceDto
    {
        [Required]
        public int OrderId { get; set; }

        [StringLength(500)]
        public string? Performance { get; set; }

        [StringLength(4000)]
        public string? Text { get; set; }
    }

    public class CleanerAssignedOrderDto
    {
        public int OrderId { get; set; }
        public DateTime ServiceDate { get; set; }
        public string ServiceTime { get; set; } = string.Empty;
        public string? ServiceAddress { get; set; }
        public string? ServiceCity { get; set; }
        public string? ServiceTypeName { get; set; }
        public string Status { get; set; } = string.Empty;
        public DateTime AssignedAt { get; set; }
        public DateTime? AssignmentNotificationSentAt { get; set; }
    }

    public class CleanerImageUploadResultDto
    {
        public string Url { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
    }

    public class CleanerMigrationResultDto
    {
        public int MigratedCount { get; set; }
        public int SkippedCount { get; set; }
        public int TotalCandidates { get; set; }
        public List<string> Warnings { get; set; } = new();
    }
}
