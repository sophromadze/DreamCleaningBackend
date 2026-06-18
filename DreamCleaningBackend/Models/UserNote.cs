using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    /// <summary>
    /// Multi-row admin notes attached to a user. Only general notes exist today —
    /// the Type column stays as a string for forward-compat with new types.
    /// (Follow-up notes and the NextOffer field were removed 2026-06.)
    /// </summary>
    public class UserNote
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public int UserId { get; set; }

        [ForeignKey("UserId")]
        public virtual User User { get; set; } = null!;

        /// <summary>Always "General" today. Stored as string for forward-compat with new types.</summary>
        [Required]
        [StringLength(20)]
        public string Type { get; set; } = "General";

        [Required]
        [StringLength(4000)]
        public string Content { get; set; } = string.Empty;

        public int? CreatedByAdminId { get; set; }

        [ForeignKey("CreatedByAdminId")]
        public virtual User? CreatedByAdmin { get; set; }

        [StringLength(100)]
        public string? CreatedByAdminName { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? UpdatedAt { get; set; }
    }
}
