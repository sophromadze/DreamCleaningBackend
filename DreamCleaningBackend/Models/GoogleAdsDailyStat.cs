using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    // Per-day Google Ads performance metrics (clicks + conversions) for the CRM "Ads" tab.
    // Money (daily spend) is NOT stored here — it lives once in the Expenses table (SourceKey
    // "googleads:yyyy-MM-dd") so ad spend keeps a single source of truth. This table only holds
    // the metrics that have no other home. One row per account-timezone calendar day.
    public class GoogleAdsDailyStat
    {
        [Key]
        public int Id { get; set; }

        // Account-timezone (Eastern) calendar date, stored date-only exactly as Google reports it.
        public DateTime Date { get; set; }

        public int Clicks { get; set; }

        // Google Ads conversions are fractional (attribution can yield e.g. 3.5), so decimal.
        [Column(TypeName = "decimal(18,2)")]
        public decimal Conversions { get; set; }

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
