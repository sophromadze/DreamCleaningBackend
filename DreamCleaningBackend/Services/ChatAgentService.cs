using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;

namespace DreamCleaningBackend.Services
{
    public interface IChatAgentService
    {
        /// <summary>Handles one visitor message: persists it, runs the AI tool-use loop
        /// (or relays to Telegram when already escalated), persists the reply.
        /// isAdmin (resolved from the validated JWT role by the controller) merely PERMITS
        /// internal content-QA mode; it activates only when the admin's message also carries
        /// the "QA:"/"/qa" prefix. Throws ArgumentException for caller errors (→ 400).</summary>
        Task<ChatMessageResponseDto> HandleMessageAsync(ChatMessageRequestDto dto, int? userId, bool isAdmin = false);

        /// <summary>Marks a session Resolved (customer End-chat or admin Mark-Resolved):
        /// writes a System audit row and posts a courtesy note to the Telegram topic if
        /// one exists. Returns false when the session doesn't exist.</summary>
        Task<bool> ResolveSessionAsync(Guid sessionId, string endedByLabel);

        /// <summary>Hard-deletes a session and everything under it: purges its chat-photo
        /// files from disk, then removes the session row (the FK cascade removes all its
        /// messages). Telegram is intentionally left untouched. Returns false when the
        /// session doesn't exist. SuperAdmin-gated at the controller.</summary>
        Task<bool> DeleteSessionAsync(Guid sessionId);
    }

    /// <summary>
    /// AI chat-agent orchestration. Owns the Anthropic tool-use loop with three tools —
    /// get_service_catalog and calculate_price_estimate (wired in-process to
    /// IChatCatalogService, never an HTTP loopback) and escalate_to_human (flips the
    /// session to EscalatedToHuman, creates/reuses a Telegram Forum Topic via the
    /// existing TelegramBotService, posts the transcript, and emails the team when the
    /// admin-toggleable ChatAgentSettings.EscalationEmailEnabled is on).
    /// Degrades gracefully: Telegram disabled → escalation persists in DB + email only;
    /// Anthropic key missing → canned unavailable reply.
    /// </summary>
    public class ChatAgentService : IChatAgentService
    {
        private const int MaxHistoryMessages = 30;
        private const int MaxToolIterations = 5;
        private const int MaxMessageLength = 4000;
        private const int TelegramChunkSize = 3500; // Telegram caps messages at 4096 chars

        private const string EscalatedReply =
            "I've forwarded this conversation to our team — a real person will reply right here shortly. Feel free to add any details in the meantime.";
        private const string UnavailableReply =
            "Sorry — our chat assistant is temporarily unavailable. Please use the contact form on our website or give us a call, and we'll be happy to help.";
        private const string TroubleReply =
            "Sorry, I'm having trouble answering right now. Please try again in a moment, or ask me to connect you with our team.";

        private static readonly Regex ImagePathRegex =
            new(@"^/chat-photos/[0-9a-fA-F]{32}\.(jpg|png|webp)$", RegexOptions.Compiled);

        private static readonly JsonSerializerOptions ToolJsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        private static readonly JsonSerializerOptions ToolInputJsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly ApplicationDbContext _context;
        private readonly AnthropicMessagesClient _anthropic;
        private readonly IChatCatalogService _catalog;
        private readonly IWebsitePageContentService _websiteContent;
        private readonly TelegramBotService _telegramBot;
        private readonly IEmailService _emailService;
        private readonly IConfiguration _configuration;
        private readonly ILogger<ChatAgentService> _logger;

        public ChatAgentService(
            ApplicationDbContext context,
            AnthropicMessagesClient anthropic,
            IChatCatalogService catalog,
            IWebsitePageContentService websiteContent,
            TelegramBotService telegramBot,
            IEmailService emailService,
            IConfiguration configuration,
            ILogger<ChatAgentService> logger)
        {
            _context = context;
            _anthropic = anthropic;
            _catalog = catalog;
            _websiteContent = websiteContent;
            _telegramBot = telegramBot;
            _emailService = emailService;
            _configuration = configuration;
            _logger = logger;
        }

