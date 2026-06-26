using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.DTOs
{
    // ── Lead DTOs ──

    public class LeadDto
    {
        public int Id { get; set; }
        public string? FirstName { get; set; }
        public string? LastName { get; set; }
        public string FullName { get; set; } = string.Empty;
        public string? Email { get; set; }
        public string? Phone { get; set; }
        public string? ServiceAddress { get; set; }
        public string? CleaningType { get; set; }
        public string Type { get; set; } = "Residential";
        public string? Message { get; set; }
        public string Stage { get; set; } = "New";
        public string Source { get; set; } = "Manual";
        public decimal? EstimatedValue { get; set; }
        public int? AssignedToAdminId { get; set; }
        public string? AssignedToAdminName { get; set; }
        public int? ClientId { get; set; }
        public int? ConvertedOrderId { get; set; }
        public string? LostReason { get; set; }
        public DateTime? NextFollowUpDate { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime LastActivityAt { get; set; }
    }

    /// <summary>Lead with its timeline — returned by the detail endpoint.</summary>
    public class LeadDetailDto : LeadDto
    {
        public List<LeadActivityDto> Activities { get; set; } = new();
    }

    /// <summary>One pipeline column (stage) plus the leads currently in it.</summary>
    public class LeadPipelineColumnDto
    {
        public string Stage { get; set; } = string.Empty;
        public int Count { get; set; }
        public decimal TotalEstimatedValue { get; set; }
        public List<LeadDto> Leads { get; set; } = new();
    }

    public class LeadActivityDto
    {
        public int Id { get; set; }
        public int LeadId { get; set; }
        public string Type { get; set; } = "Note";
        public string? Content { get; set; }
        public int? AdminId { get; set; }
        public string? AdminName { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class CreateLeadDto
    {
        [StringLength(100)]
        public string? FirstName { get; set; }

        [StringLength(100)]
        public string? LastName { get; set; }

        [StringLength(255)]
        public string? Email { get; set; }

        [StringLength(50)]
        public string? Phone { get; set; }

        [StringLength(500)]
        public string? ServiceAddress { get; set; }

        [StringLength(100)]
        public string? CleaningType { get; set; }

        /// <summary>Defaults to Residential when omitted. Validated against LeadType.</summary>
        [StringLength(20)]
        public string? Type { get; set; }

        [StringLength(2000)]
        public string? Message { get; set; }

        /// <summary>Defaults to Manual when omitted. Validated against LeadSource.</summary>
        [StringLength(30)]
        public string? Source { get; set; }

        [Range(0, 1000000)]
        public decimal? EstimatedValue { get; set; }

        public int? AssignedToAdminId { get; set; }

        public int? ClientId { get; set; }

        public DateTime? NextFollowUpDate { get; set; }
    }

    public class UpdateLeadDto
    {
        [StringLength(100)]
        public string? FirstName { get; set; }

        [StringLength(100)]
        public string? LastName { get; set; }

        [StringLength(255)]
        public string? Email { get; set; }

        [StringLength(50)]
        public string? Phone { get; set; }

        [StringLength(500)]
        public string? ServiceAddress { get; set; }

        [StringLength(100)]
        public string? CleaningType { get; set; }

        /// <summary>Validated against LeadType; ignored when null/invalid.</summary>
        [StringLength(20)]
        public string? Type { get; set; }

        [StringLength(2000)]
        public string? Message { get; set; }

        [Range(0, 1000000)]
        public decimal? EstimatedValue { get; set; }

        public int? AssignedToAdminId { get; set; }

        /// <summary>When true, clear the assigned admin (the HasValue gate alone can't express "unassign").</summary>
        public bool ClearAssignedAdmin { get; set; } = false;

        public DateTime? NextFollowUpDate { get; set; }

        /// <summary>When true, clear the follow-up date.</summary>
        public bool ClearNextFollowUpDate { get; set; } = false;
    }

    public class UpdateLeadStageDto
    {
        [Required]
        [StringLength(20)]
        public string Stage { get; set; } = string.Empty;

        /// <summary>Required-ish when moving to Lost — surfaced in the timeline.</summary>
        [StringLength(255)]
        public string? LostReason { get; set; }
    }

    public class CreateLeadActivityDto
    {
        [StringLength(20)]
        public string? Type { get; set; }

        [Required]
        [StringLength(2000)]
        public string Content { get; set; } = string.Empty;
    }

    /// <summary>Top-line pipeline metrics for the CRM dashboard.</summary>
    public class LeadStatsDto
    {
        public int Total { get; set; }
        public int New { get; set; }
        public int Contacted { get; set; }
        public int Quoted { get; set; }
        public int Won { get; set; }
        public int Lost { get; set; }
        public int OpenCount { get; set; }
        public decimal OpenPipelineValue { get; set; }
        /// <summary>Won / (Won + Lost), as a 0–100 percentage. Null when no closed leads yet.</summary>
        public double? ConversionRate { get; set; }
        public int DueFollowUps { get; set; }
    }
}
