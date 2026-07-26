using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.Models
{
    /// <summary>
    /// Aggregated first-party visit counts for the CRM "Ads" tab funnel (sessions → booked orders).
    /// One row per (NY calendar day × channel × source × medium × campaign); the public session-log
    /// endpoint upsert-increments <see cref="Sessions"/> when a NEW session is classified client-side
    /// (~one increment per session, not per pageview). Aggregated by design so the table stays small
    /// regardless of traffic. NO PII is stored (no IP / user / device id) — just counts.
    ///
    /// These are RAW first-party counts, not bot-filtered like GA4, so they run higher than GA4
    /// sessions and are a funnel-shape indicator, not a source of truth.
    /// </summary>
    public class SessionDailyStat
    {
        [Key]
        public int Id { get; set; }

        // NY calendar day (server-stamped at insert) so it lines up with the rest of the Ads tab.
        public DateTime Date { get; set; }

        [Required]
        [StringLength(50)]
        public string Channel { get; set; } = string.Empty;

        [StringLength(200)]
        public string? Source { get; set; }

        [StringLength(100)]
        public string? Medium { get; set; }

        [StringLength(200)]
        public string? Campaign { get; set; }

        public int Sessions { get; set; }

        /// <summary>
        /// Origin of the count: "live" = raw first-party (our dc_session beacon, same-day, not
        /// bot-filtered) or "ga4" = reconciled/backfilled from GA4 (bot-filtered, finalized). A day is
        /// treated as PROVISIONAL for the UI whenever it still has any non-"ga4" rows. Defaults to
        /// "live" (the live-capture path writes today's rows); the GA4 session sync flips finalized
        /// days to "ga4". Kept as a short string (not an enum) to match the other stat tables' style.
        /// (Named "Origin", not "Source", because <see cref="Source"/> above is the UTM source.)
        /// </summary>
        [StringLength(20)]
        public string Origin { get; set; } = "live";

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
