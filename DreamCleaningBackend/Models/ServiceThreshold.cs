using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    /// <summary>
    /// Maps a SOURCE service's selected quantity to a quantity of the OWNING service that is
    /// included at no charge — e.g. "sqft, when bedrooms = 2, includes 850".
    ///
    /// These rows are the single source of truth for two things that used to be a hardcoded
    /// switch (getSquareFeetForBedrooms): the free allowance used in billing, AND the slider
    /// minimum shown to the customer. Keeping them the same data is what guarantees the default
    /// configuration for any bedroom count always costs exactly zero extra square footage.
    ///
    /// Only consulted when the owning Service has ChargeAboveThreshold = true.
    /// </summary>
    public class ServiceThreshold
    {
        public int Id { get; set; }

        /// <summary>The service that RECEIVES the included allowance (e.g. the "sqft" service).</summary>
        public int ServiceId { get; set; }
        public virtual Service Service { get; set; }

        /// <summary>
        /// The service whose selected quantity picks the row (e.g. the "bedrooms" service).
        /// An int FK rather than a ServiceKey string on purpose: a key string would be silently
        /// orphaned by an admin renaming the source service, and an orphaned threshold bills a
        /// large home from zero. The FK is delete-restricted for the same reason.
        /// </summary>
        public int SourceServiceId { get; set; }
        public virtual Service SourceService { get; set; }

        /// <summary>Quantity of the source service (0 = Studio, 1 = 1BR, ...).</summary>
        public int SourceQuantity { get; set; }

        /// <summary>Units of the OWNING service included for free at that source quantity.</summary>
        [Column(TypeName = "decimal(18,2)")]
        public decimal IncludedQuantity { get; set; }

        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }
}
