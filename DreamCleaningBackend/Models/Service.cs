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

        [Column(TypeName = "decimal(18,2)")]
        public decimal TimeDuration { get; set; }

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

        // Threshold / tier billing
        // When true, the ServiceThreshold allowance is subtracted before the rate tiers are
        // applied, so the service only bills the OVERAGE. False (the default) preserves the
        // original bill-from-zero behaviour for every other service.
        public bool ChargeAboveThreshold { get; set; } = false;

        // Cost/minutes when the selected quantity is 0 (Studio is just bedrooms = 0).
        // Null on both means "not applicable" and the line prices normally.
        [Column(TypeName = "decimal(18,2)")]
        public decimal? ZeroQuantityCost { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal? ZeroQuantityDuration { get; set; }

        public bool IsActive { get; set; } = true;
        public int DisplayOrder { get; set; }

        // Navigation properties
        public virtual ICollection<OrderService> OrderServices { get; set; } = new List<OrderService>();

        /// <summary>Allowances granted TO this service (this service is the one billed above them).</summary>
        public virtual ICollection<ServiceThreshold> Thresholds { get; set; } = new List<ServiceThreshold>();

        /// <summary>Marginal rate bands for this service. Empty = flat Cost/TimeDuration.</summary>
        public virtual ICollection<ServiceRateTier> RateTiers { get; set; } = new List<ServiceRateTier>();

        // Audit fields
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }
}