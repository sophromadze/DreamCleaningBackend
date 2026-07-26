using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    // One row per (day, search query) from Google Search Console's Search Analytics — the ORGANIC
    // queries people typed to reach the site, with impressions/clicks/CTR/average position. Search
    // Console reports organic search only, so no channel filtering is needed. Query text is free-form
    // (what the user typed). Dates are stored exactly as the API reports them (date-only). Upsert is
    // done by query on (Date, Query) rather than a unique index (query length + NULL-free free text),
    // mirroring SessionDailyStat's approach.
    public class SearchConsoleDailyStat
    {
        [Key]
        public int Id { get; set; }

        public DateTime Date { get; set; }

        [MaxLength(300)]
        public string Query { get; set; } = string.Empty;

        public int Clicks { get; set; }
        public int Impressions { get; set; }

        // CTR is the fraction 0..1 exactly as Search Console reports it (clicks ÷ impressions).
        [Column(TypeName = "decimal(9,4)")]
        public decimal Ctr { get; set; }

        // Average position (1-based rank; lower is better).
        [Column(TypeName = "decimal(9,2)")]
        public decimal Position { get; set; }

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
