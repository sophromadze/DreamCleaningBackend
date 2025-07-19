using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    public class OrderCleaner
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public int OrderId { get; set; }

        [ForeignKey("OrderId")]
        public virtual Order Order { get; set; }

        [Required]
        public int CleanerId { get; set; }

        [ForeignKey("CleanerId")]
        public virtual User Cleaner { get; set; }

        public DateTime AssignedAt { get; set; } = DateTime.UtcNow;

        [Required]
        public int AssignedBy { get; set; } // Admin/Moderator who assigned

        [ForeignKey("AssignedBy")]
        public virtual User AssignedByUser { get; set; }

        // Tips for cleaner (visible to cleaners)
        [StringLength(1000)]
        public string? TipsForCleaner { get; set; }
    }
}