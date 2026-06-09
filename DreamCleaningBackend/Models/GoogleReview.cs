using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.Models
{
    /// <summary>
    /// A Google review pulled from the Google Business Profile API and persisted locally.
    /// Synced periodically by <c>GoogleReviewSyncService</c>: reviews added on Google are
    /// inserted, reviews deleted on Google are removed. <see cref="IsHidden"/> is owned by
    /// the admin (never overwritten by sync) so a specific review can be hidden from the site.
    /// </summary>
    public class GoogleReview
    {
        /// <summary>Stable Google review id (primary key). Survives across syncs.</summary>
        [Key]
        [MaxLength(255)]
        public string ReviewId { get; set; } = string.Empty;

        [MaxLength(255)]
        public string AuthorName { get; set; } = string.Empty;

        [MaxLength(1000)]
        public string? ProfilePhotoUrl { get; set; }

        /// <summary>1–5 stars.</summary>
        public int Rating { get; set; }

        public string? Text { get; set; }

        /// <summary>Owner's public reply to the review, if any.</summary>
        public string? ReplyText { get; set; }

        /// <summary>When the review was created on Google (UTC).</summary>
        public DateTime CreateTime { get; set; }

        /// <summary>When the review was last edited on Google (UTC).</summary>
        public DateTime UpdateTime { get; set; }

        /// <summary>Admin-controlled. When true the review is excluded from public output. Never touched by sync.</summary>
        public bool IsHidden { get; set; } = false;

        /// <summary>When this row was last refreshed from Google (UTC).</summary>
        public DateTime LastSyncedAt { get; set; }
    }
}
