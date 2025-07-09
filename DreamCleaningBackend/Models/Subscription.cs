using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    public class Subscription
    {
        public int Id { get; set; }
        [Required]
        [StringLength(50)]
        public string Name { get; set; } // e.g., "One Time", "Weekly", "Bi-Weekly", "Monthly"
        [StringLength(200)]
        public string? Description { get; set; }
        // Discount percentage for this subscription
        [Column(TypeName = "decimal(5,2)")]
        public decimal DiscountPercentage { get; set; } = 0;
        // Subscription period in days (0 for one-time)
        public int SubscriptionDays { get; set; } 
        public bool IsActive { get; set; } = true;
        public int DisplayOrder { get; set; }
        // Navigation properties
        public virtual ICollection<Order> Orders { get; set; } = new List<Order>();
        // Audit fields
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }
}