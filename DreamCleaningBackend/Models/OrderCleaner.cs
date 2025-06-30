using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.Models
{
    public class OrderCleaner
    {
        public int Id { get; set; }

        [Required]
        public int OrderId { get; set; }
        public virtual Order Order { get; set; }

        [Required]
        public int CleanerId { get; set; }
        public virtual User Cleaner { get; set; }

        public DateTime AssignedAt { get; set; } = DateTime.UtcNow;
        public int AssignedBy { get; set; } // Admin/Moderator who assigned
        public virtual User AssignedByUser { get; set; }

        // Tips for cleaner (visible to cleaner, not amount)
        [StringLength(1000)]
        public string? TipsForCleaner { get; set; }
    }
}