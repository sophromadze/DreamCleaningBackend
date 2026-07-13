using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.Models
{
    public enum BlogPostStatus
    {
        Draft = 0,
        PendingReview = 1,
        Published = 2,
        Archived = 3
    }

    /// <summary>
    /// A blog article. Content is authored/generated as Markdown; the sanitized HTML
    /// rendering is stored alongside it so the public API never renders Markdown at
    /// request time and never serves unsanitized HTML.
    /// </summary>
    public class BlogPost
    {
        public int Id { get; set; }

        [Required]
        [StringLength(200)]
        public string Title { get; set; } = string.Empty;

        /// <summary>URL segment. Lowercase ASCII + hyphens, unique. Editable in admin
        /// while Draft/PendingReview; locked once published (Google has the URL).</summary>
        [Required]
        [StringLength(220)]
        public string Slug { get; set; } = string.Empty;

        /// <summary>~160 chars. Shown on list cards and used as the meta-description fallback.</summary>
        [StringLength(300)]
        public string Excerpt { get; set; } = string.Empty;

        /// <summary>Source of truth the admin editor works on.</summary>
        public string ContentMarkdown { get; set; } = string.Empty;

        /// <summary>Sanitized HTML rendered from ContentMarkdown on every save (allowlist sanitizer).</summary>
        public string ContentHtml { get; set; } = string.Empty;

        [StringLength(70)]
        public string? MetaTitle { get; set; }

        [StringLength(200)]
        public string? MetaDescription { get; set; }

        /// <summary>Relative path under the uploads root, e.g. /blog-images/xyz.jpg.</summary>
        [StringLength(500)]
        public string? FeaturedImagePath { get; set; }

        [StringLength(200)]
        public string? FeaturedImageAlt { get; set; }

        public BlogPostStatus Status { get; set; } = BlogPostStatus.Draft;

        [StringLength(50)]
        public string Category { get; set; } = "Guides";

        /// <summary>Comma-separated. Deliberately simple — no join table.</summary>
        [StringLength(500)]
        public string? Tags { get; set; }

        [StringLength(100)]
        public string AuthorName { get; set; } = "Dream Cleaning Team";

        public bool IsAiGenerated { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>Set on first publish only — re-publishing after unpublish keeps the original date.</summary>
        public DateTime? PublishedAt { get; set; }

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>Incremented fire-and-forget on public single-post fetches (bots included).</summary>
        public int ViewCount { get; set; }
    }
}
