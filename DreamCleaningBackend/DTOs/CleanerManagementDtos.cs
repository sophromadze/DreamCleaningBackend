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

        // Recurring weekdays the cleaner is busy (System.DayOfWeek ints, 0=Sun … 6=Sat).
        public List<int> BusyDaysOfWeek { get; set; } = new();

        public bool AlreadyWorkedWithUs { get; set; }
        public string? Nationality { get; set; }
        public CleanerRanking Ranking { get; set; }
        public string? PhotoUrl { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }

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
