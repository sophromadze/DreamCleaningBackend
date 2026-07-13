using System.Text;
using Ganss.Xss;
using Markdig;
using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Data;

namespace DreamCleaningBackend.Services
{
    /// <summary>
    /// Stateless blog content pipeline: Markdown → sanitized HTML, and slug
    /// generation/uniqueness. Both AI-generated content and admin edits pass through
    /// RenderMarkdown on save — the public API only ever serves the sanitized output.
    /// </summary>
    public class BlogContentService
    {
        private readonly MarkdownPipeline _pipeline;
        private readonly HtmlSanitizer _sanitizer;

        public BlogContentService()
        {
            // No .UseAdvancedExtensions() on purpose: keep the surface to what articles
            // need (tables, lists, links, images) and let the sanitizer stay tight.
            _pipeline = new MarkdownPipelineBuilder()
                .UsePipeTables()
                .UseAutoLinks()
                .Build();

            _sanitizer = new HtmlSanitizer();
            _sanitizer.AllowedTags.Clear();
            foreach (var tag in new[]
            {
                "h1", "h2", "h3", "h4", "p", "br", "hr",
                "ul", "ol", "li", "blockquote",
                "strong", "em", "b", "i", "u", "s", "code", "pre",
                "a", "img",
                "table", "thead", "tbody", "tr", "th", "td"
            })
            {
                _sanitizer.AllowedTags.Add(tag);
            }

            _sanitizer.AllowedAttributes.Clear();
            foreach (var attr in new[] { "href", "src", "alt", "title", "target", "rel" })
            {
                _sanitizer.AllowedAttributes.Add(attr);
            }

            _sanitizer.AllowedSchemes.Clear();
            _sanitizer.AllowedSchemes.Add("http");
            _sanitizer.AllowedSchemes.Add("https");
            _sanitizer.AllowedSchemes.Add("mailto");
            _sanitizer.AllowedSchemes.Add("tel");

            // External links get rel hardening after sanitization (see RenderMarkdown).
            _sanitizer.PostProcessNode += (_, e) =>
            {
                if (e.Node is AngleSharp.Html.Dom.IHtmlAnchorElement anchor)
                {
                    var href = anchor.GetAttribute("href") ?? "";
                    var isExternal = href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                        && !href.Contains("dreamcleaningnyc.com", StringComparison.OrdinalIgnoreCase);
                    if (isExternal)
                    {
                        anchor.SetAttribute("rel", "noopener noreferrer");
                        anchor.SetAttribute("target", "_blank");
                    }
                }
            };
        }

        /// <summary>Markdown → sanitized HTML. Safe against injected scripts/styles/handlers
        /// regardless of whether the Markdown came from Claude or an admin.</summary>
        public string RenderMarkdown(string markdown)
        {
            if (string.IsNullOrWhiteSpace(markdown)) return string.Empty;
            var html = Markdown.ToHtml(markdown, _pipeline);
            return _sanitizer.Sanitize(html);
        }

        /// <summary>Lowercase-ASCII-hyphen slug from a title. Diacritics folded, everything
        /// else non-alphanumeric collapsed to single hyphens.</summary>
        public static string Slugify(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return string.Empty;

            var normalized = title.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(normalized.Length);
            foreach (var c in normalized)
            {
                var category = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
                if (category == System.Globalization.UnicodeCategory.NonSpacingMark) continue;

                if (char.IsAsciiLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
                else sb.Append('-');
            }

            var slug = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "-{2,}", "-").Trim('-');
            return slug.Length > 200 ? slug[..200].TrimEnd('-') : slug;
        }

        /// <summary>Returns the slug, or slug-2 / slug-3 / … if taken. excludePostId lets
        /// an update keep its own slug.</summary>
        public static async Task<string> EnsureUniqueSlugAsync(ApplicationDbContext context, string desiredSlug, int? excludePostId = null)
        {
            var baseSlug = string.IsNullOrWhiteSpace(desiredSlug) ? "post" : desiredSlug;
            var slug = baseSlug;
            var suffix = 2;

            while (await context.BlogPosts.AnyAsync(p => p.Slug == slug && (excludePostId == null || p.Id != excludePostId)))
            {
                slug = $"{baseSlug}-{suffix}";
                suffix++;
            }

            return slug;
        }
    }
}