        public async Task<ChatMessageResponseDto> HandleMessageAsync(ChatMessageRequestDto dto, int? userId, bool isAdmin = false)
        {
            var text = (dto.Message ?? string.Empty).Trim();
            if (text.Length > MaxMessageLength)
                text = text[..MaxMessageLength];

            // QA mode is opt-in PER MESSAGE: an admin prefixes a message with "QA:" or
            // "/qa" to enter internal content-QA mode for that one message. The prefix is
            // stripped here so it reaches neither Claude nor the stored transcript; without
            // it, an admin's message runs the exact normal customer flow (escalation on,
            // standard prompt/cap). The isAdmin gate means guests/customers can never
            // trigger it — for them a leading "QA:" is just literal text.
            var qaMode = false;
            if (isAdmin && TryStripQaPrefix(text, out var strippedForQa))
            {
                qaMode = true;
                text = strippedForQa;
            }

            var imagePath = NormalizeImagePath(dto.ImagePath);
            if (text.Length == 0 && imagePath == null)
                throw new ArgumentException("A message or an image is required");

            var now = DateTime.UtcNow;

            // Load or create the session. A Resolved session never accepts appends —
            // a stale client (second tab, old localStorage) gets a FRESH session
            // instead, matching the widget's "start new chat" semantics.
            ChatAgentSession? session = null;
            if (dto.SessionId.HasValue)
                session = await _context.ChatAgentSessions.FirstOrDefaultAsync(s => s.Id == dto.SessionId.Value);
            if (session?.Status == ChatSessionStatus.Resolved)
                session = null;

            if (session == null)
            {
                session = new ChatAgentSession
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    GuestIdentifier = Truncate(dto.GuestIdentifier, 64),
                    // Guests only — captured from the widget's optional start-of-chat field.
                    // Logged-in users use their account email, so this stays null for them.
                    GuestEmail = userId == null ? NormalizeGuestEmail(dto.GuestEmail) : null,
                    Status = ChatSessionStatus.AiHandling,
                    CreatedAt = now,
                    LastMessageAt = now
                };
                _context.ChatAgentSessions.Add(session);
            }
            else if (session.UserId == null && userId != null)
            {
                session.UserId = userId; // visitor logged in mid-conversation
            }

            // Audit marker for QA-triggered turns — a System row (excluded from Claude's
            // history AND the customer widget, shown in the admin transcript viewer) so
            // it's visible which turns ran in QA mode. Only added when the AI loop will
            // actually apply QA (not on an escalated relay, where QA has no effect).
            // Timestamped just before the user message so it sorts directly above it.
            if (qaMode && session.Status == ChatSessionStatus.AiHandling)
            {
                _context.ChatAgentMessages.Add(new ChatAgentMessage
                {
                    Id = Guid.NewGuid(),
                    ChatSessionId = session.Id,
                    Role = ChatMessageRole.System,
                    Content = "[QA mode] Internal content-QA question (admin).",
                    CreatedAt = now.AddMilliseconds(-1)
                });
            }

            // Persist the visitor's message first — it must survive any AI/Telegram failure
            _context.ChatAgentMessages.Add(new ChatAgentMessage
            {
                Id = Guid.NewGuid(),
                ChatSessionId = session.Id,
                Role = ChatMessageRole.User,
                Content = text.Length > 0 ? text : null,
                ImagePath = imagePath,
                ImageExpiresAt = imagePath != null ? now.AddDays(30) : null,
                CreatedAt = now
            });
            session.LastMessageAt = now;
            await _context.SaveChangesAsync();

            // Escalated sessions bypass the AI entirely — relay straight to the team.
            // Reply is null on purpose: the one-time "forwarded to our team" text was
            // already shown at escalation, so subsequent sends add no bot bubble; the
            // human's actual answer arrives via the widget's polling. Guest email is now
            // collected up front by the widget's start-of-chat field, so there is no
            // post-escalation email capture here anymore.
            if (session.Status == ChatSessionStatus.EscalatedToHuman)
            {
                await RelayToTelegramAsync(session, text, imagePath);
                return new ChatMessageResponseDto { SessionId = session.Id, Reply = null, Escalated = true };
            }

            string reply;
            var escalated = false;
            List<string>? quickReplies = null;

            if (!_anthropic.IsConfigured)
            {
                reply = UnavailableReply;
            }
            else
            {
                try
                {
                    (reply, escalated, quickReplies) = await RunAgentLoopAsync(session, qaMode);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Chat agent loop failed for session {SessionId}", session.Id);
                    reply = TroubleReply;
                    // If escalation already committed inside the loop the session status reflects it
                    escalated = session.Status == ChatSessionStatus.EscalatedToHuman;
                }
            }

