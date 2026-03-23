using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.Models
{
    public class OrderReminderAcknowledgment
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public int OrderId { get; set; }

        /// <summary>"start" or "end" — which reminder was acknowledged.</summary>
        [Required]
        [MaxLength(10)]
        public string Type { get; set; } = string.Empty;

        /// <summary>The admin who acknowledged.</summary>
        [Required]
        public int AcknowledgedByUserId { get; set; }

        public DateTime AcknowledgedAt { get; set; } = DateTime.UtcNow;

        /// <summary>When the reminder was first triggered (NY time).</summary>
        public DateTime TriggeredAt { get; set; }

        // Navigation
        public Order Order { get; set; } = null!;
        public User AcknowledgedByUser { get; set; } = null!;
    }
}
