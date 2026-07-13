using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Controllers
{
    /// <summary>
    /// Dynamic blog sitemap. Reached in production via the SSR Express proxy in server.ts
    /// (public /sitemap-blog.xml → http://localhost:5000/sitemap-blog.xml), referenced by
    /// the sitemap index at public/sitemap.xml. Route is deliberately outside /api.
    /// </summary>
    [ApiController]
    public class SitemapController : ControllerBase
    {
        private const string BaseUrl = "https://dreamcleaningnyc.com";
        private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);

        private readonly ApplicationDbContext _context;
        private readonly IMemoryCache _cache;

        public SitemapController(ApplicationDbContext context, IMemoryCache cache)
        {
            _context = context;
            _cache = cache;
        }

        [HttpGet("/sitemap-blog.xml")]
        public async Task<IActionResult> GetBlogSitemap()
        {
            var cacheKey = $"blog:sitemap:v{BlogCache.Version}";
            if (!_cache.TryGetValue(cacheKey, out string? xml) || xml == null)
            {
                // Blog hidden → valid but empty sitemap; nothing advertised to crawlers.
                if (!await BlogCache.IsPublicVisibleAsync(_context, _cache))
                {
                    xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n</urlset>\n";
                    _cache.Set(cacheKey, xml, CacheDuration);
                    return Content(xml, "application/xml", Encoding.UTF8);
                }

                var posts = await _context.BlogPosts.AsNoTracking()
                    .Where(p => p.Status == BlogPostStatus.Published)
                    .OrderByDescending(p => p.PublishedAt)
                    .Select(p => new { p.Slug, p.UpdatedAt })
                    .ToListAsync();

                var sb = new StringBuilder();
                sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
                sb.AppendLine("<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">");

                // The blog index page itself.
                sb.AppendLine("  <url>");
                sb.AppendLine($"    <loc>{BaseUrl}/blog</loc>");
                if (posts.Count > 0)
                    sb.AppendLine($"    <lastmod>{posts.Max(p => p.UpdatedAt):yyyy-MM-ddTHH:mm:ssZ}</lastmod>");
                sb.AppendLine("    <changefreq>weekly</changefreq>");
                sb.AppendLine("  </url>");

                foreach (var post in posts)
                {
                    sb.AppendLine("  <url>");
                    sb.AppendLine($"    <loc>{BaseUrl}/blog/{Uri.EscapeDataString(post.Slug)}</loc>");
                    sb.AppendLine($"    <lastmod>{post.UpdatedAt:yyyy-MM-ddTHH:mm:ssZ}</lastmod>");
                    sb.AppendLine("  </url>");
                }

                sb.AppendLine("</urlset>");
                xml = sb.ToString();
                _cache.Set(cacheKey, xml, CacheDuration);
            }

            return Content(xml, "application/xml", Encoding.UTF8);
        }
    }
}