            _context.ChatAgentMessages.Add(new ChatAgentMessage
            {
                Id = Guid.NewGuid(),
                ChatSessionId = session.Id,
                Role = ChatMessageRole.Assistant,
                Content = reply,
                CreatedAt = DateTime.UtcNow
            });
            session.LastMessageAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return new ChatMessageResponseDto
            {
                SessionId = session.Id,
                Reply = reply,
                Escalated = escalated,
                QuickReplies = quickReplies
            };
        }

        public async Task<bool> ResolveSessionAsync(Guid sessionId, string endedByLabel)
        {
            var session = await _context.ChatAgentSessions.FirstOrDefaultAsync(s => s.Id == sessionId);
            if (session == null)
                return false;

            if (session.Status != ChatSessionStatus.Resolved)
            {
                session.Status = ChatSessionStatus.Resolved;
                _context.ChatAgentMessages.Add(new ChatAgentMessage
                {
                    Id = Guid.NewGuid(),
                    ChatSessionId = session.Id,
                    Role = ChatMessageRole.System,
                    Content = $"Conversation ended ({endedByLabel}).",
                    CreatedAt = DateTime.UtcNow
                });
                session.LastMessageAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();

                // Courtesy note so the team isn't left replying into a closed chat.
                if (_telegramBot.IsConfigured && session.TelegramTopicId is > 0)
                {
                    try
                    {
                        await _telegramBot.SendTextToTopic(session.TelegramTopicId.Value, "Chat Agent",
                            $"This conversation was marked as resolved ({endedByLabel}). The visitor no longer receives replies here.");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to post resolve note to Telegram topic {TopicId}", session.TelegramTopicId);
                    }
                }
            }
            return true;
        }

        public async Task<bool> DeleteSessionAsync(Guid sessionId)
        {
            var session = await _context.ChatAgentSessions.FirstOrDefaultAsync(s => s.Id == sessionId);
            if (session == null)
                return false;

            // Collect chat-photo paths BEFORE the delete — the FK cascade wipes the
            // message rows, so we'd lose the paths otherwise.
            var imagePaths = await _context.ChatAgentMessages
                .Where(m => m.ChatSessionId == sessionId && m.ImagePath != null)
                .Select(m => m.ImagePath!)
                .ToListAsync();

            foreach (var imagePath in imagePaths)
            {
                try
                {
                    var fullPath = ResolvePhysicalPath(imagePath); // null if already gone
                    if (fullPath != null)
                        File.Delete(fullPath);
                }
                catch (Exception ex)
                {
                    // Keep going — the DB rows are still removed so no URL keeps resolving.
                    _logger.LogWarning(ex, "Failed to delete chat image {Path} during session delete", imagePath);
                }
            }

            // FK cascade (ChatAgentMessages → ChatAgentSessions, ON DELETE CASCADE) removes
            // every message row automatically — no manual message deletion needed. Telegram
            // is intentionally left untouched (orphaned-topic replies are handled gracefully
            // by the webhook's "no session for topic" path).
            _context.ChatAgentSessions.Remove(session);
            await _context.SaveChangesAsync();
            return true;
        }

        // ===== AI tool-use loop =====

