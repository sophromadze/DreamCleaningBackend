using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;

namespace DreamCleaningBackend.Controllers
{
    /// <summary>
    /// Public chat endpoints (no auth, rate-limited): pricing catalog/estimate for
    /// chat agents, plus the AI chat agent itself (message orchestration, temporary
    /// image upload, and session history polling).
    ///
    /// Catalog returns structure WITHOUT prices; every number comes from the estimate
    /// path, which resolves prices fresh from the DB through the shared
    /// OrderPricingCalculator (logic lives in ChatCatalogService — also used
    /// in-process by the AI agent's tools). Estimates never apply discounts.
    /// </summary>
    [Route("api/chat")]
    [ApiController]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicy)]
    public class ChatController : ControllerBase
    {
        /// <summary>Per-IP rate-limit policy for these public endpoints — registered in Program.cs.</summary>
        public const string RateLimitPolicy = "public-chat";

        private const long MaxImageBytes = 5 * 1024 * 1024; // 5 MB

        private readonly ApplicationDbContext _context;
        private readonly IChatCatalogService _catalogService;
        private readonly IChatAgentService _chatAgentService;
        private readonly IConfiguration _configuration;
        private readonly ILogger<ChatController> _logger;

        public ChatController(
            ApplicationDbContext context,
            IChatCatalogService catalogService,
            IChatAgentService chatAgentService,
            IConfiguration configuration,
            ILogger<ChatController> logger)
        {
            _context = context;
            _catalogService = catalogService;
            _chatAgentService = chatAgentService;
            _configuration = configuration;
            _logger = logger;
        }

        // ===== Pricing (structure-only catalog + strict estimate) =====

        /// <summary>
        /// Active service types with their active services and extra services —
        /// structure only, no cost/price fields (see ChatEstimateDtos.cs).
        /// Custom (admin-only) service types are excluded.
        /// </summary>
        [HttpGet("service-catalog")]
        public async Task<ActionResult<List<ChatServiceTypeDto>>> GetServiceCatalog()
        {
            return Ok(await _catalogService.GetServiceCatalogAsync());
        }

        /// <summary>
        /// Price an ad-hoc selection. IDs are validated strictly (unknown / inactive /
        /// wrong-service-type rows are a 400, never a silent skip), then the selection
        /// flows through the exact same path a real booking uses (incl. the
        /// bedrooms→sqft floor clamp). See ChatCatalogService.
        /// </summary>
        [HttpPost("estimate")]
        public async Task<ActionResult<ChatEstimateResponseDto>> Estimate(ChatEstimateRequestDto dto)
        {
            var (result, error) = await _catalogService.EstimateAsync(dto);
            if (error != null)
                return BadRequest(new { message = error });
            return Ok(result);
        }

        // ===== AI chat agent =====

        /// <summary>
        /// Whether the caller may see and use the chat widget, per the admin-controlled
        /// ChatAgentSettings.VisibilityMode. AllowAnonymous, but the JWT (header in dev,
        /// access_token cookie in prod) is still validated by the auth middleware when
        /// present, so AdminOnly resolves the role SERVER-side — a customer can never
        /// see the widget in AdminOnly mode by tweaking client state.
        /// </summary>
        [HttpGet("widget-visibility")]
        public async Task<ActionResult<ChatWidgetVisibilityDto>> GetWidgetVisibility()
        {
            return Ok(new ChatWidgetVisibilityDto { Visible = await IsChatAccessibleAsync() });
        }

        /// <summary>
        /// One conversational turn: persists the visitor's message, runs the AI
        /// (catalog/estimate/escalation tools) or — once escalated — relays the message
        /// to the team's Telegram topic, and returns the reply.
        /// </summary>
        [HttpPost("message")]
        public async Task<ActionResult<ChatMessageResponseDto>> SendMessage(ChatMessageRequestDto dto)
        {
            if (!await IsChatAccessibleAsync())
                return StatusCode(403, new { message = "Chat is not available" });

            try
            {
                // Role comes from the validated JWT/cookie (same source as the AdminOnly
                // gate) — a customer/guest can never trigger admin mode.
                var isAdmin = User.IsInRole("SuperAdmin") || User.IsInRole("Admin");
                var result = await _chatAgentService.HandleMessageAsync(dto, GetOptionalUserId(), isAdmin);
                return Ok(result);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        /// <summary>
        /// Temporary chat-photo upload (jpg/png/webp, max 5 MB — validated by magic
        /// bytes, not the client's file name). Stored under {FileUpload:Path}/chat-photos
        /// and served from /chat-photos/{name} via the existing uploads static mapping.
        /// Auto-purged 30 days after being attached to a message.
        /// </summary>
        [HttpPost("upload-image")]
        [RequestSizeLimit(MaxImageBytes + 512 * 1024)] // multipart overhead headroom
        public async Task<ActionResult<ChatImageUploadResponseDto>> UploadImage(IFormFile? file)
        {
            if (!await IsChatAccessibleAsync())
                return StatusCode(403, new { message = "Chat is not available" });

            if (file == null || file.Length == 0)
                return BadRequest(new { message = "No file uploaded" });
            if (file.Length > MaxImageBytes)
                return BadRequest(new { message = "Image too large — maximum size is 5 MB" });

            var uploadRoot = _configuration["FileUpload:Path"];
            if (string.IsNullOrEmpty(uploadRoot))
            {
                _logger.LogError("FileUpload:Path is not configured — cannot store chat images");
                return StatusCode(500, new { message = "Upload storage is not configured" });
            }

            // Sniff the real type from magic bytes — the client's name/content-type is untrusted.
            byte[] header = new byte[12];
            await using (var probe = file.OpenReadStream())
            {
                var read = await probe.ReadAsync(header.AsMemory(0, 12));
                if (read < 12)
                    return BadRequest(new { message = "Invalid image file" });
            }

            string? extension = null;
            if (header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
                extension = "jpg";
            else if (header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
                extension = "png";
            else if (header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F'
                     && header[8] == (byte)'W' && header[9] == (byte)'E' && header[10] == (byte)'B' && header[11] == (byte)'P')
                extension = "webp";

            if (extension == null)
                return BadRequest(new { message = "Only JPG, PNG and WEBP images are allowed" });

            var directory = Path.Combine(Path.GetFullPath(uploadRoot), "chat-photos");
            Directory.CreateDirectory(directory);

            var fileName = $"{Guid.NewGuid():N}.{extension}";
            var fullPath = Path.Combine(directory, fileName);
            await using (var target = System.IO.File.Create(fullPath))
            await using (var source = file.OpenReadStream())
            {
                await source.CopyToAsync(target);
            }

            return Ok(new ChatImageUploadResponseDto
            {
                ImagePath = $"/chat-photos/{fileName}",
                ExpiresAt = DateTime.UtcNow.AddDays(30)
            });
        }

        /// <summary>
        /// Session history for the widget to poll — the channel through which human
        /// (Telegram) replies reach the visitor after escalation. Internal System
        /// audit rows are excluded. Optional ?after= returns only newer messages.
        /// </summary>
        [HttpGet("session/{sessionId:guid}/messages")]
        public async Task<ActionResult<ChatSessionHistoryDto>> GetSessionMessages(Guid sessionId, [FromQuery] DateTime? after)
        {
            if (!await IsChatAccessibleAsync())
                return StatusCode(403, new { message = "Chat is not available" });

            var session = await _context.ChatAgentSessions
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == sessionId);
            if (session == null)
                return NotFound(new { message = "Session not found" });

            var query = _context.ChatAgentMessages
                .AsNoTracking()
                .Where(m => m.ChatSessionId == sessionId && m.Role != ChatMessageRole.System);
            if (after.HasValue)
                query = query.Where(m => m.CreatedAt > after.Value);

            var messages = await query
                .OrderBy(m => m.CreatedAt)
                .Take(200)
                .ToListAsync();

            var agentNames = await ResolveAgentDisplayNamesAsync(messages);

            return Ok(new ChatSessionHistoryDto
            {
                SessionId = session.Id,
                Status = session.Status switch
                {
                    ChatSessionStatus.EscalatedToHuman => "escalatedToHuman",
                    ChatSessionStatus.Resolved => "resolved",
                    _ => "aiHandling"
                },
                Messages = messages.Select(m => new ChatHistoryMessageDto
                {
                    Id = m.Id,
                    Role = m.Role switch
                    {
                        ChatMessageRole.Assistant => "assistant",
                        ChatMessageRole.HumanAgent => "humanAgent",
                        _ => "user"
                    },
                    Content = m.Content,
                    ImagePath = m.ImagePath,
                    AgentName = m.SenderTelegramUserId != null
                        ? agentNames.GetValueOrDefault(m.SenderTelegramUserId.Value)
                        : null,
                    CreatedAt = m.CreatedAt
                }).ToList()
            });
        }

        /// <summary>
        /// Customer "End chat": marks the session Resolved. Guid-session trust model,
        /// same as the other public chat endpoints. The widget shows the ended state
        /// and clears its stored session; the server row is kept for the admin viewer.
        /// </summary>
        [HttpPost("session/{sessionId:guid}/resolve")]
        public async Task<IActionResult> ResolveSession(Guid sessionId)
        {
            if (!await IsChatAccessibleAsync())
                return StatusCode(403, new { message = "Chat is not available" });

            var resolved = await _chatAgentService.ResolveSessionAsync(sessionId, "ended by the customer");
            if (!resolved)
                return NotFound(new { message = "Session not found" });
            return Ok(new { status = "resolved" });
        }

        /// <summary>
        /// Admin-chosen display names for the human-agent senders in a message batch
        /// (TelegramAgentDisplayNames, one dictionary query). Names resolve at read time
        /// so a mapping added later retroactively names past replies; unmapped senders
        /// stay null and the UI falls back to "Team". The numeric ids never leave the API.
        /// </summary>
        private async Task<Dictionary<long, string>> ResolveAgentDisplayNamesAsync(
            IEnumerable<ChatAgentMessage> messages)
        {
            var senderIds = messages
                .Where(m => m.SenderTelegramUserId != null)
                .Select(m => m.SenderTelegramUserId!.Value)
                .Distinct()
                .ToList();
            if (senderIds.Count == 0) return new Dictionary<long, string>();

            return await _context.TelegramAgentDisplayNames
                .AsNoTracking()
                .Where(d => senderIds.Contains(d.TelegramUserId))
                .ToDictionaryAsync(d => d.TelegramUserId, d => d.DisplayName);
        }

        /// <summary>
        /// Server-side visibility gate shared by the widget check and the chat
        /// endpoints themselves (hiding the button alone would leave the
        /// Anthropic-backed API publicly callable). No settings row → Disabled.
        /// </summary>
        private async Task<bool> IsChatAccessibleAsync()
        {
            var settings = await _context.ChatAgentSettings.AsNoTracking().FirstOrDefaultAsync();
            return (settings?.VisibilityMode ?? ChatWidgetVisibility.Disabled) switch
            {
                ChatWidgetVisibility.Public => true,
                ChatWidgetVisibility.AdminOnly => User.IsInRole("SuperAdmin") || User.IsInRole("Admin"),
                _ => false
            };
        }

        /// <summary>JWT is optional on this anonymous controller — associate the session
        /// with the account when a logged-in user chats.</summary>
        private int? GetOptionalUserId()
        {
            if (User.Identity?.IsAuthenticated != true)
                return null;
            var claim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return int.TryParse(claim, out var id) ? id : null;
        }
    }
}
