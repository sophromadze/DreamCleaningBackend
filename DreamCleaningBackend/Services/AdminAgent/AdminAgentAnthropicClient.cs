using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DreamCleaningBackend.Services.AdminAgent
{
    /// <summary>
    /// Settings of the SuperAdmin analytics agent, read from the <c>AdminAgent</c> section on every
    /// use (so a config change needs no restart). Every key is optional.
    /// </summary>
    public sealed class AdminAgentSettings
    {
        public const string Section = "AdminAgent";
        public const string DefaultModel = "claude-sonnet-5-5";
        public const int DefaultMaxTokens = 16000;
        public const int DefaultMaxToolIterations = 12;
        public const string DefaultEffort = "medium";

        public string? ApiKey { get; init; }
        public string Model { get; init; } = DefaultModel;
        public int MaxTokens { get; init; } = DefaultMaxTokens;
        public int MaxToolIterations { get; init; } = DefaultMaxToolIterations;

        /// <summary>output_config.effort: low / medium / high / xhigh / max.</summary>
        public string Effort { get; init; } = DefaultEffort;

        /// <summary>USD per million tokens. Null = no cost estimate is shown.</summary>
        public decimal? PriceInputPerMTok { get; init; }
        public decimal? PriceOutputPerMTok { get; init; }
        public decimal? PriceCacheWritePerMTok { get; init; }
        public decimal? PriceCacheReadPerMTok { get; init; }

        public static AdminAgentSettings From(IConfiguration configuration)
        {
            var s = configuration.GetSection(Section);
            var apiKey = s["ApiKey"];
            if (string.IsNullOrWhiteSpace(apiKey))
                apiKey = configuration["Anthropic:ApiKey"];
            var model = s["Model"];
            var effort = s["Effort"]?.Trim().ToLowerInvariant();
            return new AdminAgentSettings
            {
                ApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim(),
                Model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim(),
                MaxTokens = Positive(s, "MaxTokens", DefaultMaxTokens),
                MaxToolIterations = Positive(s, "MaxToolIterations", DefaultMaxToolIterations),
                Effort = effort is "low" or "medium" or "high" or "xhigh" or "max" ? effort : DefaultEffort,
                PriceInputPerMTok = Price(s, "PriceInputPerMTok"),
                PriceOutputPerMTok = Price(s, "PriceOutputPerMTok"),
                PriceCacheWritePerMTok = Price(s, "PriceCacheWritePerMTok"),
                PriceCacheReadPerMTok = Price(s, "PriceCacheReadPerMTok")
            };
        }

        /// <summary>
        /// Estimated USD for a turn, or null unless the input and output prices are both set. A
        /// missing cache price falls back to the input price — over-estimating rather than
        /// silently pricing cached tokens at zero.
        /// </summary>
        public decimal? EstimateCost(long input, long output, long cacheWrite, long cacheRead)
        {
            if (PriceInputPerMTok is not { } inPrice || PriceOutputPerMTok is not { } outPrice)
                return null;
            var write = PriceCacheWritePerMTok ?? inPrice;
            var read = PriceCacheReadPerMTok ?? inPrice;
            return decimal.Round((input * inPrice + output * outPrice + cacheWrite * write + cacheRead * read) / 1_000_000m, 6);
        }

        private static int Positive(IConfigurationSection s, string key, int fallback) =>
            int.TryParse(s[key], out var v) && v > 0 ? v : fallback;

        private static decimal? Price(IConfigurationSection s, string key) =>
            decimal.TryParse(s[key], System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : null;
    }

    public sealed class AdminAgentApiException : Exception
    {
        public int? StatusCode { get; }
        public AdminAgentApiException(string message, int? statusCode = null) : base(message) => StatusCode = statusCode;
    }

    /// <summary>
    /// Raw Messages API transport for the analytics agent. Raw JSON on purpose: the agent has to
    /// send every content block back EXACTLY as it came (thinking blocks carry signatures and are
    /// only valid unchanged), and a typed round-trip would drop fields it does not know about.
    /// Shares the IPv4-only HttpClient setup with <see cref="AnthropicMessagesClient"/>; one
    /// HttpClient per distinct API key, so changing AdminAgent:ApiKey needs no restart.
    /// </summary>
    public sealed class AdminAgentAnthropicClient : IDisposable
    {
        // Long enough for a non-streaming answer at 16k output tokens; the turn runs in the
        // background, so nobody is holding an HTTP request open meanwhile.
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);

        private readonly ILogger<AdminAgentAnthropicClient> _logger;
        private readonly Func<string, HttpClient> _httpFactory;
        private readonly object _gate = new();
        private HttpClient? _http;
        private string? _httpKey;

        public AdminAgentAnthropicClient(ILogger<AdminAgentAnthropicClient> logger)
            : this(logger, apiKey => AnthropicMessagesClient.CreateHttpClient(apiKey, RequestTimeout))
        {
        }

        /// <summary>For tests: supply the HttpClient (e.g. over a scripted handler). DI never
        /// resolves this overload — nothing registers the factory.</summary>
        public AdminAgentAnthropicClient(ILogger<AdminAgentAnthropicClient> logger, Func<string, HttpClient> httpFactory)
        {
            _logger = logger;
            _httpFactory = httpFactory;
        }

        public async Task<JsonDocument> SendAsync(string apiKey, JsonObject body, IEnumerable<string> betas, CancellationToken ct)
        {
            var http = ClientFor(apiKey);
            using var request = new HttpRequestMessage(HttpMethod.Post, AnthropicMessagesClient.MessagesEndpoint)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            };
            var betaList = betas.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
            if (betaList.Count > 0)
                request.Headers.Add("anthropic-beta", string.Join(",", betaList));

            using var response = await http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Analytics agent: Anthropic API error {Status}: {Body}",
                    (int)response.StatusCode, text.Length > 2000 ? text[..2000] : text);
                throw new AdminAgentApiException($"The AI service returned {(int)response.StatusCode}.", (int)response.StatusCode);
            }
            return JsonDocument.Parse(text);
        }

        private HttpClient ClientFor(string apiKey)
        {
            lock (_gate)
            {
                if (_http == null || _httpKey != apiKey)
                {
                    // The previous client is NOT disposed: a turn may still be awaiting it. A key
                    // change is a once-in-a-while event, so the abandoned client is negligible.
                    _http = _httpFactory(apiKey);
                    _httpKey = apiKey;
                }
                return _http;
            }
        }

        public void Dispose() => _http?.Dispose();
    }
}
