using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.Models
{
    public enum BlogTopicStatus
    {
        Queued = 0,
        Generated = 1,
        Skipped = 2
    }

    /// <summary>
    /// The generation queue. The scheduler (and the manual "Generate Now" button) takes
    /// the highest-priority Queued topic, produces a PendingReview draft, and marks the
    /// topic Generated with a link back to the created post.
    /// </summary>
    public class BlogTopic
    {
        public int Id { get; set; }

        [Required]
        [StringLength(300)]
        public string TopicTitle { get; set; } = string.Empty;

        [StringLength(200)]
        public string? TargetKeyword { get; set; }

        /// <summary>Extra context/angle passed to the generator verbatim.</summary>
        [StringLength(1000)]
        public string? Notes { get; set; }

        public BlogTopicStatus Status { get; set; } = BlogTopicStatus.Queued;

        /// <summary>Lower = generated sooner.</summary>
        public int Priority { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime? GeneratedAt { get; set; }

        public int? GeneratedBlogPostId { get; set; }
        public BlogPost? GeneratedBlogPost { get; set; }
    }
}
