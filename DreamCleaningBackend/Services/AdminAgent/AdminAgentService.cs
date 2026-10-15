using System.Text.Json;
using System.Text.Json.Nodes;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Hubs;
using DreamCleaningBackend.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services.AdminAgent
{
    // ───────────────────────────────────────────── DTOs ───────────────────────────────────────────

    public class AdminAgentStatusDto
    {
        /// <summary>False when no API key is configured (AdminAgent:ApiKey or Anthropic:ApiKey).</summary>
        public bool IsConfigured { get; set; }

        /// <summary>False when ConnectionStrings:AgentReadOnly is missing - only run_sql is affected.</summary>
        public bool SqlConfigured { get; set; }

        public string Model { get; set; } = string.Empty;
        public bool CostEstimateAvailable { get; set; }
    }

    public class AdminAgentSessionDto
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long CacheReadTokens { get; set; }
        public long CacheWriteTokens { get; set; }
        public decimal? EstimatedCostUsd { get; set; }
    }

    public class AdminAgentMessageDto
    {
        public long Id { get; set; }
        public string Role { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public string? InputMode { get; set; }
        public string Status { get; set; } = string.Empty;
        public string? ProgressLabel { get; set; }
        public string? ErrorMessage { get; set; }

        /// <summary>Charts and tables (snake_case keys, see AdminAgentTools.RenderChart/RenderTable).</summary>
        public JsonNode? Parts { get; set; }

        /// <summary>"How this was calculated": the tool trail (snake_case keys).</summary>
        public JsonNode? Calculation { get; set; }

        public string? Model { get; set; }
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public int CacheReadTokens { get; set; }
        public int CacheWriteTokens { get; set; }
        public decimal? CostUsd { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
    }

    public class AdminAgentSessionDetailDto
    {
        public AdminAgentSessionDto Session { get; set; } = new();
        public List<AdminAgentMessageDto> Messages { get; set; } = new();
    }

    public class AdminAgentTurnStartedDto
    {
        public AdminAgentMessageDto UserMessage { get; set; } = new();
        public AdminAgentMessageDto AssistantMessage { get; set; } = new();
        public AdminAgentSessionDto Session { get; set; } = new();
    }

    /// <summary>A refusal the controller turns into an HTTP status with this message.</summary>
    public sealed class AdminAgentRequestException : Exception
    {
        public int StatusCode { get; }
        public AdminAgentRequestException(int statusCode, string message) : base(message) => StatusCode = statusCode;
    }

    // ─────────────────────────────────────────── Service ──────────────────────────────────────────

    public interface IAdminAgentService
    {
        AdminAgentStatusDto GetStatus();
        Task<List<AdminAgentSessionDto>> ListSessionsAsync(int ownerUserId);
        Task<AdminAgentSessionDto> CreateSessionAsync(int ownerUserId, string? title);
        Task<AdminAgentSessionDto?> RenameSessionAsync(int ownerUserId, int sessionId, string title);
        Task<bool> DeleteSessionAsync(int ownerUserId, int sessionId);
        Task<AdminAgentSessionDetailDto?> GetSessionAsync(int ownerUserId, int sessionId);
        Task<AdminAgentMessageDto?> GetMessageAsync(int ownerUserId, int sessionId, long messageId);

        /// <summary>
        /// Records the question, starts the answer in the background and returns at once — the
        /// browser follows progress over SignalR (or by polling the message), so a long tool loop
        /// never holds an HTTP request open through Apache and Cloudflare.
        /// </summary>
        Task<AdminAgentTurnStartedDto> StartTurnAsync(int ownerUserId, int sessionId, string text, string? inputMode);
    }

    /// <summary>
    /// The SuperAdmin analytics agent (2026-10): sessions, persistence and the background turn.
    /// Every query is scoped to the caller's own sessions — a SuperAdmin can never read, rename,
    /// delete or continue another SuperAdmin's conversation, even by guessing ids.
    /// </summary>
    public sealed class AdminAgentService : IAdminAgentService
    {
        public const int MaxQuestionLength = 4000;
        public const int MaxTitleLength = 200;
        public const string DefaultTitle = "New chat";

        /// <summary>A turn still "running" after this long died with the process (deploy, crash).</summary>
        internal static readonly TimeSpan StaleTurnAfter = TimeSpan.FromMinutes(20);

        private readonly ApplicationDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly IAgentSqlRunner _sql;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<AdminAgentService> _logger;

        public AdminAgentService(
            ApplicationDbContext context,
            IConfiguration configuration,
            IAgentSqlRunner sql,
            IServiceScopeFactory scopeFactory,
            ILogger<AdminAgentService> logger)
        {
            _context = context;
            _configuration = configuration;
            _sql = sql;
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        public AdminAgentStatusDto GetStatus()
        {
            var settings = AdminAgentSettings.From(_configuration);
            return new AdminAgentStatusDto
            {
                IsConfigured = settings.ApiKey != null,
                SqlConfigured = _sql.IsConfigured,
                Model = settings.Model,
                CostEstimateAvailable = settings.PriceInputPerMTok != null && settings.PriceOutputPerMTok != null
            };
        }

        public async Task<List<AdminAgentSessionDto>> ListSessionsAsync(int ownerUserId) =>
            (await _context.AdminAgentSessions.AsNoTracking()
                .Where(s => s.OwnerUserId == ownerUserId)
                .OrderByDescending(s => s.UpdatedAt)
                .ToListAsync())
            .Select(ToDto)
            .ToList();

        public async Task<AdminAgentSessionDto> CreateSessionAsync(int ownerUserId, string? title)
        {
            var now = DateTime.UtcNow;
            var session = new AdminAgentSession
            {
                OwnerUserId = ownerUserId,
                Title = CleanTitle(title) ?? DefaultTitle,
                CreatedAt = now,
                UpdatedAt = now
            };
            _context.AdminAgentSessions.Add(session);
            await _context.SaveChangesAsync();
            return ToDto(session);
        }

        public async Task<AdminAgentSessionDto?> RenameSessionAsync(int ownerUserId, int sessionId, string title)
        {
            var clean = CleanTitle(title)
                ?? throw new AdminAgentRequestException(400, "The title cannot be empty.");
            var session = await OwnedSessionAsync(ownerUserId, sessionId);
            if (session == null) return null;
            session.Title = clean;
            session.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            return ToDto(session);
        }

        public async Task<bool> DeleteSessionAsync(int ownerUserId, int sessionId)
        {
            var session = await OwnedSessionAsync(ownerUserId, sessionId);
            if (session == null) return false;
            _context.AdminAgentSessions.Remove(session);
            await _context.SaveChangesAsync();
            return true;
        }

        public async Task<AdminAgentSessionDetailDto?> GetSessionAsync(int ownerUserId, int sessionId)
        {
            var session = await OwnedSessionAsync(ownerUserId, sessionId);
            if (session == null) return null;
            var messages = await _context.AdminAgentMessages
                .Where(m => m.SessionId == sessionId)
                .OrderBy(m => m.Id)
                .ToListAsync();
            if (await FailStaleTurnsAsync(messages))
                await _context.SaveChangesAsync();
            return new AdminAgentSessionDetailDto { Session = ToDto(session), Messages = messages.Select(ToDto).ToList() };
        }

        public async Task<AdminAgentMessageDto?> GetMessageAsync(int ownerUserId, int sessionId, long messageId)
        {
            var message = await _context.AdminAgentMessages
                .Where(m => m.Id == messageId && m.SessionId == sessionId && m.Session!.OwnerUserId == ownerUserId)
                .FirstOrDefaultAsync();
            if (message == null) return null;
            if (await FailStaleTurnsAsync(new List<AdminAgentMessage> { message }))
                await _context.SaveChangesAsync();
            return ToDto(message);
        }

        public async Task<AdminAgentTurnStartedDto> StartTurnAsync(int ownerUserId, int sessionId, string text, string? inputMode)
        {
            var question = text?.Trim() ?? string.Empty;
            if (question.Length == 0)
                throw new AdminAgentRequestException(400, "Type a question first.");
            if (question.Length > MaxQuestionLength)
                throw new AdminAgentRequestException(400, $"The question is longer than {MaxQuestionLength} characters.");

            var settings = AdminAgentSettings.From(_configuration);
            if (settings.ApiKey == null)
                throw new AdminAgentRequestException(503,
                    "The analytics agent is not configured: no AI API key is set (AdminAgent:ApiKey or Anthropic:ApiKey).");

            var session = await OwnedSessionAsync(ownerUserId, sessionId);
            if (session == null)
                throw new AdminAgentRequestException(404, "Chat not found.");

            var running = await _context.AdminAgentMessages
                .Where(m => m.SessionId == sessionId && m.Status == AdminAgentMessageStatuses.Running)
                .ToListAsync();
            await FailStaleTurnsAsync(running);
            if (running.Any(m => m.Status == AdminAgentMessageStatuses.Running))
                throw new AdminAgentRequestException(409, "The previous question in this chat is still being answered.");

            var nowUtc = DateTime.UtcNow;
            var nowNy = NyTimeHelper.NowNy;
            // The date travels with the question, not in the system prompt: the prompt must stay
            // byte-identical across turns (see AdminAgentSystemPrompt).
            var context = $"[Context: the current date and time in New York is {nowNy:dddd, yyyy-MM-dd HH:mm}.]";
            var apiUser = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "text", ["text"] = context },
                        new JsonObject { ["type"] = "text", ["text"] = question }
                    }
                }
            };

            var userMessage = new AdminAgentMessage
            {
                SessionId = sessionId,
                Role = AdminAgentMessageRoles.User,
                Content = question,
                InputMode = inputMode is "voice" ? "voice" : "text",
                Status = AdminAgentMessageStatuses.Complete,
                ApiMessagesJson = apiUser.ToJsonString(),
                CreatedAt = nowUtc,
                CompletedAt = nowUtc
            };
            var assistantMessage = new AdminAgentMessage
            {
                SessionId = sessionId,
                Role = AdminAgentMessageRoles.Assistant,
                Status = AdminAgentMessageStatuses.Running,
                ProgressLabel = "Thinking…",
                Model = settings.Model,
                CreatedAt = nowUtc
            };
            _context.AdminAgentMessages.Add(userMessage);
            _context.AdminAgentMessages.Add(assistantMessage);
            if (session.Title == DefaultTitle)
                session.Title = CleanTitle(question.Length > 80 ? question[..80] + "…" : question) ?? DefaultTitle;
            session.UpdatedAt = nowUtc;
            await _context.SaveChangesAsync();

            var assistantId = assistantMessage.Id;
            BackgroundWork.Run(_scopeFactory, _logger, $"analytics agent turn {assistantId}",
                sp => sp.GetRequiredService<AdminAgentTurnRunner>().RunAsync(ownerUserId, sessionId, assistantId));

            return new AdminAgentTurnStartedDto
            {
                UserMessage = ToDto(userMessage),
                AssistantMessage = ToDto(assistantMessage),
                Session = ToDto(session)
            };
        }

        private Task<AdminAgentSession?> OwnedSessionAsync(int ownerUserId, int sessionId) =>
            _context.AdminAgentSessions.FirstOrDefaultAsync(s => s.Id == sessionId && s.OwnerUserId == ownerUserId);

        /// <summary>Marks turns orphaned by a restart as failed. Returns true when anything changed.</summary>
        private static Task<bool> FailStaleTurnsAsync(List<AdminAgentMessage> messages)
        {
            var changed = false;
            foreach (var m in messages.Where(m => m.Status == AdminAgentMessageStatuses.Running
                                                  && DateTime.UtcNow - m.CreatedAt > StaleTurnAfter))
            {
                m.Status = AdminAgentMessageStatuses.Error;
                m.ErrorMessage = "This answer was interrupted (the server restarted). Please ask again.";
                m.ProgressLabel = null;
                m.CompletedAt = DateTime.UtcNow;
                changed = true;
            }
            return Task.FromResult(changed);
        }

        private static string? CleanTitle(string? title)
        {
            var t = title?.Trim().Replace('\n', ' ').Replace('\r', ' ');
            if (string.IsNullOrWhiteSpace(t)) return null;
            return t.Length > MaxTitleLength ? t[..MaxTitleLength] : t;
        }

        internal static AdminAgentSessionDto ToDto(AdminAgentSession s) => new()
        {
            Id = s.Id,
            Title = s.Title,
            CreatedAt = s.CreatedAt,
            UpdatedAt = s.UpdatedAt,
            InputTokens = s.InputTokens,
            OutputTokens = s.OutputTokens,
            CacheReadTokens = s.CacheReadTokens,
            CacheWriteTokens = s.CacheWriteTokens,
            EstimatedCostUsd = s.EstimatedCostUsd
        };

        internal static AdminAgentMessageDto ToDto(AdminAgentMessage m) => new()
        {
            Id = m.Id,
            Role = m.Role,
            Content = m.Content,
            InputMode = m.InputMode,
            Status = m.Status,
            ProgressLabel = m.ProgressLabel,
            ErrorMessage = m.ErrorMessage,
            Parts = string.IsNullOrEmpty(m.PartsJson) ? null : JsonNode.Parse(m.PartsJson),
            Calculation = string.IsNullOrEmpty(m.ToolCallsJson) ? null : JsonNode.Parse(m.ToolCallsJson),
            Model = m.Model,
            InputTokens = m.InputTokens,
            OutputTokens = m.OutputTokens,
            CacheReadTokens = m.CacheReadTokens,
            CacheWriteTokens = m.CacheWriteTokens,
            CostUsd = m.CostUsd,
            CreatedAt = m.CreatedAt,
            CompletedAt = m.CompletedAt
        };
    }

    // ────────────────────────────────────────── Turn runner ───────────────────────────────────────

    /// <summary>
    /// Runs one answer in its own DI scope (started by <see cref="BackgroundWork"/>): the
    /// Messages API tool loop, the tools, persistence and the SignalR progress notices.
    /// </summary>
    public sealed class AdminAgentTurnRunner
    {
        public const string ProgressEvent = "AdminAgentProgress";
        public const string CompletedEvent = "AdminAgentTurnCompleted";

        // Models known to take preserved-thinking binding controls and server-side refusal
        // fallbacks. Anything else configured in AdminAgent:Model is sent a plain request.
        private static readonly HashSet<string> BindingControlModels = new(StringComparer.OrdinalIgnoreCase)
            { "claude-sonnet-5-5", "claude-opus-5-5", "claude-fable-5-1" };
        private static readonly HashSet<string> FallbackModels = new(StringComparer.OrdinalIgnoreCase)
            { "claude-sonnet-5-5", "claude-opus-5-5", "claude-opus-5", "claude-fable-5-1" };

        private const string BindingBeta = "thinking-binding-controls-2026-08-01";
        private const string FallbackBeta = "server-side-fallback-2026-07-01";

        private readonly ApplicationDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly AdminAgentAnthropicClient _client;
        private readonly AdminAgentTools _tools;
        private readonly IHubContext<UserManagementHub> _hub;
        private readonly ILogger<AdminAgentTurnRunner> _logger;

        public AdminAgentTurnRunner(
            ApplicationDbContext context,
            IConfiguration configuration,
            AdminAgentAnthropicClient client,
            AdminAgentTools tools,
            IHubContext<UserManagementHub> hub,
            ILogger<AdminAgentTurnRunner> logger)
        {
            _context = context;
            _configuration = configuration;
            _client = client;
            _tools = tools;
            _hub = hub;
            _logger = logger;
        }

        public async Task RunAsync(int ownerUserId, int sessionId, long assistantMessageId)
        {
            var message = await _context.AdminAgentMessages.FirstOrDefaultAsync(m => m.Id == assistantMessageId);
            var session = await _context.AdminAgentSessions.FirstOrDefaultAsync(s => s.Id == sessionId && s.OwnerUserId == ownerUserId);
            if (message == null || session == null) return;

            var settings = AdminAgentSettings.From(_configuration);
            var ctx = new AgentToolContext { UserId = ownerUserId, SessionId = sessionId, MessageId = assistantMessageId };
            var usage = new UsageTotals();
            var turnMessages = new JsonArray();

            try
            {
                if (settings.ApiKey == null)
                    throw new AdminAgentApiException("The analytics agent is not configured: no AI API key is set.");

                var history = await BuildHistoryAsync(sessionId, assistantMessageId);
                var system = AdminAgentSystemPrompt.Build();
                var tools = AdminAgentTools.Definitions();
                using var timeout = new CancellationTokenSource(AdminAgentService.StaleTurnAfter - TimeSpan.FromMinutes(2));

                string? finalText = null;
                for (var call = 0; call <= settings.MaxToolIterations; call++)
                {
                    var body = BuildRequest(settings, system, tools, history, turnMessages);
                    using var doc = await _client.SendAsync(settings.ApiKey, body, Betas(settings.Model), timeout.Token);
                    var root = doc.RootElement;
                    usage.Add(root);

                    var stopReason = root.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : null;
                    var content = root.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array
                        ? c : default;

                    if (stopReason == "refusal")
                    {
                        // A declined answer is not replayed later: the pair is left out of context.
                        throw new AgentRefusalException(
                            "The AI service declined to answer this question. Try rephrasing it as a business-data question.");
                    }

                    // Append the assistant turn EXACTLY as it came back (thinking blocks included).
                    turnMessages.Add(new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = content.ValueKind == JsonValueKind.Array ? JsonNode.Parse(content.GetRawText()) : new JsonArray()
                    });

                    var toolUses = content.ValueKind == JsonValueKind.Array
                        ? content.EnumerateArray().Where(b => b.TryGetProperty("type", out var t) && t.GetString() == "tool_use").ToList()
                        : new List<JsonElement>();

                    if (stopReason != "tool_use" || toolUses.Count == 0)
                    {
                        finalText = TextOf(content);
                        if (stopReason == "max_tokens")
                            finalText = (finalText + "\n\n*(The answer was cut off at the output limit. Ask a narrower question.)*").Trim();
                        break;
                    }

                    var results = new JsonArray();
                    foreach (var toolUse in toolUses)
                    {
                        var name = toolUse.GetProperty("name").GetString() ?? string.Empty;
                        var id = toolUse.GetProperty("id").GetString() ?? string.Empty;
                        await ProgressAsync(ownerUserId, message, AdminAgentTools.ProgressLabel(name));
                        var input = toolUse.TryGetProperty("input", out var inp) ? inp : default;
                        var result = await _tools.ExecuteAsync(name, input, ctx, timeout.Token);
                        var block = new JsonObject
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = id,
                            ["content"] = result.Content
                        };
                        if (result.IsError) block["is_error"] = true;
                        results.Add(block);
                    }

                    // On the last allowed round, ask for an answer with what is there instead of
                    // more tool calls. Appended, never inserted, so earlier thinking stays valid.
                    if (call == settings.MaxToolIterations - 1)
                        results.Add(new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = "[You have reached the tool-call limit for this question. Answer now with the results you have, and say plainly what you could not determine.]"
                        });

                    turnMessages.Add(new JsonObject { ["role"] = "user", ["content"] = results });
                    await ProgressAsync(ownerUserId, message, "Thinking…");
                }

                if (finalText == null)
                {
                    // The model was still calling tools after the limit: the transcript ends on a
                    // tool result, so it cannot be replayed - fail the turn rather than store it.
                    throw new AgentRefusalException(
                        "This question needed more steps than allowed. Try splitting it into smaller questions.");
                }

                message.Content = string.IsNullOrWhiteSpace(finalText) ? "(No answer text was returned.)" : finalText;
                message.Status = AdminAgentMessageStatuses.Complete;
                message.ApiMessagesJson = turnMessages.ToJsonString();
            }
            catch (Exception ex)
            {
                // Everything ends the turn as a visible error: an exception escaping here would
                // leave the message "running" until the stale-turn sweep, with a spinner nobody
                // can stop.
                _logger.LogWarning(ex, "Analytics agent turn {MessageId} failed", assistantMessageId);
                message.Status = AdminAgentMessageStatuses.Error;
                message.ErrorMessage = ex switch
                {
                    AgentRefusalException r => r.Message,
                    AdminAgentApiException a when a.StatusCode is 401 or 403 => "The AI service rejected the API key (AdminAgent:ApiKey / Anthropic:ApiKey).",
                    AdminAgentApiException a when a.StatusCode is 429 or 529 => "The AI service is busy right now. Please try again in a minute.",
                    AdminAgentApiException a => a.Message,
                    OperationCanceledException => "The answer took too long and was stopped. Try a narrower question.",
                    HttpRequestException => "The AI service could not be reached. Please try again.",
                    _ => "Something went wrong while answering. Please try again."
                };
                // A failed turn is left out of later context (see BuildHistoryAsync).
                message.ApiMessagesJson = null;
            }

            message.ProgressLabel = null;
            message.CompletedAt = DateTime.UtcNow;
            message.PartsJson = ctx.Parts.Count > 0 ? ctx.Parts.ToJsonString() : null;
            message.ToolCallsJson = ctx.Trail.Count > 0 ? JsonSerializer.Serialize(ctx.Trail, AdminAgentTools.Json) : null;
            message.InputTokens = usage.Input;
            message.OutputTokens = usage.Output;
            message.CacheReadTokens = usage.CacheRead;
            message.CacheWriteTokens = usage.CacheWrite;
            message.CostUsd = settings.EstimateCost(usage.Input, usage.Output, usage.CacheWrite, usage.CacheRead);

            session.InputTokens += usage.Input;
            session.OutputTokens += usage.Output;
            session.CacheReadTokens += usage.CacheRead;
            session.CacheWriteTokens += usage.CacheWrite;
            if (message.CostUsd is { } cost)
                session.EstimatedCostUsd = (session.EstimatedCostUsd ?? 0m) + cost;
            session.UpdatedAt = DateTime.UtcNow;

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException ex)
            {
                // The session may have been deleted while the answer was being written.
                _logger.LogWarning(ex, "Analytics agent turn {MessageId} could not be saved", assistantMessageId);
            }

            await NotifyAsync(ownerUserId, CompletedEvent, new { sessionId, messageId = assistantMessageId, status = message.Status });
        }

        private JsonObject BuildRequest(AdminAgentSettings settings, string system, JsonArray tools, JsonArray history, JsonArray turn)
        {
            var messages = new JsonArray();
            foreach (var m in history) messages.Add(m!.DeepClone());
            foreach (var m in turn) messages.Add(m!.DeepClone());

            var body = new JsonObject
            {
                ["model"] = settings.Model,
                ["max_tokens"] = settings.MaxTokens,
                ["system"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = system,
                        ["cache_control"] = new JsonObject { ["type"] = "ephemeral" }
                    }
                },
                ["tools"] = tools.DeepClone(),
                ["messages"] = messages,
                // Automatic caching of the growing conversation (the system block above carries
                // its own breakpoint).
                ["cache_control"] = new JsonObject { ["type"] = "ephemeral" },
                ["output_config"] = new JsonObject { ["effort"] = settings.Effort }
            };

            if (BindingControlModels.Contains(settings.Model))
            {
                // Edited history (a deploy that changes the prompt or tools) drops the stale
                // thinking blocks instead of failing the request.
                body["thinking"] = new JsonObject
                {
                    ["type"] = "adaptive",
                    ["block_binding"] = new JsonObject { ["prefix_mismatch_behavior"] = "drop_block" }
                };
            }
            if (FallbackModels.Contains(settings.Model))
                body["fallbacks"] = "default";
            return body;
        }

        private static IEnumerable<string> Betas(string model)
        {
            if (BindingControlModels.Contains(model)) yield return BindingBeta;
            if (FallbackModels.Contains(model)) yield return FallbackBeta;
        }

        /// <summary>
        /// The conversation so far, exactly as it was sent before: every question whose answer
        /// completed, followed by that answer's recorded API messages, then the current question.
        /// Failed or refused turns are left out entirely, question included.
        /// </summary>
        private async Task<JsonArray> BuildHistoryAsync(int sessionId, long currentAssistantId)
        {
            var rows = await _context.AdminAgentMessages.AsNoTracking()
                .Where(m => m.SessionId == sessionId && m.Id < currentAssistantId)
                .OrderBy(m => m.Id)
                .Select(m => new { m.Id, m.Role, m.ApiMessagesJson })
                .ToListAsync();

            var history = new JsonArray();
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row.Role != AdminAgentMessageRoles.User || row.ApiMessagesJson == null) continue;

                var isCurrent = i == rows.Count - 1;
                if (isCurrent)
                {
                    AppendAll(history, row.ApiMessagesJson);
                    continue;
                }

                var answer = i + 1 < rows.Count ? rows[i + 1] : null;
                if (answer is { Role: AdminAgentMessageRoles.Assistant, ApiMessagesJson: not null })
                {
                    AppendAll(history, row.ApiMessagesJson);
                    AppendAll(history, answer.ApiMessagesJson);
                }
            }
            return history;
        }

        private static void AppendAll(JsonArray target, string json)
        {
            if (JsonNode.Parse(json) is JsonArray items)
                foreach (var item in items.ToList())
                {
                    items.Remove(item);
                    target.Add(item);
                }
        }

        private static string TextOf(JsonElement content)
        {
            if (content.ValueKind != JsonValueKind.Array) return string.Empty;
            var parts = content.EnumerateArray()
                .Where(b => b.TryGetProperty("type", out var t) && t.GetString() == "text"
                            && b.TryGetProperty("text", out var x) && !string.IsNullOrWhiteSpace(x.GetString()))
                .Select(b => b.GetProperty("text").GetString()!.Trim());
            return string.Join("\n\n", parts);
        }

        private async Task ProgressAsync(int userId, AdminAgentMessage message, string label)
        {
            message.ProgressLabel = label;
            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException ex)
            {
                _logger.LogDebug(ex, "Analytics agent progress label not saved");
            }
            await NotifyAsync(userId, ProgressEvent, new { sessionId = message.SessionId, messageId = message.Id, label });
        }

        private async Task NotifyAsync(int userId, string evt, object payload)
        {
            try
            {
                // The owner's own group only — another SuperAdmin never sees this conversation.
                await _hub.Clients.Group($"User_{userId}").SendAsync(evt, payload);
            }
            catch (Exception ex)
            {
                // Progress is a convenience; the page also polls the message.
                _logger.LogDebug(ex, "Analytics agent SignalR notice {Event} not sent", evt);
            }
        }

        private sealed class UsageTotals
        {
            public int Input, Output, CacheRead, CacheWrite;

            public void Add(JsonElement root)
            {
                if (!root.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) return;
                Input += Read(u, "input_tokens");
                Output += Read(u, "output_tokens");
                CacheRead += Read(u, "cache_read_input_tokens");
                CacheWrite += Read(u, "cache_creation_input_tokens");
            }

            private static int Read(JsonElement u, string name) =>
                u.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;
        }

        private sealed class AgentRefusalException : Exception
        {
            public AgentRefusalException(string message) : base(message) { }
        }
    }
}
