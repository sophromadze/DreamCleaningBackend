namespace DreamCleaningBackend.Services
{
    /// <summary>
    /// Single source of truth for Anthropic model IDs the admin UI may select.
    /// The blog Settings dropdown is populated from this list and the update endpoint
    /// validates against it. (The chat agent's Anthropic:Model config is a plain string
    /// with no validation today — if it ever gets an admin UI, reuse this catalog.)
    /// </summary>
    public static class AnthropicModelCatalog
    {
        public record ModelOption(string Id, string Label);

        /// <summary>Models allowed for blog generation. All three support structured
        /// outputs (output_config json_schema), which generation relies on.</summary>
        public static readonly IReadOnlyList<ModelOption> BlogModels = new List<ModelOption>
        {
            new("claude-sonnet-5", "Sonnet 5 — recommended for article quality"),
            new("claude-opus-4-8", "Opus 4.8 — highest quality, ~2x cost"),
            new("claude-haiku-4-5-20251001", "Haiku 4.5 — fastest and cheapest")
        };

        public static bool IsValidBlogModel(string? id) =>
            !string.IsNullOrWhiteSpace(id) && BlogModels.Any(m => m.Id == id);
    }
}
