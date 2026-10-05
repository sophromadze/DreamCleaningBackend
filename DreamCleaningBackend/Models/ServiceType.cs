using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using DreamCleaningBackend.Helpers;

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

        /// <summary>
        /// Whether the booking flow asks apartment/condo vs house/townhouse for this type.
        ///
        /// A FLAG rather than an inferred rule, because there is no structural way to tell some
        /// types apart: Office Cleaning and Heavy Conditional Cleaning have identical shape (same
        /// cleaner+hours services, no bedrooms, no sq.ft, HasPoll and IsCustom both false) and yet
        /// one should ask and the other should not. Matching on Id or Name to separate them is
        /// ruled out - both diverge between the local and production databases.
        ///
        /// Defaults to TRUE so every existing type keeps its current behaviour and a type added
        /// later opts in automatically. Admin-editable in the Booking Services tab, so a new
        /// hourly type can be switched off without a deploy.
        /// </summary>
        public bool CollectsPropertyType { get; set; } = true;

        /// <summary>
        /// Stable, admin-assigned identifier for code that must find a specific service type
        /// (e.g. the generated /llms.txt reads "residential" and "move-in-out" for its prices).
        ///
        /// Exists for the same reason as CollectsPropertyType: Id and Name both diverge between
        /// the local and production databases (and Name is freely editable), so neither may be
        /// used to recognise a type. Lowercase words joined by hyphens, unique when set, NULL
        /// for a type nothing needs to find. Rules live in Helpers/ServiceTypeKeyPolicy.cs.
        /// Set by hand in the Booking Services tab on each database - never seeded.
        /// </summary>
        [StringLength(ServiceTypeKeyPolicy.MaxLength)]
        public string? ServiceKey { get; set; }

        /// <summary>
        /// Marketing-only price for a type the booking calculator cannot price (today Filthy
        /// Cleaning: inspected first, priced by hand). Shown on the public pages and /llms.txt
        /// together with <see cref="DisplayPriceUnit"/>; NEVER read by any quote. NULL = no
        /// stated price, and the site says "priced after assessment". Rules live in
        /// Helpers/ServiceTypeDisplayPricePolicy.cs.
        /// </summary>
        [Column(TypeName = "decimal(18,2)")]
        public decimal? DisplayPrice { get; set; }

        /// <summary>How <see cref="DisplayPrice"/> is worded: per-hour-per-cleaner, per-hour or from. NULL with it.</summary>
        [StringLength(ServiceTypeDisplayPricePolicy.UnitMaxLength)]
        public string? DisplayPriceUnit { get; set; }

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