        private async Task<(string Reply, bool Escalated, List<string>? QuickReplies)> RunAgentLoopAsync(ChatAgentSession session, bool qaMode)
        {
            var messages = await BuildApiHistoryAsync(session.Id);
            if (messages.Count == 0)
                return (TroubleReply, false, null);

            // QA mode (admin + "QA:"/"/qa" prefix, resolved upstream) gets the internal
            // content-QA addendum + a larger output cap so discrepancy reports aren't
            // truncated. A normal admin message (no prefix) runs the standard flow.
            var systemPrompt = _configuration["ChatAgent:SystemPrompt"] ?? ChatAgentSystemPrompt.Default;
            if (qaMode)
                systemPrompt += ChatAgentSystemPrompt.AdminAddendum;
            var maxTokensOverride = qaMode
                ? _configuration.GetValue<int>("ChatAgent:AdminMaxTokens", 3000)
                : (int?)null;
            var tools = BuildTools();

            for (var iteration = 0; iteration < MaxToolIterations; iteration++)
            {
                var response = await _anthropic.CreateMessageAsync(systemPrompt, messages, tools, maxTokensOverride);

                var textParts = response.Content
                    .Where(b => b.Type == "text" && !string.IsNullOrWhiteSpace(b.Text))
                    .Select(b => b.Text!.Trim())
                    .ToList();
                var toolUses = response.Content.Where(b => b.Type == "tool_use").ToList();

                if (response.StopReason != "tool_use" || toolUses.Count == 0)
                    return (textParts.Count > 0 ? string.Join("\n\n", textParts) : TroubleReply, false, null);

                // present_choices is a UI pseudo-tool: it ends the turn immediately and its
                // arguments go straight to the widget — no tool_result, no further model call.
                // (Safe because history is rebuilt from persisted text each turn, so the
                // unanswered tool_use block is never replayed.) Escalation takes precedence
                // if the model ever calls both; invalid arguments fall through to the normal
                // execution path, where the tool handler returns an error so the model retries.
                var choicesCall = toolUses.FirstOrDefault(t => t.Name == "present_choices");
                var hasEscalate = toolUses.Any(t => t.Name == "escalate_to_human");
                if (choicesCall != null && !hasEscalate)
                {
                    var parsed = TryParsePresentChoices(choicesCall.Input);
                    if (parsed != null)
                    {
                        var (question, options) = parsed.Value;
                        var lead = string.Join("\n\n", textParts);
                        // Models often repeat the question in a text block — don't show it twice.
                        var reply = textParts.Count == 0
                            ? question
                            : lead.Contains(question, StringComparison.OrdinalIgnoreCase)
                                ? lead
                                : lead + "\n\n" + question;
                        return (reply, false, options);
                    }
                }

                // Echo the assistant turn (text + tool_use blocks) back into the conversation
                messages.Add(new AnthropicMessage
                {
                    Role = "assistant",
                    Content = response.Content.Select(ToRequestBlock).ToList()
                });

                var results = new List<AnthropicContentBlock>();
                var escalatedThisTurn = false;

                foreach (var toolUse in toolUses)
                {
                    var result = await ExecuteToolAsync(session, toolUse);
                    results.Add(result.Block);
                    escalatedThisTurn |= result.Escalated;
                }

                messages.Add(new AnthropicMessage { Role = "user", Content = results });

                if (escalatedThisTurn)
                {
                    // Escalation ends the AI's involvement — don't call Claude again.
                    // Guest email (if any) was collected up front by the widget, so nothing
                    // is asked for here.
                    var lead = textParts.Count > 0 ? string.Join("\n\n", textParts) + "\n\n" : string.Empty;
                    return (lead + EscalatedReply, true, null);
                }
            }

            _logger.LogWarning("Chat agent hit the tool-iteration cap for session {SessionId}", session.Id);
            return (TroubleReply, false, null);
        }

        /// <summary>Validates present_choices arguments: 1–8 non-empty options (≤80 chars,
        /// de-duplicated) + a question. Returns null when unusable so the caller falls
        /// through to the error-retry path.</summary>
        private static (string Question, List<string> Options)? TryParsePresentChoices(System.Text.Json.JsonElement input)
        {
            if (input.ValueKind != JsonValueKind.Object ||
                !input.TryGetProperty("options", out var optionsElement) ||
                optionsElement.ValueKind != JsonValueKind.Array)
                return null;

            var options = optionsElement.EnumerateArray()
                .Where(o => o.ValueKind == JsonValueKind.String)
                .Select(o => (o.GetString() ?? string.Empty).Trim())
                .Where(o => o.Length > 0)
                .Select(o => o.Length <= 80 ? o : o[..80])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();
            if (options.Count == 0)
                return null;

            var question = input.TryGetProperty("question", out var q) && q.ValueKind == JsonValueKind.String
                ? (q.GetString() ?? string.Empty).Trim()
                : string.Empty;
            if (question.Length == 0)
                question = "Please choose an option:";
            if (question.Length > 300)
                question = question[..300];

            return (question, options);
        }

