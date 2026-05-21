using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.DTOs
{
    // ── AdminTask DTOs ──

    public class AdminTaskDto
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string Priority { get; set; } = "Normal";
        public string Status { get; set; } = "Todo";
        public DateTime? DueDate { get; set; }
        public string? ClientName { get; set; }
        public string? ClientEmail { get; set; }
        public string? ClientPhone { get; set; }
        public int? ClientId { get; set; }
        public int? OrderId { get; set; }
        public int CreatedByAdminId { get; set; }
        public string CreatedByAdminName { get; set; } = string.Empty;
        public string CreatedByAdminRole { get; set; } = string.Empty;
        public string? CompletionNote { get; set; }
        public DateTime? CompletedAt { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public class CreateAdminTaskDto
    {
        [Required]
        [StringLength(500)]
        public string Title { get; set; } = string.Empty;

        [StringLength(2000)]
        public string? Description { get; set; }

        [StringLength(20)]
        public string Priority { get; set; } = "Normal";

        public DateTime? DueDate { get; set; }

        [StringLength(200)]
        public string? ClientName { get; set; }

        [StringLength(255)]
        public string? ClientEmail { get; set; }

        [StringLength(50)]
        public string? ClientPhone { get; set; }

        public int? ClientId { get; set; }

        public int? OrderId { get; set; }
    }

    public class UpdateAdminTaskDto
    {
        [StringLength(500)]
        public string? Title { get; set; }

        [StringLength(2000)]
        public string? Description { get; set; }

        [StringLength(20)]
        public string? Priority { get; set; }

        [StringLength(20)]
        public string? Status { get; set; }

        public DateTime? DueDate { get; set; }

        /// <summary>When true, null out the existing DueDate column. Needed because the DTO
        /// cannot distinguish "field omitted from request" from "field set to null on the
        /// wire" — the existing DueDate.HasValue gate preserves the old value in both cases.
        /// Frontend sets this when the user clears a previously-set due date.</summary>
        public bool ClearDueDate { get; set; } = false;

        [StringLength(200)]
        public string? ClientName { get; set; }

        [StringLength(255)]
        public string? ClientEmail { get; set; }

        [StringLength(50)]
        public string? ClientPhone { get; set; }

        public int? ClientId { get; set; }

        public int? OrderId { get; set; }

        [StringLength(2000)]
        public string? CompletionNote { get; set; }
    }

    public class UpdateTaskStatusDto
    {
        [Required]
        [StringLength(20)]
        public string Status { get; set; } = string.Empty;

        [StringLength(2000)]
        public string? CompletionNote { get; set; }
    }

    // ── ClientInteraction DTOs ──

    public class ClientInteractionDto
    {
        public int Id { get; set; }
        public string ClientName { get; set; } = string.Empty;
        public string? ClientPhone { get; set; }
        public string? ClientEmail { get; set; }
        public int? ClientId { get; set; }
        public DateTime InteractionDate { get; set; }
        public string AdminName { get; set; } = string.Empty;
        public string AdminRole { get; set; } = string.Empty;
        public int AdminId { get; set; }
        public string Type { get; set; } = string.Empty;
        public string? Notes { get; set; }
        public string Status { get; set; } = "Pending";
        public DateTime CreatedAt { get; set; }
    }

    public class CreateClientInteractionDto
    {
        [Required]
        [StringLength(200)]
        public string ClientName { get; set; } = string.Empty;

        [StringLength(50)]
        public string? ClientPhone { get; set; }

        [StringLength(255)]
        public string? ClientEmail { get; set; }

        public int? ClientId { get; set; }

        [Required]
        [StringLength(100)]
        public string Type { get; set; } = string.Empty;

        [StringLength(2000)]
        public string? Notes { get; set; }

        [StringLength(20)]
        public string Status { get; set; } = "Pending";
    }

    public class UpdateClientInteractionDto
    {
        [StringLength(200)]
        public string? ClientName { get; set; }

        [StringLength(50)]
        public string? ClientPhone { get; set; }

        [StringLength(255)]
        public string? ClientEmail { get; set; }

        [StringLength(100)]
        public string? Type { get; set; }

        [StringLength(2000)]
        public string? Notes { get; set; }

        [StringLength(20)]
        public string? Status { get; set; }
    }

    // ── HandoverNote DTOs ──

    public class HandoverNoteDto
    {
        public int Id { get; set; }
        public string Content { get; set; } = string.Empty;
        public string AdminName { get; set; } = string.Empty;
        public string AdminRole { get; set; } = string.Empty;
        public int AdminId { get; set; }
        public string TargetAudience { get; set; } = "ForNextAdmin";
        public DateTime CreatedAt { get; set; }
        public int TaskCount { get; set; }
        public int InteractionCount { get; set; }
    }

    public class CreateHandoverNoteDto
    {
        [Required]
        public string Content { get; set; } = string.Empty;

        [StringLength(50)]
        public string TargetAudience { get; set; } = "ForNextAdmin";
    }

    public class UpdateHandoverNoteDto
    {
        public string? Content { get; set; }

        [StringLength(50)]
        public string? TargetAudience { get; set; }
    }

    // ── PersonalAdminTask DTOs ──

    public class PersonalAdminTaskDto
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string Priority { get; set; } = "Normal";
        public string Status { get; set; } = "Todo";
        public DateTime? DueDate { get; set; }
        public int AssignedToAdminId { get; set; }
        public string AssignedToAdminName { get; set; } = string.Empty;
        public string AssignedToAdminRole { get; set; } = string.Empty;
        public int CreatedByAdminId { get; set; }
        public string CreatedByAdminName { get; set; } = string.Empty;
        public string CreatedByAdminRole { get; set; } = string.Empty;
        public string? CompletionNote { get; set; }
        public DateTime? CompletedAt { get; set; }
        public bool CheckedByCreator { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public class CreatePersonalAdminTaskDto
    {
        [Required]
        [StringLength(500)]
        public string Title { get; set; } = string.Empty;

        [StringLength(2000)]
        public string? Description { get; set; }

        [StringLength(20)]
        public string Priority { get; set; } = "Normal";

        public DateTime? DueDate { get; set; }

        [Required]
        public List<int> AssignedToAdminIds { get; set; } = new();
    }

    public class UpdatePersonalAdminTaskDto
    {
        [StringLength(500)]
        public string? Title { get; set; }

        [StringLength(2000)]
        public string? Description { get; set; }

        [StringLength(20)]
        public string? Priority { get; set; }

        [StringLength(20)]
        public string? Status { get; set; }

        public DateTime? DueDate { get; set; }

        /// <summary>Mirror of UpdateAdminTaskDto.ClearDueDate — set true to null an existing due
        /// date. The HasValue gate alone can't represent a clear request because "field omitted"
        /// and "field set to null" arrive identically on the wire.</summary>
        public bool ClearDueDate { get; set; } = false;

        public int? AssignedToAdminId { get; set; }

        [StringLength(2000)]
        public string? CompletionNote { get; set; }
    }

    // ── TaskActivityLog DTOs ──

    public class TaskActivityLogDto
    {
        public long Id { get; set; }
        public string EntityType { get; set; } = string.Empty;
        public int EntityId { get; set; }
        public string? EntityTitle { get; set; }
        public string Action { get; set; } = string.Empty;
        public string? Changes { get; set; }
        public int AdminId { get; set; }
        public string AdminName { get; set; } = string.Empty;
        public string AdminRole { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
    }

    // ── Search DTOs ──

    public class ClientSearchResultDto
    {
        public int Id { get; set; }
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string? Phone { get; set; }
    }

    public class OrderSearchResultDto
    {
        public int Id { get; set; }
        public string ContactFirstName { get; set; } = string.Empty;
        public string ContactLastName { get; set; } = string.Empty;
        public string? ContactEmail { get; set; }
        public string? ContactPhone { get; set; }
        public string? ServiceAddress { get; set; }
        public string ServiceTypeName { get; set; } = string.Empty;
        public DateTime ServiceDate { get; set; }
        public string Status { get; set; } = string.Empty;
    }
}
