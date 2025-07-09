using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    public class Service
    {
        public int Id { get; set; }

        [Required]
        [StringLength(100)]
        public string Name { get; set; } // e.g., "Bedrooms", "Bathrooms", "Square Feet", "Cleaners", "Hours"

        [StringLength(50)]
        public string ServiceKey { get; set; } // e.g., "bedrooms", "bathrooms", "sqft", "cleaners", "hours"

        [Column(TypeName = "decimal(18,2)")]
        public decimal Cost { get; set; } // Cost per unit

        // Time duration in minutes (per unit)
        public int TimeDuration { get; set; }

        // Service Type
        public int ServiceTypeId { get; set; }
        public virtual ServiceType ServiceType { get; set; }

        // UI Configuration
        [StringLength(50)]
        public string InputType { get; set; } = "dropdown"; // "dropdown", "slider", "number"

        // For dropdown options
        public int? MinValue { get; set; }
        public int? MaxValue { get; set; }
        public int? StepValue { get; set; }

        // For slider (square feet)
        public bool IsRangeInput { get; set; } = false;

        [StringLength(20)]
        public string? Unit { get; set; } // e.g., "per hour", "per cleaner", "per 100 sqft"

        // Service relationship type
        [StringLength(20)]
        public string? ServiceRelationType { get; set; } // "cleaner", "hours", null for regular

        public bool IsActive { get; set; } = true;
        public int DisplayOrder { get; set; }

        // Navigation properties
        public virtual ICollection<OrderService> OrderServices { get; set; } = new List<OrderService>();

        // Audit fields
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }
}