        private async Task<(AnthropicContentBlock Block, bool Escalated)> ExecuteToolAsync(
            ChatAgentSession session, AnthropicResponseBlock toolUse)
        {
            var id = toolUse.Id ?? string.Empty;
            try
            {
                switch (toolUse.Name)
                {
                    case "get_service_catalog":
                        var catalog = await _catalog.GetServiceCatalogAsync();
                        return (AnthropicContentBlock.OfToolResult(id, JsonSerializer.Serialize(catalog, ToolJsonOptions)), false);

                    case "calculate_price_estimate":
                        var request = JsonSerializer.Deserialize<ChatEstimateRequestDto>(
                            toolUse.Input.GetRawText(), ToolInputJsonOptions);
                        if (request == null)
                            return (AnthropicContentBlock.OfToolResult(id, "Invalid estimate input", true), false);
                        var (estimate, error) = await _catalog.EstimateAsync(request);
                        return error != null
                            ? (AnthropicContentBlock.OfToolResult(id, error, true), false)
                            : (AnthropicContentBlock.OfToolResult(id, JsonSerializer.Serialize(estimate, ToolJsonOptions)), false);

                    case "get_page_content":
                        var topic = toolUse.Input.ValueKind == JsonValueKind.Object &&
                                    toolUse.Input.TryGetProperty("topic", out var t) &&
                                    t.ValueKind == JsonValueKind.String
                            ? t.GetString() ?? string.Empty
                            : string.Empty;
                        var content = await _websiteContent.GetContentAsync(topic);
                        if (content == null)
                        {
                            var known = string.Join(", ", _websiteContent.Topics);
                            return (AnthropicContentBlock.OfToolResult(id,
                                _websiteContent.Topics.Contains(topic)
                                    ? "Page content is temporarily unavailable — do not guess the answer; offer to connect the customer with the team instead."
                                    : $"Unknown topic '{topic}'. Valid topics: {known}", true), false);
                        }
                        return (AnthropicContentBlock.OfToolResult(id, content), false);

                    case "escalate_to_human":
                        var reason = toolUse.Input.ValueKind == JsonValueKind.Object &&
                                     toolUse.Input.TryGetProperty("reason", out var r) &&
                                     r.ValueKind == JsonValueKind.String
                            ? r.GetString() ?? "No reason given"
                            : "No reason given";
                        await EscalateSessionAsync(session, reason);
                        return (AnthropicContentBlock.OfToolResult(id, "Escalation completed — the team has been notified."), true);

                    case "present_choices":
                        // Only reached when the call was invalid or arrived alongside an
                        // escalation (valid solo calls short-circuit in RunAgentLoopAsync).
                        return (AnthropicContentBlock.OfToolResult(id,
                            "present_choices was not displayed — either its arguments were invalid (needs 'question' plus 1-8 short string 'options') or it was called alongside another tool. Call it ALONE with valid arguments, or just ask in plain text.", true), false);

                    default:
                        return (AnthropicContentBlock.OfToolResult(id, $"Unknown tool: {toolUse.Name}", true), false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Tool {Tool} failed for session {SessionId}", toolUse.Name, session.Id);
                return (AnthropicContentBlock.OfToolResult(id, "Tool execution failed", true), false);
            }
        }

        private static AnthropicContentBlock ToRequestBlock(AnthropicResponseBlock block) => block.Type switch
        {
            "tool_use" => new AnthropicContentBlock { Type = "tool_use", Id = block.Id, Name = block.Name, Input = block.Input },
            _ => AnthropicContentBlock.OfText(block.Text ?? string.Empty)
        };

        private static List<AnthropicTool> BuildTools() => new()
        {
            new AnthropicTool
            {
                Name = "get_service_catalog",
                Description = "Returns all active service types with their services (bedrooms, bathrooms, square feet, cleaners, hours, ...) and extra services, including the IDs needed for calculate_price_estimate. Structure only — no prices. Call this before your first estimate in a conversation.",
                InputSchema = new { type = "object", properties = new { } }
            },
            new AnthropicTool
            {
                Name = "calculate_price_estimate",
                Description = "Calculates a price estimate for a concrete selection using the company's live pricing. The ONLY valid source for any price, tax, total or duration you state. Estimates exclude discounts/promo codes; final price is confirmed at checkout.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        serviceTypeId = new { type = "integer", description = "Service type id from get_service_catalog" },
                        services = new
                        {
                            type = "array",
                            description = "Selected services with quantities (e.g. bedrooms=2, bathrooms=1, sqft=850)",
                            items = new
                            {
                                type = "object",
                                properties = new
                                {
                                    serviceId = new { type = "integer" },
                                    quantity = new { type = "integer" }
                                },
                                required = new[] { "serviceId", "quantity" }
                            }
                        },
                        extraServices = new
                        {
                            type = "array",
                            description = "Selected extra services; quantity for countable extras, hours for hourly extras",
                            items = new
                            {
                                type = "object",
                                properties = new
                                {
                                    extraServiceId = new { type = "integer" },
                                    quantity = new { type = "integer" },
                                    hours = new { type = "number" }
                                },
                                required = new[] { "extraServiceId" }
                            }
                        }
                    },
                    required = new[] { "serviceTypeId", "services" }
                }
            },
            new AnthropicTool
            {
                Name = "get_page_content",
                Description = "Returns the CURRENT live text of a website page — the ONLY valid source for a service's details/inclusions, policies, and pricing/discount figures (never answer those from memory). "
                    + "Service pages (one per cleaning service; the topic key is the service's URL slug with underscores): "
                    + "residential_cleaning, house_cleaning, condo_cleaning, airbnb_cleaning, deep_cleaning, move_in_out_cleaning, kitchen_cleaning, bathroom_cleaning, office_cleaning, post_construction_cleaning, custom_cleaning, heavy_condition_cleaning, filthy_cleaning, post_renovation_cleaning, laundry_and_dishwashing. "
                    + "These specialized/custom-quote pages carry their own rate structure on the page (e.g. filthy_cleaning states $/hour per cleaner and minimums). "
                    + "Non-service topics: cleaning_checklist (precise room-by-room included/NOT-included table for Standard vs Deep, plus the Move-In/Out checklist — the authoritative source for what's included), pricing_and_discounts (residential starting prices plus CURRENT discount figures), cancellation_policy (cancellation/rescheduling fees). "
                    + "If you pass an unknown topic, the tool replies with the list of valid topics — pick from that list and retry.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        topic = new
                        {
                            type = "string",
                            description = "A registered topic key (a service slug like 'filthy_cleaning', or cleaning_checklist / pricing_and_discounts / cancellation_policy)"
                        }
                    },
                    required = new[] { "topic" }
                }
            },
            new AnthropicTool
            {
                Name = "present_choices",
                Description = "Shows the customer clickable choice buttons. REQUIRED whenever you ask the customer to pick between 2-8 named options — service type selection (options from get_service_catalog), Regular Cleaning vs Deep Cleaning, home vs business, one-time vs recurring frequency, or any other menu-style moment. Never ask such a question in plain prose instead. IMPORTANT: call this ALONE, as the only tool call in your response, after any data tools you needed; your short question goes in 'question' and is shown above the buttons. Do not also repeat the options in text.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        question = new { type = "string", description = "Short question shown above the buttons" },
                        options = new
                        {
                            type = "array",
                            items = new { type = "string" },
                            description = "1-8 short option labels; each is sent back verbatim as the customer's reply when clicked"
                        }
                    },
                    required = new[] { "question", "options" }
                }
            },
            new AnthropicTool
            {
                Name = "escalate_to_human",
                Description = "Hands the conversation over to a human team member. Use when you cannot answer confidently, the customer asks for a human, or the topic is a complaint/refund/existing-booking change. After calling this, tell the customer the team will reply here shortly.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        reason = new { type = "string", description = "Short reason for the hand-off" }
                    },
                    required = new[] { "reason" }
                }
            }
        };

        // ===== History → Messages API =====

        private async Task<List<AnthropicMessage>> BuildApiHistoryAsync(Guid sessionId)
        {
            var history = await _context.ChatAgentMessages
                .AsNoTracking()
                .Where(m => m.ChatSessionId == sessionId && m.Role != ChatMessageRole.System)
                .OrderByDescending(m => m.CreatedAt)
                .Take(MaxHistoryMessages)
                .ToListAsync();
            history.Reverse();

            // The API requires the first message to be a user turn — drop leading
            // assistant/human-agent rows left over from history truncation.
            while (history.Count > 0 && history[0].Role != ChatMessageRole.User)
                history.RemoveAt(0);

            var messages = new List<AnthropicMessage>();
            foreach (var m in history)
            {
                if (m.Role == ChatMessageRole.User)
                {
                    var blocks = new List<AnthropicContentBlock>();
                    var image = TryLoadImage(m.ImagePath);
                    if (image != null)
                        blocks.Add(AnthropicContentBlock.OfImage(image.Value.MediaType, image.Value.Base64));
                    blocks.Add(AnthropicContentBlock.OfText(
                        !string.IsNullOrWhiteSpace(m.Content) ? m.Content! :
                        image != null ? "(The customer sent this photo without text.)" : "(empty message)"));
                    messages.Add(new AnthropicMessage { Role = "user", Content = blocks });
                }
                else // Assistant or HumanAgent → assistant turns
                {
                    var prefix = m.Role == ChatMessageRole.HumanAgent ? "[Support team] " : string.Empty;
                    messages.Add(AnthropicMessage.Assistant(
                        AnthropicContentBlock.OfText(prefix + (m.Content ?? string.Empty))));
                }
            }
            return messages;
        }

        // ===== Escalation =====

        private async Task<string> ResolveVisitorNameAsync(ChatAgentSession session)
        {
            if (session.UserId != null)
            {
                var user = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == session.UserId);
                if (user != null)
                {
                    var name = $"{user.FirstName} {user.LastName}".Trim();
                    if (name.Length > 0) return name;
                }
            }
            return "Website Guest";
        }

        private async Task EscalateSessionAsync(ChatAgentSession session, string reason)
        {
            session.Status = ChatSessionStatus.EscalatedToHuman;
            _context.ChatAgentMessages.Add(new ChatAgentMessage
            {
                Id = Guid.NewGuid(),
                ChatSessionId = session.Id,
                Role = ChatMessageRole.System,
                Content = $"Escalated to human support. Reason: {reason}",
                CreatedAt = DateTime.UtcNow
            });
            await _context.SaveChangesAsync();

            var visitorName = await ResolveVisitorNameAsync(session);
            var transcript = await BuildTranscriptAsync(session.Id);

            // Telegram Forum Topic hand-off (no-op while TelegramBot:Enabled=false)
            if (_telegramBot.IsConfigured)
            {
                try
                {
                    var topicId = session.TelegramTopicId
                        ?? await _telegramBot.CreateTopicForVisitor($"🤖 {visitorName}");
                    if (topicId > 0)
                    {
                        session.TelegramTopicId = topicId;
                        await _context.SaveChangesAsync();

                        var emailLine = string.IsNullOrWhiteSpace(session.GuestEmail)
                            ? string.Empty
                            : $"\nEmail: {session.GuestEmail}";
                        await _telegramBot.SendTextToTopic(topicId, "Chat Agent",
                            $"Escalated by the AI assistant.\nReason: {reason}{emailLine}\n\nTranscript follows — replies typed here reach the visitor on the website.");
                        foreach (var chunk in Chunk(transcript, TelegramChunkSize))
                            await _telegramBot.SendTextToTopic(topicId, "Transcript", chunk);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to create/post Telegram topic for escalated chat session {SessionId}", session.Id);
                }
            }

            // Email notification (admin-toggleable via ChatAgentSettings)
            try
            {
                var settings = await _context.ChatAgentSettings.AsNoTracking().FirstOrDefaultAsync();
                if (settings?.EscalationEmailEnabled ?? true)
                {
                    var to = _configuration["Email:CompanyEmail"] ?? _configuration["Email:FromAddress"];
                    if (!string.IsNullOrEmpty(to))
                    {
                        var html = BuildEscalationEmailHtml(visitorName, reason, transcript, session);
                        await _emailService.SendContactFormEmailAsync(to,
                            $"Live chat escalated to human — {visitorName}", html);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send escalation email for chat session {SessionId}", session.Id);
            }
        }

        private async Task<string> BuildTranscriptAsync(Guid sessionId)
        {
            var messages = await _context.ChatAgentMessages
                .AsNoTracking()
                .Where(m => m.ChatSessionId == sessionId)
                .OrderBy(m => m.CreatedAt)
                .Take(100)
                .ToListAsync();

            var siteUrl = (_configuration["Frontend:Url"] ?? "https://dreamcleaningnyc.com").TrimEnd('/');
            var sb = new StringBuilder();
            foreach (var m in messages)
            {
                var speaker = m.Role switch
                {
                    ChatMessageRole.User => "Customer",
                    ChatMessageRole.Assistant => "AI",
                    ChatMessageRole.HumanAgent => "Support",
                    _ => "System"
                };
                sb.Append('[').Append(m.CreatedAt.ToString("MMM dd HH:mm")).Append(" UTC] ")
                  .Append(speaker).Append(": ");
                if (!string.IsNullOrWhiteSpace(m.Content))
                    sb.Append(m.Content);
                if (m.ImagePath != null)
                    sb.Append(" [photo: ").Append(siteUrl).Append(m.ImagePath).Append(']');
                sb.AppendLine();
            }
            return sb.ToString();
        }

        private static string BuildEscalationEmailHtml(string visitorName, string reason, string transcript, ChatAgentSession session)
        {
            string Encode(string s) => System.Net.WebUtility.HtmlEncode(s);
            var emailLine = string.IsNullOrWhiteSpace(session.GuestEmail)
                ? string.Empty
                : $"<p><strong>Email:</strong> {Encode(session.GuestEmail)}</p>";
            return $@"
                <h2>💬 Live chat escalated to human</h2>
                <p><strong>Visitor:</strong> {Encode(visitorName)}</p>
                <p><strong>Reason:</strong> {Encode(reason)}</p>
                {emailLine}
                <p><strong>Session:</strong> {session.Id}</p>
                <p><strong>Telegram topic:</strong> {(session.TelegramTopicId?.ToString() ?? "not created (Telegram disabled)")}</p>
                <h3>Transcript</h3>
                <pre style='background:#f5f5f5;padding:12px;border-radius:8px;white-space:pre-wrap;'>{Encode(transcript)}</pre>";
        }

        // ===== Relay to Telegram after escalation =====

        private async Task RelayToTelegramAsync(ChatAgentSession session, string text, string? imagePath)
        {
            if (!_telegramBot.IsConfigured || session.TelegramTopicId is not > 0)
                return; // message is persisted; team catches up when Telegram is enabled

            try
            {
                var visitorName = await ResolveVisitorNameAsync(session);
                if (text.Length > 0)
                    await _telegramBot.SendTextToTopic(session.TelegramTopicId.Value, visitorName, text);

                var image = TryLoadImageBytes(imagePath);
                if (image != null)
                    await _telegramBot.SendPhotoToTopic(session.TelegramTopicId.Value, visitorName,
                        image.Value.Bytes, image.Value.MediaType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to relay chat message to Telegram topic {TopicId}", session.TelegramTopicId);
            }
        }

        // ===== Image helpers =====

        /// <summary>Validates a client-supplied relative image path (must match the
        /// upload endpoint's output exactly — no traversal, no foreign files).</summary>
        private string? NormalizeImagePath(string? imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath))
                return null;
            if (!ImagePathRegex.IsMatch(imagePath))
                throw new ArgumentException("Invalid image path");
            if (ResolvePhysicalPath(imagePath) == null)
                throw new ArgumentException("Image not found — upload it first via /api/chat/upload-image");
            return imagePath;
        }

        private string? ResolvePhysicalPath(string relativeImagePath)
        {
            var root = _configuration["FileUpload:Path"];
            if (string.IsNullOrEmpty(root)) return null;
            var fileName = Path.GetFileName(relativeImagePath);
            var fullPath = Path.Combine(Path.GetFullPath(root), "chat-photos", fileName);
            return File.Exists(fullPath) ? fullPath : null;
        }

        private (string MediaType, string Base64)? TryLoadImage(string? relativeImagePath)
        {
            var raw = TryLoadImageBytes(relativeImagePath);
            return raw == null ? null : (raw.Value.MediaType, Convert.ToBase64String(raw.Value.Bytes));
        }

        private (string MediaType, byte[] Bytes)? TryLoadImageBytes(string? relativeImagePath)
        {
            if (relativeImagePath == null) return null;
            try
            {
                var fullPath = ResolvePhysicalPath(relativeImagePath);
                if (fullPath == null) return null; // expired/purged — skip silently
                var mediaType = Path.GetExtension(relativeImagePath).ToLowerInvariant() switch
                {
                    ".png" => "image/png",
                    ".webp" => "image/webp",
                    _ => "image/jpeg"
                };
                return (mediaType, File.ReadAllBytes(fullPath));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read chat image {Path}", relativeImagePath);
                return null;
            }
        }

        // ===== Misc =====

        private static string? Truncate(string? s, int max) =>
            string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s[..max]);

        /// <summary>Best-effort sanity check for the guest email from the widget's optional
        /// start-of-chat field: trims, requires an '@', and enforces the column's 255-char
        /// cap. Returns null when it doesn't look like an email — nothing sends to it, so
        /// this is deliberately lenient, not strict validation.</summary>
        private static string? NormalizeGuestEmail(string? raw)
        {
            var email = raw?.Trim();
            if (string.IsNullOrEmpty(email) || !email.Contains('@') || email.Length > 255)
                return null;
            return email;
        }

        /// <summary>Detects and strips the admin QA-mode prefix. Accepts "QA:" (any
        /// following char) and "/qa" only when followed by a space, colon, or end — so
        /// "/qaxyz" is NOT a trigger. Case-insensitive; returns the stripped, trimmed
        /// remainder via <paramref name="stripped"/> when a prefix was present.</summary>
        private static bool TryStripQaPrefix(string text, out string stripped)
        {
            if (text.StartsWith("qa:", StringComparison.OrdinalIgnoreCase))
            {
                stripped = text[3..].Trim();
                return true;
            }
            if (text.Equals("/qa", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("/qa ", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("/qa:", StringComparison.OrdinalIgnoreCase))
            {
                stripped = text[3..].TrimStart(' ', ':', '\t').Trim();
                return true;
            }
            stripped = text;
            return false;
        }

        private static IEnumerable<string> Chunk(string text, int size)
        {
            for (var i = 0; i < text.Length; i += size)
                yield return text.Substring(i, Math.Min(size, text.Length - i));
        }
    }
}
