using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;

namespace DreamCleaningBackend.Services
{
    /// <summary>
    /// Turns a topic into a PendingReview BlogPost draft via the Anthropic Messages API.
    /// Reuses the IPv4-forcing AnthropicMessagesClient (VPS quirk) with a per-call model
    /// override — the chat agent keeps its own model, blog uses Blog:GenerationModel.
    /// Structured outputs (output_config.format json_schema) guarantee parseable JSON;
    /// the fence-stripping fallback stays as a belt-and-braces measure.
    /// </summary>
    public class BlogGenerationService : IBlogGenerationService
    {
        private readonly ApplicationDbContext _context;
        private readonly AnthropicMessagesClient _anthropic;
        private readonly BlogContentService _content;
        private readonly IConfiguration _configuration;
        private readonly ILogger<BlogGenerationService> _logger;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public BlogGenerationService(
            ApplicationDbContext context,
            AnthropicMessagesClient anthropic,
            BlogContentService content,
            IConfiguration configuration,
            ILogger<BlogGenerationService> logger)
        {
            _context = context;
            _anthropic = anthropic;
            _content = content;
            _configuration = configuration;
            _logger = logger;
        }

        // claude-sonnet-5 runs adaptive thinking by default and thinking tokens count
        // against max_tokens — 8000 leaves room for a 1,400-word article plus thinking.
        private int MaxTokens => _configuration.GetValue<int>("Blog:GenerationMaxTokens", 8000);

        /// <summary>Admin-selected model from the BlogSettings row, resolved at generation
        /// time (no redeploy to change it); appsettings is only the fallback/seed default.</summary>
        private async Task<string> ResolveModelAsync(CancellationToken cancellationToken)
        {
            var dbModel = await _context.BlogSettings
                .OrderBy(s => s.Id)
                .Select(s => s.GenerationModel)
                .FirstOrDefaultAsync(cancellationToken);

            if (AnthropicModelCatalog.IsValidBlogModel(dbModel))
                return dbModel!;

            return _configuration["Blog:GenerationModel"] ?? "claude-sonnet-5";
        }

        public async Task<BlogPost> GenerateFromTopicAsync(BlogTopic topic, CancellationToken cancellationToken = default)
        {
            // Idempotency: never generate twice for the same topic while an AI draft
            // from it is still awaiting review.
            if (topic.GeneratedBlogPostId != null)
            {
                var existing = await _context.BlogPosts.FirstOrDefaultAsync(
                    p => p.Id == topic.GeneratedBlogPostId && p.Status == BlogPostStatus.PendingReview,
                    cancellationToken);
                if (existing != null)
                    throw new InvalidOperationException(
                        $"A draft for this topic is already pending review (\"{existing.Title}\"). Review or delete it first.");
            }

            var post = await GenerateAsync(topic.TopicTitle, topic.TargetKeyword, topic.Notes, cancellationToken);

            topic.Status = BlogTopicStatus.Generated;
            topic.GeneratedAt = DateTime.UtcNow;
            topic.GeneratedBlogPostId = post.Id;
            await _context.SaveChangesAsync(cancellationToken);

            return post;
        }

