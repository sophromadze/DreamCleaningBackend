using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;
using System.Security.Claims;
using DreamCleaningBackend.Attributes;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using DreamCleaningBackend.Services.Interfaces;

namespace DreamCleaningBackend.Controllers
{
    /// <summary>Blog administration: posts, topic queue, AI generation, settings.
    /// New topic controller following the 2026-06 admin split — same api/admin prefix.</summary>
    [Route("api/admin")]
    [ApiController]
    [Authorize(Roles = "Admin,SuperAdmin,Moderator")]
    public class AdminBlogController : AdminControllerBase
    {
        private const long MaxUploadSizeBytes = 10 * 1024 * 1024;
        private static readonly string[] AllowedImageExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".gif" };
        private const string BlogImagesSubfolder = "blog-images";

        private readonly ApplicationDbContext _context;
        private readonly BlogContentService _content;
        private readonly IBlogGenerationService _generation;
        private readonly IConfiguration _configuration;

        public AdminBlogController(
            ApplicationDbContext context,
            BlogContentService content,
            IBlogGenerationService generation,
            IConfiguration configuration)
        {
            _context = context;
            _content = content;
            _generation = generation;
            _configuration = configuration;
        }

        // ─────────────────────────────────────────────────────────
        //  Posts
        // ─────────────────────────────────────────────────────────

