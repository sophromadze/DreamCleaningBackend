using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.DTOs
{
    // ===== Public (no auth) =====

    /// <summary>Card on the /blog grid — no body content.</summary>
    public class BlogPostListItemDto
    {
        public string Title { get; set; } = string.Empty;
        public string Slug { get; set; } = string.Empty;
        public string Excerpt { get; set; } = string.Empty;
        public string? FeaturedImagePath { get; set; }
        public string? FeaturedImageAlt { get; set; }
        public string Category { get; set; } = string.Empty;
        public DateTime? PublishedAt { get; set; }
    }

    public class BlogPostListResponseDto
    {
        public List<BlogPostListItemDto> Posts { get; set; } = new();
        public int TotalCount { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
        public List<string> Categories { get; set; } = new();

        /// <summary>False while the owner keeps the blog hidden (BlogSettings.PublicVisible) —
        /// the frontend renders a "coming soon" page instead of the grid.</summary>
        public bool PublicVisible { get; set; } = true;
    }

    /// <summary>Lightweight public visibility probe — the header and blog pages share
    /// one cached call to this (see BlogStatusService on the frontend).</summary>
    public class BlogStatusDto
    {
        public bool PublicVisible { get; set; }
    }

    /// <summary>Full public post (sanitized HTML only — Markdown never leaves the admin API).</summary>
    public class BlogPostDetailDto
    {
        public string Title { get; set; } = string.Empty;
        public string Slug { get; set; } = string.Empty;
        public string Excerpt { get; set; } = string.Empty;
        public string ContentHtml { get; set; } = string.Empty;
        public string? MetaTitle { get; set; }
        public string? MetaDescription { get; set; }
        public string? FeaturedImagePath { get; set; }
        public string? FeaturedImageAlt { get; set; }
        public string Category { get; set; } = string.Empty;
        public List<string> Tags { get; set; } = new();
        public string AuthorName { get; set; } = string.Empty;
        public DateTime? PublishedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public List<BlogPostListItemDto> RelatedPosts { get; set; } = new();
    }

    // ===== Admin =====

    public class BlogPostAdminListItemDto
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Slug { get; set; } = string.Empty;
        public int Status { get; set; }
        public string Category { get; set; } = string.Empty;
        public bool IsAiGenerated { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? PublishedAt { get; set; }
        public int ViewCount { get; set; }
    }

    public class BlogPostAdminDto
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Slug { get; set; } = string.Empty;
        public string Excerpt { get; set; } = string.Empty;
        public string ContentMarkdown { get; set; } = string.Empty;
        public string ContentHtml { get; set; } = string.Empty;
        public string? MetaTitle { get; set; }
        public string? MetaDescription { get; set; }
        public string? FeaturedImagePath { get; set; }
        public string? FeaturedImageAlt { get; set; }
        public int Status { get; set; }
        public string Category { get; set; } = string.Empty;
        public string? Tags { get; set; }
        public string AuthorName { get; set; } = string.Empty;
        public bool IsAiGenerated { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? PublishedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public int ViewCount { get; set; }
    }

    /// <summary>Create/update payload. Status transitions are NOT accepted here —
    /// publish/unpublish go through their dedicated endpoints.</summary>
    public class SaveBlogPostDto
    {
        [Required]
        [StringLength(200)]
        public string Title { get; set; } = string.Empty;

        /// <summary>Optional; auto-generated from Title when empty. Ignored on updates
        /// to already-published posts (slug is locked once Google has the URL).</summary>
        [StringLength(220)]
        public string? Slug { get; set; }

        [StringLength(300)]
        public string Excerpt { get; set; } = string.Empty;

        [Required]
        public string ContentMarkdown { get; set; } = string.Empty;

        [StringLength(70)]
        public string? MetaTitle { get; set; }

        [StringLength(200)]
        public string? MetaDescription { get; set; }

        [StringLength(500)]
        public string? FeaturedImagePath { get; set; }

        [StringLength(200)]
        public string? FeaturedImageAlt { get; set; }

        [StringLength(50)]
        public string Category { get; set; } = "Guides";

        [StringLength(500)]
        public string? Tags { get; set; }

        [StringLength(100)]
        public string AuthorName { get; set; } = "Dream Cleaning Team";
    }

    public class BlogTopicDto
    {
        public int Id { get; set; }
        public string TopicTitle { get; set; } = string.Empty;
        public string? TargetKeyword { get; set; }
        public string? Notes { get; set; }
        public int Status { get; set; }
        public int Priority { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? GeneratedAt { get; set; }
        public int? GeneratedBlogPostId { get; set; }
    }

    public class SaveBlogTopicDto
    {
        [Required]
        [StringLength(300)]
        public string TopicTitle { get; set; } = string.Empty;

        [StringLength(200)]
        public string? TargetKeyword { get; set; }

        [StringLength(1000)]
        public string? Notes { get; set; }
    }

    public class ReorderBlogTopicsDto
    {
        /// <summary>Queued topic ids in the desired order (first = next to generate).</summary>
        [Required]
        public List<int> TopicIds { get; set; } = new();
    }

    /// <summary>Manual generation trigger: either an existing queue topic or a freeform one.</summary>
    public class GenerateBlogPostDto
    {
        public int? TopicId { get; set; }

        [StringLength(300)]
        public string? FreeformTopic { get; set; }

        [StringLength(200)]
        public string? TargetKeyword { get; set; }

        [StringLength(1000)]
        public string? Notes { get; set; }
    }

    public class SuggestedTopicDto
    {
        public string TopicTitle { get; set; } = string.Empty;
        public string TargetKeyword { get; set; } = string.Empty;
    }

    public class AddSuggestedTopicsDto
    {
        [Required]
        public List<SaveBlogTopicDto> Topics { get; set; } = new();
    }

    public class BlogSettingsDto
    {
        public bool AutoGenerateEnabled { get; set; }
        public bool PublicVisible { get; set; }
        public int GenerationIntervalDays { get; set; }
        public int GenerationHourUtc { get; set; }
        public string GenerationModel { get; set; } = string.Empty;
        public List<BlogModelOptionDto> ModelOptions { get; set; } = new();
        public DateTime? LastRunAt { get; set; }
        public string? LastRunResult { get; set; }
        public int QueuedTopicsCount { get; set; }
    }

    public class BlogModelOptionDto
    {
        public string Id { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
    }

    public class UpdateBlogSettingsDto
    {
        public bool AutoGenerateEnabled { get; set; }
        public bool PublicVisible { get; set; }

        /// <summary>Must be one of AnthropicModelCatalog.BlogModels.</summary>
        [StringLength(100)]
        public string? GenerationModel { get; set; }
    }

    /// <summary>Server-side Markdown → sanitized-HTML preview for the admin editor
    /// (same pipeline as save, so the preview always matches the published output).</summary>
    public class BlogPreviewRequestDto
    {
        [Required]
        public string ContentMarkdown { get; set; } = string.Empty;
    }

    public class BlogPreviewResponseDto
    {
        public string ContentHtml { get; set; } = string.Empty;
    }
}
