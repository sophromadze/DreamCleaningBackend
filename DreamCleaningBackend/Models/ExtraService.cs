using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using DreamCleaningBackend.Helpers;

namespace DreamCleaningBackend.Models
{
    public class ExtraService
    {
        public int Id { get; set; }

        [Required]
        [StringLength(100)]
        public string Name { get; set; } // e.g., "Deep Cleaning", "Same Day Service", "Window Cleaning"

        /// <summary>
        /// Stable, admin-assigned identifier for code that must recognise a specific extra
        /// ("extra-cleaners" changes the price, "cleaning-supplies" changes the checklist, the FAQ
        /// reads the "cleaning-supplies" and "vacuum-cleaner" prices). Name is freely editable and
        /// Id differs between databases, so neither may be used for that.
        ///
        /// Unlike ServiceType.ServiceKey it is NOT unique across the table: the catalogue keeps
        /// per-service-type COPIES of the same extra (Residential and Move In/Out each own an
        /// "Extra Cleaners" row), and the copies share the key. It is unique within what one
        /// service type can see - its own rows plus the universal ones. Rules and messages live in
        /// Helpers/ExtraServiceKeyPolicy.cs, the known keys in Helpers/ExtraServiceKeys.cs.
        /// NULL = no key; code then falls back to the legacy name match (see ExtraServiceKeys).
        /// </summary>
        [StringLength(ExtraServiceKeyPolicy.MaxLength)]
        public string? ExtraServiceKey { get; set; }

        [StringLength(500)]
        public string? Description { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal Price { get; set; }

        // Duration in minutes
        public decimal Duration { get; set; }

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