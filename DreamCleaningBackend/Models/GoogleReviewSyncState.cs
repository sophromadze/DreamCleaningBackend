using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    /// <summary>
    /// Single-row table (Id = <see cref="SingletonId"/>) holding the Google Business Profile
    /// aggregate and the health of <c>GoogleReviewSyncService</c>.
    /// <see cref="TotalReviewCount"/> / <see cref="AverageRating"/> are Google's own
    /// totalReviewCount / averageRating from reviews.list — the single source of truth for every
    /// review count and rating shown on the site (the GoogleReviews row count is NOT, because
    /// it excludes admin-hidden reviews and the site further filters star-only/price reviews).
    /// </summary>
    public class GoogleReviewSyncState
    {
        public const int SingletonId = 1;

        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.None)]
        public int Id { get; set; } = SingletonId;

        /// <summary>Google's totalReviewCount as of the last successful sync. Null until the first one.</summary>
        public int? TotalReviewCount { get; set; }

        /// <summary>Google's averageRating as of the last successful sync. Null until the first one.</summary>
        public double? AverageRating { get; set; }

        /// <summary>When the last sync completed successfully (UTC).</summary>
        public DateTime? LastSuccessAt { get; set; }

        /// <summary>When a sync was last attempted, successful or not (UTC).</summary>
        public DateTime? LastAttemptAt { get; set; }

        /// <summary>Error from the most recent failed sync. Cleared by a successful sync.</summary>
        [MaxLength(2000)]
        public string? LastError { get; set; }

        public DateTime? LastErrorAt { get; set; }

        /// <summary>Failed syncs since the last success. Reset to 0 by a successful sync.</summary>
        public int ConsecutiveFailures { get; set; }

        /// <summary>
        /// When the failure alert email went out for the current failure streak. While set, no
        /// further alerts are sent; a successful sync clears it so the next streak alerts again.
        /// </summary>
        public DateTime? AlertSentAt { get; set; }
    }
}
