using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DreamCleaningBackend.Services
{
    /// <summary>
    /// Thin client for the Anthropic Messages API (POST /v1/messages) used by the
    /// public chat agent. Raw HttpClient + System.Text.Json on purpose (no SDK):
    /// the VPS has IPv6 disabled, so outbound HTTP must force IPv4 via a
    /// SocketsHttpHandler ConnectCallback — same workaround as TelegramBotService.
    /// Configuration: Anthropic:ApiKey (secret), Anthropic:Model, Anthropic:MaxTokens.
    /// </summary>
    public class AnthropicMessagesClient
    {
        private const string MessagesUrl = "https://api.anthropic.com/v1/messages";
        private const string ApiVersion = "2023-06-01";

        private readonly HttpClient? _http;
        private readonly ILogger<AnthropicMessagesClient> _logger;
        private readonly string _model;
        private readonly int _maxTokens;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public bool IsConfigured { get; }
        public string Model => _model;

        public AnthropicMessagesClient(IConfiguration config, ILogger<AnthropicMessagesClient> logger)
        {
            _logger = logger;
            _model = config["Anthropic:Model"] ?? "claude-haiku-4-5-20251001";
            _maxTokens = config.GetValue<int>("Anthropic:MaxTokens", 1024);

            var apiKey = config["Anthropic:ApiKey"];
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _logger.LogWarning("AnthropicMessagesClient: Anthropic:ApiKey not configured — AI chat agent disabled.");
                IsConfigured = false;
                return;
            }

            // Force IPv4 — IPv6 is disabled on the production VPS (see CLAUDE.md quirk #2).
            var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (context, cancellationToken) =>
                {
                    var socket = new System.Net.Sockets.Socket(
                        System.Net.Sockets.AddressFamily.InterNetwork,
                        System.Net.Sockets.SocketType.Stream,
                        System.Net.Sockets.ProtocolType.Tcp);
                    await socket.ConnectAsync(context.DnsEndPoint, cancellationToken);
                    return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                }
            };

            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
            _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
            _http.DefaultRequestHeaders.Add("anthropic-version", ApiVersion);
            IsConfigured = true;
        }

        /// <summary>
        /// One Messages API call. The caller (ChatAgentService) owns the tool-use loop.
        /// Throws on transport/API errors — callers catch and degrade gracefully.
        /// </summary>
        public async Task<AnthropicResponse> CreateMessageAsync(
            string systemPrompt,
            List<AnthropicMessage> messages,
            List<AnthropicTool>? tools,
            int? maxTokensOverride = null,
            CancellationToken cancellationToken = default)
        {
            if (!IsConfigured || _http == null)
                throw new InvalidOperationException("Anthropic API key is not configured");

            var request = new AnthropicRequest
            {
                Model = _model,
                // Admin-mode QA answers pass a higher cap so multi-page discrepancy
                // reports aren't truncated; customer replies keep the default.
                MaxTokens = maxTokensOverride is > 0 ? maxTokensOverride.Value : _maxTokens,
                System = systemPrompt,
                Messages = messages,
                Tools = tools
            };

            var json = JsonSerializer.Serialize(request, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(MessagesUrl, content, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Anthropic API error {Status}: {Body}", (int)response.StatusCode, Truncate(body, 2000));
                throw new HttpRequestException($"Anthropic API returned {(int)response.StatusCode}");
            }

            var parsed = JsonSerializer.Deserialize<AnthropicResponse>(body, JsonOptions);
            if (parsed == null)
                throw new InvalidOperationException("Failed to parse Anthropic API response");
            return parsed;
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
    }

    // ===== Wire DTOs (snake_case via JsonPropertyName) =====

    public class AnthropicRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = "";
        [JsonPropertyName("max_tokens")] public int MaxTokens { get; set; }
        [JsonPropertyName("system")] public string? System { get; set; }
        [JsonPropertyName("messages")] public List<AnthropicMessage> Messages { get; set; } = new();
        [JsonPropertyName("tools")] public List<AnthropicTool>? Tools { get; set; }
    }

    public class AnthropicMessage
    {
        [JsonPropertyName("role")] public string Role { get; set; } = "user"; // "user" | "assistant"
        [JsonPropertyName("content")] public List<AnthropicContentBlock> Content { get; set; } = new();

        public static AnthropicMessage User(params AnthropicContentBlock[] blocks) =>
            new() { Role = "user", Content = blocks.ToList() };

        public static AnthropicMessage Assistant(params AnthropicContentBlock[] blocks) =>
            new() { Role = "assistant", Content = blocks.ToList() };
    }

    /// <summary>
    /// Union content block: text / image / tool_use (assistant echo) / tool_result.
    /// Null properties are omitted from the wire (WhenWritingNull).
    /// </summary>
    public class AnthropicContentBlock
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "text";

        // text
        [JsonPropertyName("text")] public string? Text { get; set; }

        // image
        [JsonPropertyName("source")] public AnthropicImageSource? Source { get; set; }

        // tool_use (echoed back in the assistant turn during the loop)
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("input")] public JsonElement? Input { get; set; }

        // tool_result
        [JsonPropertyName("tool_use_id")] public string? ToolUseId { get; set; }
        [JsonPropertyName("content")] public string? Content { get; set; }
        [JsonPropertyName("is_error")] public bool? IsError { get; set; }

        public static AnthropicContentBlock OfText(string text) => new() { Type = "text", Text = text };

        public static AnthropicContentBlock OfImage(string mediaType, string base64Data) => new()
        {
            Type = "image",
            Source = new AnthropicImageSource { MediaType = mediaType, Data = base64Data }
        };

        public static AnthropicContentBlock OfToolResult(string toolUseId, string content, bool isError = false) => new()
        {
            Type = "tool_result",
            ToolUseId = toolUseId,
            Content = content,
            IsError = isError ? true : null
        };
    }

    public class AnthropicImageSource
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "base64";
        [JsonPropertyName("media_type")] public string MediaType { get; set; } = "image/jpeg";
        [JsonPropertyName("data")] public string Data { get; set; } = "";
    }

    public class AnthropicTool
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("description")] public string Description { get; set; } = "";
        [JsonPropertyName("input_schema")] public object InputSchema { get; set; } = new { type = "object" };
    }

    public class AnthropicResponse
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("model")] public string? Model { get; set; }
        [JsonPropertyName("stop_reason")] public string? StopReason { get; set; }
        [JsonPropertyName("content")] public List<AnthropicResponseBlock> Content { get; set; } = new();
    }

    public class AnthropicResponseBlock
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "";
        [JsonPropertyName("text")] public string? Text { get; set; }

        // tool_use blocks
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("input")] public JsonElement Input { get; set; }
    }
}
