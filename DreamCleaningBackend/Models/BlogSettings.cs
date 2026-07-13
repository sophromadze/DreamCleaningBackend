using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.Models
{
    /// <summary>
    /// Single-row runtime settings for the blog system (same pattern as ChatAgentSettings:
    /// admin-toggleable without a redeploy, row created on first read). Interval/hour and
    /// the model stay in appsettings (Blog:*) — they change rarely and only with a deploy.
    /// </summary>
    public class BlogSettings
    {
        public int Id { get; set; }

        /// <summary>Master switch for the scheduled generator. Deliberately defaults to
        /// false — the owner flips it in the admin Blog → Settings tab when ready.</summary>
        public bool AutoGenerateEnabled { get; set; } = false;

        /// <summary>Master switch for PUBLIC visibility (/blog pages, public API, header
        /// link, sitemap). Defaults to false: drafting/publishing in the admin keeps
        /// working, but visitors see a "coming soon" page until the owner flips this.
        /// Replaces the old header.component.html manual edit + redeploy step.</summary>
        public bool PublicVisible { get; set; } = false;

        /// <summary>Generation model override (admin-selectable, validated against
        /// AnthropicModelCatalog). Null/empty → fall back to Blog:GenerationModel.</summary>
        [StringLength(100)]
        public string? GenerationModel { get; set; }

        /// <summary>Last scheduled-generation attempt (UTC), success or failure.</summary>
        public DateTime? LastRunAt { get; set; }

        /// <summary>Human-readable outcome of the last run ("OK — created draft #12" /
        /// "Failed: ..."), surfaced in the admin Settings tab so silent failures are visible.</summary>
        [StringLength(1000)]
        public string? LastRunResult { get; set; }

        public DateTime? UpdatedAt { get; set; }

        [StringLength(255)]
        public string? UpdatedByEmail { get; set; }
    }
}
