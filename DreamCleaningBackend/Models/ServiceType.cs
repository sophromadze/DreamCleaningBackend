using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    public class ServiceType
    {
        public int Id { get; set; }

        [Required]
        [StringLength(100)]
        public string Name { get; set; } // e.g., "Residential Cleaning", "Office Cleaning"

        [Column(TypeName = "decimal(18,2)")]
        public decimal BasePrice { get; set; } // Base price for this service type

        [StringLength(500)]
        public string? Description { get; set; }

        // Explicitly decimal(18,2) to match Service.TimeDuration. Without the annotation
        // Pomelo mapped this to the provider default (decimal(65,30)); the values are whole
        // minutes, so narrowing is lossless.
        [Column(TypeName = "decimal(18,2)")]
        public decimal TimeDuration { get; set; } = 90;

        /// <summary>
        /// Floor for the base-price + services portion of the subtotal. Extras and the
        /// deep-cleaning fee stack ON TOP of it rather than being absorbed by it, so the floor
        /// protects the cleaning itself. 0 = no floor (the default, and what every service type
        /// other than Residential ships with).
        /// </summary>
        [Column(TypeName = "decimal(18,2)")]
        public decimal MinimumPrice { get; set; } = 0m;
        public bool IsActive { get; set; } = true;
        public int DisplayOrder { get; set; }
        public bool HasPoll { get; set; } = false;
        public bool IsCustom { get; set; } = false;

        // Navigation properties
        public virtual ICollection<Service> Services { get; set; } = new List<Service>();
        public virtual ICollection<ExtraService> ExtraServices { get; set; } = new List<ExtraService>();
        public virtual ICollection<Order> Orders { get; set; } = new List<Order>();
        public virtual ICollection<PollQuestion> PollQuestions { get; set; } = new List<PollQuestion>();

        // Audit fields
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }
}