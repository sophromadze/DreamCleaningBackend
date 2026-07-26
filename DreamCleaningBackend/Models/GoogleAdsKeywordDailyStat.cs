using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    // One row per (day, search term) from Google Ads' search_term_view — the ACTUAL queries users
    // typed that triggered our ads (not the bid keywords), with clicks/impressions/cost/conversions.
    // Complements the account-level daily totals in GoogleAdsDailyStat + spend in Expenses; this table
    // is the keyword-level detail for the Keywords dashboard. Dates are the account timezone (Eastern)
    // calendar day as Google reports it. Upsert is by query on (Date, SearchTerm).
    public class GoogleAdsKeywordDailyStat
    {
        [Key]
        public int Id { get; set; }

        public DateTime Date { get; set; }

        [MaxLength(300)]
        public string SearchTerm { get; set; } = string.Empty;

        public int Clicks { get; set; }
        public int Impressions { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal CostUsd { get; set; }

        // Google Ads conversions are fractional (attribution can yield e.g. 3.5), so decimal.
        [Column(TypeName = "decimal(18,2)")]
        public decimal Conversions { get; set; }

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
