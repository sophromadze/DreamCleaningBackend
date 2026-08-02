using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    /// <summary>
    /// One MARGINAL rate band for a service. Tiers are applied to the BILLABLE quantity — the
    /// selected quantity after the ServiceThreshold allowance has been subtracted — not to the
    /// raw selection, and each band prices only the slice of that billable quantity falling
    /// inside it. The top band is never applied to the whole amount.
    ///
    /// A service with NO tier rows falls back to its own flat Service.Cost / Service.TimeDuration
    /// across the entire billable quantity, which is the pre-refactor behaviour that bedrooms,
    /// bathrooms, cleaners and hours all still rely on.
    /// </summary>
    public class ServiceRateTier
    {
        public int Id { get; set; }

        public int ServiceId { get; set; }
        public virtual Service Service { get; set; }

        /// <summary>
        /// Billable quantity at which this band starts, i.e. measured ABOVE the included
        /// allowance, not in absolute units. The lowest band must be 0.
        /// </summary>
        [Column(TypeName = "decimal(18,2)")]
        public decimal FromQuantity { get; set; }

        /// <summary>
        /// Cost per unit within this band.
        /// decimal(18,4), NOT (18,2): the shipped sqft tiers use 0.135, which a 2-decimal column
        /// would round to 0.14 and shift every large-home quote. The deliberate 0.11 (rounded up
        /// from 0.10875) stays 0.11 — that is a chosen VALUE, not a column limitation.
        /// </summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal Cost { get; set; }

        /// <summary>
        /// Minutes per unit within this band.
        /// decimal(18,4) for the same reason as Cost — the shipped top tier is 0.145.
        /// </summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal TimeDuration { get; set; }

        public int DisplayOrder { get; set; }

        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }
}
