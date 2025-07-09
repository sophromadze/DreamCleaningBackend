using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.Models
{
    public class AuditLog
    {
        [Key]
        public long Id { get; set; }

        [Required]
        [StringLength(50)]
        public string EntityType { get; set; } // Like "User", "Order", "GiftCard"

        [Required]
        public long EntityId { get; set; } // The ID of the thing that changed

        [Required]
        [StringLength(20)]
        public string Action { get; set; } // "Create", "Update", "Delete"

        [Column(TypeName = "LONGTEXT")]
        public string? OldValues { get; set; } // What it was before (as JSON)

        [Column(TypeName = "LONGTEXT")]
        public string? NewValues { get; set; } // What it is now (as JSON)

        [Column(TypeName = "LONGTEXT")]
        public string? ChangedFields { get; set; } // Which fields changed (as JSON)

        public int? UserId { get; set; } // Who made the change
        public virtual User? User { get; set; }

        [StringLength(45)]
        public string? IpAddress { get; set; }

        [StringLength(500)]
        public string? UserAgent { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}
