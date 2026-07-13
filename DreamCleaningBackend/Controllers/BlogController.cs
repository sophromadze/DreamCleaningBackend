using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Controllers
{
    /// <summary>
    /// Cache-key versioning for the public blog endpoints. Admin writes bump the version,
    /// which orphans every cached entry at once (entries expire out of memory on their own
    /// TTL). Cheaper and safer than tracking individual keys.
    /// </summary>
    public static class BlogCache
    {
        private static int _version;
        public static int Version => Volatile.Read(ref _version);
        public static void Invalidate() => Interlocked.Increment(ref _version);

        /// <summary>Cached read of BlogSettings.PublicVisible (the owner's master switch
        /// for public visibility). Version-keyed, so flipping the toggle in the admin
        /// takes effect immediately. Shared by BlogController and SitemapController.</summary>
        public static async Task<bool> IsPublicVisibleAsync(ApplicationDbContext context, IMemoryCache cache)
        {
            var cacheKey = $"blog:visible:v{Version}";
            if (cache.TryGetValue(cacheKey, out bool visible))
                return visible;

            visible = await context.BlogSettings
                .OrderBy(s => s.Id)
                .Select(s => (bool?)s.PublicVisible)
                .FirstOrDefaultAsync() ?? false;

            cache.Set(cacheKey, visible, TimeSpan.FromMinutes(10));
            return visible;
        }
    }

    [Route("api/blog")]
    [ApiController]
    public class BlogController : ControllerBase
    {
        private const int MaxPageSize = 24;
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

        private readonly ApplicationDbContext _context;
        private readonly IMemoryCache _cache;
        private readonly IServiceScopeFactory _scopeFactory;

        public BlogController(ApplicationDbContext context, IMemoryCache cache, IServiceScopeFactory scopeFactory)
        {
            _context = context;
            _cache = cache;
            _scopeFactory = scopeFactory;
        }

        /// <summary>Public visibility probe — one cheap cached call shared by the header
        /// and the blog pages (frontend BlogStatusService, shareReplay + SSR transfer cache).</summary>
        [HttpGet("status")]
        public async Task<ActionResult<BlogStatusDto>> GetStatus()
        {
            var visible = await BlogCache.IsPublicVisibleAsync(_context, _cache);
            return Ok(new BlogStatusDto { PublicVisible = visible });
        }

        [HttpGet("posts")]
        public async Task<ActionResult<BlogPostListResponseDto>> GetPosts(
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 9,
            [FromQuery] string? category = null)
        {
            // Master switch OFF → empty payload with the flag; the frontend renders
            // its "coming soon" page. Admin drafting/publishing is unaffected.
            if (!await BlogCache.IsPublicVisibleAsync(_context, _cache))
                return Ok(new BlogPostListResponseDto { PublicVisible = false, Page = 1, PageSize = pageSize });

            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
            category = string.IsNullOrWhiteSpace(category) ? null : category.Trim();

            var cacheKey = $"blog:list:v{BlogCache.Version}:{page}:{pageSize}:{category?.ToLowerInvariant()}";
            if (_cache.TryGetValue(cacheKey, out BlogPostListResponseDto? cached) && cached != null)
                return Ok(cached);

            var query = _context.BlogPosts.AsNoTracking()
                .Where(p => p.Status == BlogPostStatus.Published);

            if (category != null)
                query = query.Where(p => p.Category == category);

            var totalCount = await query.CountAsync();

            var posts = await query
                .OrderByDescending(p => p.PublishedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(p => new BlogPostListItemDto
                {
                    Title = p.Title,
                    Slug = p.Slug,
                    Excerpt = p.Excerpt,
                    FeaturedImagePath = p.FeaturedImagePath,
                    FeaturedImageAlt = p.FeaturedImageAlt,
                    Category = p.Category,
                    PublishedAt = p.PublishedAt
                })
                .ToListAsync();

            var categories = await _context.BlogPosts.AsNoTracking()
                .Where(p => p.Status == BlogPostStatus.Published)
                .Select(p => p.Category)
                .Distinct()
                .OrderBy(c => c)
                .ToListAsync();

            var response = new BlogPostListResponseDto
            {
                Posts = posts,
                TotalCount = totalCount,
                Page = page,
                PageSize = pageSize,
                Categories = categories
            };

            _cache.Set(cacheKey, response, CacheDuration);
            return Ok(response);
        }

        [HttpGet("posts/{slug}")]
        public async Task<ActionResult<BlogPostDetailDto>> GetPost(string slug)
        {
            if (string.IsNullOrWhiteSpace(slug) || slug.Length > 220)
                return NotFound();

            // Hidden blog → 404; the frontend consults /api/blog/status to decide
            // between "coming soon" (hidden) and a real not-found page (visible).
            if (!await BlogCache.IsPublicVisibleAsync(_context, _cache))
                return NotFound();

            var cacheKey = $"blog:post:v{BlogCache.Version}:{slug.ToLowerInvariant()}";
            if (!_cache.TryGetValue(cacheKey, out BlogPostDetailDto? dto) || dto == null)
            {
                var post = await _context.BlogPosts.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.Slug == slug && p.Status == BlogPostStatus.Published);

                if (post == null)
                    return NotFound();

                // Related: same category first, most recent fill-in — never the post itself.
                var related = await _context.BlogPosts.AsNoTracking()
                    .Where(p => p.Status == BlogPostStatus.Published && p.Id != post.Id)
                    .OrderByDescending(p => p.Category == post.Category)
                    .ThenByDescending(p => p.PublishedAt)
                    .Take(3)
                    .Select(p => new BlogPostListItemDto
                    {
                        Title = p.Title,
                        Slug = p.Slug,
                        Excerpt = p.Excerpt,
                        FeaturedImagePath = p.FeaturedImagePath,
                        FeaturedImageAlt = p.FeaturedImageAlt,
                        Category = p.Category,
                        PublishedAt = p.PublishedAt
                    })
                    .ToListAsync();

                dto = new BlogPostDetailDto
                {
                    Title = post.Title,
                    Slug = post.Slug,
                    Excerpt = post.Excerpt,
                    ContentHtml = post.ContentHtml,
                    MetaTitle = post.MetaTitle,
                    MetaDescription = post.MetaDescription,
                    FeaturedImagePath = post.FeaturedImagePath,
                    FeaturedImageAlt = post.FeaturedImageAlt,
                    Category = post.Category,
                    Tags = (post.Tags ?? "")
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .ToList(),
                    AuthorName = post.AuthorName,
                    PublishedAt = post.PublishedAt,
                    UpdatedAt = post.UpdatedAt,
                    RelatedPosts = related
                };

                _cache.Set(cacheKey, dto, CacheDuration);
            }

            // Fire-and-forget view count — atomic UPDATE, never blocks the response and
            // runs even on cache hits (the cached DTO skips the SELECT, not the count).
            var postSlug = dto.Slug;
            var scopeFactory = _scopeFactory;
            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    await db.BlogPosts
                        .Where(p => p.Slug == postSlug)
                        .ExecuteUpdateAsync(s => s.SetProperty(p => p.ViewCount, p => p.ViewCount + 1));
                }
                catch
                {
                    // View counts are best-effort; never surface failures.
                }
            });

            return Ok(dto);
        }
    }
}
