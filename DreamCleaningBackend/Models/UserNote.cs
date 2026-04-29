using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    /// <summary>
    /// Multi-row admin notes attached to a user. Type discriminates between general notes
    /// and follow-up notes. Stored in one table per the product spec, exposed as two
    /// separate streams in the UI.
    /// </summary>
    public class UserNote
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public int UserId { get; set; }

        [ForeignKey("UserId")]
        public virtual User User { get; set; } = null!;

        /// <summary>"General" or "FollowUp". Stored as string for forward-compat with new types.</summary>
        [Required]
        [StringLength(20)]
        public string Type { get; set; } = "General";

        [Required]
        [StringLength(4000)]
        public string Content { get; set; } = string.Empty;

        /// <summary>Optional individual offer/suggestion for the user's next service. Only used when Type == "FollowUp".</summary>
        [StringLength(500)]
        public string? NextOffer { get; set; }

        public int? CreatedByAdminId { get; set; }

        [ForeignKey("CreatedByAdminId")]
        public virtual User? CreatedByAdmin { get; set; }

        [StringLength(100)]
        public string? CreatedByAdminName { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? UpdatedAt { get; set; }
    }
}