        [HttpGet("blog/posts")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<BlogPostAdminListItemDto>>> GetPosts([FromQuery] int? status = null)
        {
            var query = _context.BlogPosts.AsNoTracking();
            if (status.HasValue)
                query = query.Where(p => (int)p.Status == status.Value);

            // PendingReview drafts surface first so a new AI draft is impossible to miss.
            var posts = await query
                .OrderByDescending(p => p.Status == BlogPostStatus.PendingReview)
                .ThenByDescending(p => p.CreatedAt)
                .Select(p => new BlogPostAdminListItemDto
                {
                    Id = p.Id,
                    Title = p.Title,
                    Slug = p.Slug,
                    Status = (int)p.Status,
                    Category = p.Category,
                    IsAiGenerated = p.IsAiGenerated,
                    CreatedAt = p.CreatedAt,
                    PublishedAt = p.PublishedAt,
                    ViewCount = p.ViewCount
                })
                .ToListAsync();

            return Ok(posts);
        }

        /// <summary>Badge count for the admin nav — PendingReview drafts waiting for a human.</summary>
        [HttpGet("blog/pending-count")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<object>> GetPendingCount()
        {
            var count = await _context.BlogPosts.CountAsync(p => p.Status == BlogPostStatus.PendingReview);
            return Ok(new { count });
        }

        [HttpGet("blog/posts/{id}")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<BlogPostAdminDto>> GetPost(int id)
        {
            var post = await _context.BlogPosts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
            if (post == null) return NotFound(new { message = "Post not found." });
            return Ok(MapAdminDto(post));
        }

        [HttpPost("blog/posts")]
        [RequirePermission(Permission.Create)]
        public async Task<ActionResult<BlogPostAdminDto>> CreatePost([FromBody] SaveBlogPostDto dto)
        {
            var post = new BlogPost
            {
                Status = BlogPostStatus.Draft,
                IsAiGenerated = false,
                CreatedAt = DateTime.UtcNow
            };

            await ApplySaveDtoAsync(post, dto);
            _context.BlogPosts.Add(post);
            await _context.SaveChangesAsync();

            return Ok(MapAdminDto(post));
        }

        [HttpPut("blog/posts/{id}")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<BlogPostAdminDto>> UpdatePost(int id, [FromBody] SaveBlogPostDto dto)
        {
            var post = await _context.BlogPosts.FirstOrDefaultAsync(p => p.Id == id);
            if (post == null) return NotFound(new { message = "Post not found." });

            await ApplySaveDtoAsync(post, dto);
            await _context.SaveChangesAsync();

            if (post.Status == BlogPostStatus.Published)
                BlogCache.Invalidate();

            return Ok(MapAdminDto(post));
        }

        [HttpPost("blog/posts/{id}/publish")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<BlogPostAdminDto>> PublishPost(int id)
        {
            var post = await _context.BlogPosts.FirstOrDefaultAsync(p => p.Id == id);
            if (post == null) return NotFound(new { message = "Post not found." });

            if (string.IsNullOrWhiteSpace(post.ContentHtml))
                return BadRequest(new { message = "Post has no content." });

            post.Status = BlogPostStatus.Published;
            post.PublishedAt ??= DateTime.UtcNow;
            post.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            BlogCache.Invalidate();
            return Ok(MapAdminDto(post));
        }

        [HttpPost("blog/posts/{id}/unpublish")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<BlogPostAdminDto>> UnpublishPost(int id)
        {
            var post = await _context.BlogPosts.FirstOrDefaultAsync(p => p.Id == id);
            if (post == null) return NotFound(new { message = "Post not found." });

            post.Status = BlogPostStatus.Draft;
            post.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            BlogCache.Invalidate();
            return Ok(MapAdminDto(post));
        }

        // SuperAdmin ONLY — the method-level attribute AND-combines with the class-level
        // roles; the Delete permission (which Admin lacks) is enforced on top of it.
        [HttpDelete("blog/posts/{id}")]
        [Authorize(Roles = "SuperAdmin")]
        [RequirePermission(Permission.Delete)]
        public async Task<ActionResult> DeletePost(int id)
        {
            var post = await _context.BlogPosts.FirstOrDefaultAsync(p => p.Id == id);
            if (post == null) return NotFound(new { message = "Post not found." });

            DeleteFileIfExists(post.FeaturedImagePath);
            _context.BlogPosts.Remove(post);
            await _context.SaveChangesAsync();

            BlogCache.Invalidate();
            return NoContent();
        }

        /// <summary>Markdown → sanitized HTML, same pipeline as save. Used by the editor's
        /// live preview so what admins see is exactly what publishes.</summary>
        [HttpPost("blog/preview")]
        [RequirePermission(Permission.View)]
        public ActionResult<BlogPreviewResponseDto> Preview([FromBody] BlogPreviewRequestDto dto)
        {
            return Ok(new BlogPreviewResponseDto { ContentHtml = _content.RenderMarkdown(dto.ContentMarkdown) });
        }

        // ─────────────────────────────────────────────────────────
        //  Topic queue
        // ─────────────────────────────────────────────────────────

        [HttpGet("blog/topics")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<BlogTopicDto>>> GetTopics()
        {
            var topics = await _context.BlogTopics.AsNoTracking()
                .OrderBy(t => t.Status)
                .ThenBy(t => t.Priority)
                .ThenBy(t => t.Id)
                .Select(t => new BlogTopicDto
                {
                    Id = t.Id,
                    TopicTitle = t.TopicTitle,
                    TargetKeyword = t.TargetKeyword,
                    Notes = t.Notes,
                    Status = (int)t.Status,
                    Priority = t.Priority,
                    CreatedAt = t.CreatedAt,
                    GeneratedAt = t.GeneratedAt,
                    GeneratedBlogPostId = t.GeneratedBlogPostId
                })
                .ToListAsync();

            return Ok(topics);
        }

        [HttpPost("blog/topics")]
        [RequirePermission(Permission.Create)]
        public async Task<ActionResult<BlogTopicDto>> CreateTopic([FromBody] SaveBlogTopicDto dto)
        {
            var maxPriority = await _context.BlogTopics
                .Where(t => t.Status == BlogTopicStatus.Queued)
                .Select(t => (int?)t.Priority)
                .MaxAsync() ?? 0;

            var topic = new BlogTopic
            {
                TopicTitle = dto.TopicTitle.Trim(),
                TargetKeyword = dto.TargetKeyword?.Trim(),
                Notes = dto.Notes?.Trim(),
                Priority = maxPriority + 1
            };

            _context.BlogTopics.Add(topic);
            await _context.SaveChangesAsync();
            return Ok(MapTopicDto(topic));
        }

        /// <summary>Bulk-add checked AI suggestions from the "Suggest Topics" flow.</summary>
        [HttpPost("blog/topics/bulk")]
        [RequirePermission(Permission.Create)]
        public async Task<ActionResult<List<BlogTopicDto>>> AddTopics([FromBody] AddSuggestedTopicsDto dto)
        {
            var maxPriority = await _context.BlogTopics
                .Where(t => t.Status == BlogTopicStatus.Queued)
                .Select(t => (int?)t.Priority)
                .MaxAsync() ?? 0;

            var created = new List<BlogTopic>();
            foreach (var item in dto.Topics.Where(t => !string.IsNullOrWhiteSpace(t.TopicTitle)))
            {
                maxPriority++;
                created.Add(new BlogTopic
                {
                    TopicTitle = item.TopicTitle.Trim(),
                    TargetKeyword = item.TargetKeyword?.Trim(),
                    Notes = item.Notes?.Trim(),
                    Priority = maxPriority
                });
            }

            _context.BlogTopics.AddRange(created);
            await _context.SaveChangesAsync();
            return Ok(created.Select(MapTopicDto).ToList());
        }

        [HttpPut("blog/topics/{id}")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<BlogTopicDto>> UpdateTopic(int id, [FromBody] SaveBlogTopicDto dto)
        {
            var topic = await _context.BlogTopics.FirstOrDefaultAsync(t => t.Id == id);
            if (topic == null) return NotFound(new { message = "Topic not found." });

            topic.TopicTitle = dto.TopicTitle.Trim();
            topic.TargetKeyword = dto.TargetKeyword?.Trim();
            topic.Notes = dto.Notes?.Trim();
            await _context.SaveChangesAsync();

            return Ok(MapTopicDto(topic));
        }

        // SuperAdmin ONLY — see DeletePost.
        [HttpDelete("blog/topics/{id}")]
        [Authorize(Roles = "SuperAdmin")]
        [RequirePermission(Permission.Delete)]
        public async Task<ActionResult> DeleteTopic(int id)
        {
            var topic = await _context.BlogTopics.FirstOrDefaultAsync(t => t.Id == id);
            if (topic == null) return NotFound(new { message = "Topic not found." });

            _context.BlogTopics.Remove(topic);
            await _context.SaveChangesAsync();
            return NoContent();
        }

        [HttpPost("blog/topics/reorder")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult> ReorderTopics([FromBody] ReorderBlogTopicsDto dto)
        {
            var topics = await _context.BlogTopics
                .Where(t => dto.TopicIds.Contains(t.Id))
                .ToListAsync();

            for (var i = 0; i < dto.TopicIds.Count; i++)
            {
                var topic = topics.FirstOrDefault(t => t.Id == dto.TopicIds[i]);
                if (topic != null) topic.Priority = i + 1;
            }

            await _context.SaveChangesAsync();
            return Ok();
        }

        // ─────────────────────────────────────────────────────────
        //  AI generation
        // ─────────────────────────────────────────────────────────

        /// <summary>Manual trigger — same code path as the scheduled job. Returns the draft.</summary>
        [HttpPost("blog/generate")]
        [RequirePermission(Permission.Create)]
        public async Task<ActionResult<BlogPostAdminDto>> Generate([FromBody] GenerateBlogPostDto dto)
        {
            try
            {
                BlogPost post;
                if (dto.TopicId.HasValue)
                {
                    var topic = await _context.BlogTopics.FirstOrDefaultAsync(t => t.Id == dto.TopicId.Value);
                    if (topic == null) return NotFound(new { message = "Topic not found." });
                    post = await _generation.GenerateFromTopicAsync(topic);
                }
                else if (!string.IsNullOrWhiteSpace(dto.FreeformTopic))
                {
                    post = await _generation.GenerateAsync(dto.FreeformTopic!, dto.TargetKeyword, dto.Notes);
                }
                else
                {
                    return BadRequest(new { message = "Provide either topicId or freeformTopic." });
                }

                return Ok(MapAdminDto(post));
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        /// <summary>Asks Claude for 15–20 fresh topic ideas that don't duplicate existing
        /// posts/queued topics. Returned for the admin to pick from — nothing is saved here.
        /// Create (not View) on purpose: it costs a paid Anthropic call, so view-only
        /// Moderators must not be able to trigger it.</summary>
        [HttpPost("blog/topics/suggest")]
        [RequirePermission(Permission.Create)]
        public async Task<ActionResult<List<SuggestedTopicDto>>> SuggestTopics()
        {
            try
            {
                var suggestions = await _generation.SuggestTopicsAsync();
                return Ok(suggestions);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        // ─────────────────────────────────────────────────────────
        //  Settings
        // ─────────────────────────────────────────────────────────

        // Settings are SuperAdmin ONLY (both read and write) — the admin UI hides the
        // whole tab from other roles, and these attributes make that real server-side.
        [HttpGet("blog/settings")]
        [Authorize(Roles = "SuperAdmin")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<BlogSettingsDto>> GetSettings()
        {
            var settings = await GetOrCreateSettingsAsync();
            var queued = await _context.BlogTopics.CountAsync(t => t.Status == BlogTopicStatus.Queued);

            var fallbackModel = _configuration["Blog:GenerationModel"] ?? "claude-sonnet-5";
            var effectiveModel = AnthropicModelCatalog.IsValidBlogModel(settings.GenerationModel)
                ? settings.GenerationModel!
                : fallbackModel;

            return Ok(new BlogSettingsDto
            {
                AutoGenerateEnabled = settings.AutoGenerateEnabled,
                PublicVisible = settings.PublicVisible,
                GenerationIntervalDays = _configuration.GetValue<int>("Blog:GenerationIntervalDays", 7),
                GenerationHourUtc = _configuration.GetValue<int>("Blog:GenerationHourUtc", 9),
                GenerationModel = effectiveModel,
                ModelOptions = AnthropicModelCatalog.BlogModels
                    .Select(m => new BlogModelOptionDto { Id = m.Id, Label = m.Label })
                    .ToList(),
                LastRunAt = settings.LastRunAt,
                LastRunResult = settings.LastRunResult,
                QueuedTopicsCount = queued
            });
        }

        [HttpPut("blog/settings")]
        [Authorize(Roles = "SuperAdmin")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult> UpdateSettings([FromBody] UpdateBlogSettingsDto dto)
        {
            if (dto.GenerationModel != null && !AnthropicModelCatalog.IsValidBlogModel(dto.GenerationModel))
                return BadRequest(new { message = "Unknown model. Pick one from the list." });

            var settings = await GetOrCreateSettingsAsync();
            var visibilityChanged = settings.PublicVisible != dto.PublicVisible;

            settings.AutoGenerateEnabled = dto.AutoGenerateEnabled;
            settings.PublicVisible = dto.PublicVisible;
            if (dto.GenerationModel != null)
                settings.GenerationModel = dto.GenerationModel;
            settings.UpdatedAt = DateTime.UtcNow;
            settings.UpdatedByEmail = User.FindFirst(ClaimTypes.Email)?.Value;
            await _context.SaveChangesAsync();

            // Public list/detail/status/sitemap caches all key on BlogCache.Version —
            // flipping visibility must take effect immediately.
            if (visibilityChanged)
                BlogCache.Invalidate();

            return Ok();
        }

        // ─────────────────────────────────────────────────────────
        //  Featured image upload
        // ─────────────────────────────────────────────────────────

        [HttpPost("blog/upload-image")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<object>> UploadImage(IFormFile file)
        {
            try
            {
                if (file == null || file.Length == 0)
                    return BadRequest(new { message = "No file uploaded." });

                if (file.Length > MaxUploadSizeBytes)
                    return BadRequest(new { message = "File size must be less than 10MB." });

                var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
                if (!AllowedImageExtensions.Contains(extension))
                    return BadRequest(new { message = "Invalid file type. Only image files are allowed." });

                var basePath = _configuration["FileUpload:Path"];
                if (string.IsNullOrWhiteSpace(basePath))
                    return StatusCode(500, new { message = "FileUpload:Path is not configured." });

                var uploadDir = Path.Combine(basePath, BlogImagesSubfolder);
                Directory.CreateDirectory(uploadDir);

                var fileName = $"blog-{DateTime.UtcNow:yyyyMMddHHmmssfff}.webp";
                var fullPath = Path.Combine(uploadDir, fileName);

                using (var inputStream = file.OpenReadStream())
                using (var image = await Image.LoadAsync(inputStream))
                {
                    // 1600px wide is plenty for article heroes + og:image.
                    if (image.Width > 1600 || image.Height > 1600)
                    {
                        image.Mutate(x => x.Resize(new ResizeOptions
                        {
                            Size = new Size(1600, 1600),
                            Mode = ResizeMode.Max
                        }));
                    }

                    await image.SaveAsync(fullPath, new WebpEncoder { Quality = 82 });
                }

                var publicUrl = $"/{BlogImagesSubfolder}/{fileName}";
                return Ok(new { url = publicUrl });
            }
            catch (Exception)
            {
                return StatusCode(500, new { message = "Failed to process the image." });
            }
        }

        // ─────────────────────────────────────────────────────────
        //  Helpers
        // ─────────────────────────────────────────────────────────

        private async Task ApplySaveDtoAsync(BlogPost post, SaveBlogPostDto dto)
        {
            post.Title = dto.Title.Trim();
            post.Excerpt = dto.Excerpt?.Trim() ?? string.Empty;
            post.ContentMarkdown = dto.ContentMarkdown;
            post.ContentHtml = _content.RenderMarkdown(dto.ContentMarkdown);
            post.MetaTitle = dto.MetaTitle?.Trim();
            post.MetaDescription = dto.MetaDescription?.Trim();
            post.FeaturedImagePath = dto.FeaturedImagePath?.Trim();
            post.FeaturedImageAlt = dto.FeaturedImageAlt?.Trim();
            post.Category = string.IsNullOrWhiteSpace(dto.Category) ? "Guides" : dto.Category.Trim();
            post.Tags = dto.Tags?.Trim();
            post.AuthorName = string.IsNullOrWhiteSpace(dto.AuthorName) ? "Dream Cleaning Team" : dto.AuthorName.Trim();
            post.UpdatedAt = DateTime.UtcNow;

            // Slug is locked once published — Google already indexed the URL.
            if (post.Status != BlogPostStatus.Published)
            {
                var desired = string.IsNullOrWhiteSpace(dto.Slug)
                    ? BlogContentService.Slugify(dto.Title)
                    : BlogContentService.Slugify(dto.Slug);
                post.Slug = await BlogContentService.EnsureUniqueSlugAsync(_context, desired, post.Id == 0 ? null : post.Id);
            }
        }

        private static BlogPostAdminDto MapAdminDto(BlogPost p) => new()
        {
            Id = p.Id,
            Title = p.Title,
            Slug = p.Slug,
            Excerpt = p.Excerpt,
            ContentMarkdown = p.ContentMarkdown,
            ContentHtml = p.ContentHtml,
            MetaTitle = p.MetaTitle,
            MetaDescription = p.MetaDescription,
            FeaturedImagePath = p.FeaturedImagePath,
            FeaturedImageAlt = p.FeaturedImageAlt,
            Status = (int)p.Status,
            Category = p.Category,
            Tags = p.Tags,
            AuthorName = p.AuthorName,
            IsAiGenerated = p.IsAiGenerated,
            CreatedAt = p.CreatedAt,
            PublishedAt = p.PublishedAt,
            UpdatedAt = p.UpdatedAt,
            ViewCount = p.ViewCount
        };

        private static BlogTopicDto MapTopicDto(BlogTopic t) => new()
        {
            Id = t.Id,
            TopicTitle = t.TopicTitle,
            TargetKeyword = t.TargetKeyword,
            Notes = t.Notes,
            Status = (int)t.Status,
            Priority = t.Priority,
            CreatedAt = t.CreatedAt,
            GeneratedAt = t.GeneratedAt,
            GeneratedBlogPostId = t.GeneratedBlogPostId
        };

        private async Task<BlogSettings> GetOrCreateSettingsAsync()
        {
            var settings = await _context.BlogSettings.OrderBy(s => s.Id).FirstOrDefaultAsync();
            if (settings == null)
            {
                settings = new BlogSettings();
                _context.BlogSettings.Add(settings);
                await _context.SaveChangesAsync();
            }
            return settings;
        }

        private void DeleteFileIfExists(string? publicUrl)
        {
            if (string.IsNullOrWhiteSpace(publicUrl)) return;

            var basePath = _configuration["FileUpload:Path"];
            if (string.IsNullOrWhiteSpace(basePath)) return;

            var fullPath = Path.Combine(basePath, publicUrl.TrimStart('/'));
            try
            {
                if (System.IO.File.Exists(fullPath)) System.IO.File.Delete(fullPath);
            }
            catch
            {
                // DB row wins; ignore disk failures.
            }
        }
    }
}
