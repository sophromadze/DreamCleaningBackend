using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    public class TaskActivityLog
    {
        [Key]
        public long Id { get; set; }

        [Required]
        [StringLength(50)]
        public string EntityType { get; set; } = string.Empty; // SharedTask, PersonalTask, ClientInteraction, HandoverNote

        public int EntityId { get; set; }

        [StringLength(200)]
        public string? EntityTitle { get; set; } // Title or client name for quick reference

        [Required]
        [StringLength(30)]
        public string Action { get; set; } = string.Empty; // Created, Updated, Deleted, StatusChanged

        [Column(TypeName = "TEXT")]
        public string? Changes { get; set; } // JSON describing what changed (field: { from, to })

        public int AdminId { get; set; }

        [Required]
        [StringLength(200)]
        public string AdminName { get; set; } = string.Empty;

        [Required]
        [StringLength(20)]
        public string AdminRole { get; set; } = string.Empty;

        public virtual User? Admin { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
