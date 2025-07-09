using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    public class ExtraService
    {
        public int Id { get; set; }

        [Required]
        [StringLength(100)]
        public string Name { get; set; } // e.g., "Deep Cleaning", "Same Day Service", "Window Cleaning"

        [StringLength(500)]
        public string? Description { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal Price { get; set; }

        // Duration in minutes
        public int Duration { get; set; }

        // Icon path or name
        [StringLength(100)]
        public string? Icon { get; set; }

        // Configuration
        public bool HasQuantity { get; set; } = false; // e.g., walls, windows
        public bool HasHours { get; set; } = false; // e.g., organizing service

        // Special flags
        public bool IsDeepCleaning { get; set; } = false; // Affects service pricing
        public bool IsSuperDeepCleaning { get; set; } = false; // Affects service pricing
        public bool IsSameDayService { get; set; } = false; // Affects calendar selection

        // Price multiplier for deep cleaning services
        [Column(TypeName = "decimal(18,2)")]
        public decimal PriceMultiplier { get; set; } = 1.0m;

        // Service Type relationship (which service types can use this extra service)
        public int? ServiceTypeId { get; set; }
        public virtual ServiceType? ServiceType { get; set; }

        // If null, available for all service types
        public bool IsAvailableForAll { get; set; } = true;

        public bool IsActive { get; set; } = true;
        public int DisplayOrder { get; set; }

        // Navigation properties
        public virtual ICollection<OrderExtraService> OrderExtraServices { get; set; } = new List<OrderExtraService>();

        // Audit fields
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }
}