        public async Task<BlogPost> GenerateAsync(string topicTitle, string? targetKeyword, string? notes, CancellationToken cancellationToken = default)
        {
            if (!_anthropic.IsConfigured)
                throw new InvalidOperationException("Anthropic API key is not configured.");

            var systemPrompt = BlogGenerationPrompts.ArticleSystemPrompt
                .Replace("{INTERNAL_LINKS}", BlogGenerationPrompts.InternalLinksAllowlist);

            var userPrompt = BlogGenerationPrompts.ArticleUserPromptTemplate
                .Replace("{TOPIC}", topicTitle.Trim())
                .Replace("{KEYWORD}", string.IsNullOrWhiteSpace(targetKeyword) ? "(choose the most natural search phrase for this topic)" : targetKeyword.Trim())
                .Replace("{NOTES}", string.IsNullOrWhiteSpace(notes) ? "" : $"Extra context / angle: {notes.Trim()}");

            var model = await ResolveModelAsync(cancellationToken);
            var response = await _anthropic.CreateMessageAsync(
                systemPrompt,
                new List<AnthropicMessage> { AnthropicMessage.User(AnthropicContentBlock.OfText(userPrompt)) },
                tools: null,
                maxTokensOverride: MaxTokens,
                cancellationToken: cancellationToken,
                modelOverride: model,
                outputConfig: AnthropicOutputConfig.JsonSchema(BlogGenerationPrompts.ArticleSchema));

            var text = ExtractText(response);
            var article = ParseJson<GeneratedArticle>(text);

            if (string.IsNullOrWhiteSpace(article.Title) || string.IsNullOrWhiteSpace(article.ContentMarkdown))
                throw new InvalidOperationException("Generation returned an incomplete article. Try again.");

            var desiredSlug = BlogContentService.Slugify(
                string.IsNullOrWhiteSpace(article.SlugSuggestion) ? article.Title : article.SlugSuggestion);

            var post = new BlogPost
            {
                Title = Truncate(article.Title.Trim(), 200)!,
                Slug = await BlogContentService.EnsureUniqueSlugAsync(_context, desiredSlug),
                Excerpt = Truncate(article.Excerpt?.Trim(), 300) ?? "",
                ContentMarkdown = article.ContentMarkdown,
                ContentHtml = _content.RenderMarkdown(article.ContentMarkdown),
                MetaTitle = Truncate(article.MetaTitle?.Trim(), 70),
                MetaDescription = Truncate(article.MetaDescription?.Trim(), 200),
                Category = string.IsNullOrWhiteSpace(article.Category) ? "Guides" : Truncate(article.Category.Trim(), 50)!,
                Tags = article.Tags is { Count: > 0 } ? Truncate(string.Join(", ", article.Tags), 500) : null,
                Status = BlogPostStatus.PendingReview,
                IsAiGenerated = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _context.BlogPosts.Add(post);
            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Blog draft #{Id} generated for topic \"{Topic}\" ({Model})", post.Id, topicTitle, model);
            return post;
        }

        public async Task<List<SuggestedTopicDto>> SuggestTopicsAsync(CancellationToken cancellationToken = default)
        {
            if (!_anthropic.IsConfigured)
                throw new InvalidOperationException("Anthropic API key is not configured.");

            var existingTitles = await _context.BlogPosts.AsNoTracking()
                .Select(p => p.Title)
                .ToListAsync(cancellationToken);
            var existingTopics = await _context.BlogTopics.AsNoTracking()
                .Where(t => t.Status != BlogTopicStatus.Skipped)
                .Select(t => t.TopicTitle)
                .ToListAsync(cancellationToken);

            var existing = existingTitles.Concat(existingTopics).Distinct().ToList();
            var existingBlock = existing.Count == 0 ? "(none yet)" : string.Join("\n", existing.Select(t => $"- {t}"));

            var userPrompt = BlogGenerationPrompts.SuggestTopicsUserPromptTemplate
                .Replace("{EXISTING}", existingBlock);

            var response = await _anthropic.CreateMessageAsync(
                BlogGenerationPrompts.SuggestTopicsSystemPrompt,
                new List<AnthropicMessage> { AnthropicMessage.User(AnthropicContentBlock.OfText(userPrompt)) },
                tools: null,
                maxTokensOverride: 4000,
                cancellationToken: cancellationToken,
                modelOverride: await ResolveModelAsync(cancellationToken),
                outputConfig: AnthropicOutputConfig.JsonSchema(BlogGenerationPrompts.TopicSuggestionsSchema));

            var text = ExtractText(response);
            var parsed = ParseJson<TopicSuggestionsPayload>(text);

            return (parsed.Topics ?? new List<SuggestedTopicDto>())
                .Where(t => !string.IsNullOrWhiteSpace(t.TopicTitle))
                .Take(20)
                .ToList();
        }

        // ─────────────────────────────────────────────────────────
        //  Helpers
        // ─────────────────────────────────────────────────────────

        private static string ExtractText(AnthropicResponse response)
        {
            var text = string.Concat(response.Content
                .Where(b => b.Type == "text" && !string.IsNullOrEmpty(b.Text))
                .Select(b => b.Text));

            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException("Generation returned no content. Try again.");

            return text;
        }

        /// <summary>Structured outputs should make this trivial, but strip markdown fences
        /// defensively in case the model is ever swapped for one without schema support.</summary>
        private static T ParseJson<T>(string text)
        {
            var trimmed = text.Trim();
            if (trimmed.StartsWith("```"))
            {
                var firstNewline = trimmed.IndexOf('\n');
                if (firstNewline >= 0) trimmed = trimmed[(firstNewline + 1)..];
                var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
                if (lastFence >= 0) trimmed = trimmed[..lastFence];
                trimmed = trimmed.Trim();
            }

            try
            {
                var parsed = JsonSerializer.Deserialize<T>(trimmed, JsonOptions);
                if (parsed == null) throw new JsonException("null payload");
                return parsed;
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"Generation returned unparseable JSON: {ex.Message}");
            }
        }

        private static string? Truncate(string? value, int max) =>
            value == null ? null : (value.Length <= max ? value : value[..max]);

        private class GeneratedArticle
        {
            public string Title { get; set; } = "";
            public string? SlugSuggestion { get; set; }
            public string? MetaTitle { get; set; }
            public string? MetaDescription { get; set; }
            public string? Excerpt { get; set; }
            public string? Category { get; set; }
            public List<string>? Tags { get; set; }
            public string ContentMarkdown { get; set; } = "";
        }

        private class TopicSuggestionsPayload
        {
            public List<SuggestedTopicDto>? Topics { get; set; }
        }
    }
}
