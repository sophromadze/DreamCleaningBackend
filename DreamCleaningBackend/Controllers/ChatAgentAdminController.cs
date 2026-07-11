using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Controllers
{
    /// <summary>
    /// Runtime settings for the AI chat agent (MaintenanceModeController pattern —
    /// standalone controller, single-row settings table, toggle without redeploy).
    /// Currently: the "email on every escalation" switch.
    /// </summary>
    [Route("api/admin/chat-agent")]
    [ApiController]
    [Authorize(Roles = "SuperAdmin,Admin")]
    public class ChatAgentAdminController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly Services.IChatAgentService _chatAgentService;

        public ChatAgentAdminController(ApplicationDbContext context, Services.IChatAgentService chatAgentService)
        {
            _context = context;
            _chatAgentService = chatAgentService;
        }

        [HttpGet("settings")]
        public async Task<ActionResult<ChatAgentSettingsDto>> GetSettings()
        {
            var settings = await GetOrCreateSettingsAsync();
            return Ok(ToDto(settings));
        }

        [HttpPost("settings/toggle-escalation-email")]
        public async Task<ActionResult<ChatAgentSettingsDto>> ToggleEscalationEmail()
        {
            var settings = await GetOrCreateSettingsAsync();
            settings.EscalationEmailEnabled = !settings.EscalationEmailEnabled;
            settings.UpdatedAt = DateTime.UtcNow;
            settings.UpdatedByEmail = User.FindFirst(ClaimTypes.Email)?.Value;
            await _context.SaveChangesAsync();
            return Ok(ToDto(settings));
        }

        /// <summary>Sets who can see and use the chat widget: Disabled | AdminOnly | Public.
        /// Enforced server-side on the public chat endpoints, not just the button.</summary>
        [HttpPost("settings/visibility")]
        public async Task<ActionResult<ChatAgentSettingsDto>> SetVisibility([FromBody] ChatAgentVisibilityDto dto)
        {
            if (!Enum.TryParse<ChatWidgetVisibility>(dto.Mode, ignoreCase: true, out var mode))
                return BadRequest(new { message = "Mode must be Disabled, AdminOnly or Public" });

            var settings = await GetOrCreateSettingsAsync();
            settings.VisibilityMode = mode;
            settings.UpdatedAt = DateTime.UtcNow;
            settings.UpdatedByEmail = User.FindFirst(ClaimTypes.Email)?.Value;
            await _context.SaveChangesAsync();
            return Ok(ToDto(settings));
        }

        // ===== Chat history viewer =====

        /// <summary>Paginated session list, newest activity first. Follows the admin
        /// pagination convention (AdminCallsController): page/pageSize + totals.
        /// Filters: status name (AiHandling/EscalatedToHuman/Resolved), from/to dates
        /// (inclusive, applied to session start).</summary>
        [HttpGet("sessions")]
        public async Task<ActionResult<ChatSessionListResponseDto>> GetSessions(
            [FromQuery] string? status,
            [FromQuery] DateTime? from,
            [FromQuery] DateTime? to,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20)
        {
            if (page < 1) page = 1;
            if (pageSize < 1) pageSize = 20;
            if (pageSize > 100) pageSize = 100;

            var query = _context.ChatAgentSessions.AsNoTracking();

            if (!string.IsNullOrWhiteSpace(status) &&
                Enum.TryParse<ChatSessionStatus>(status, ignoreCase: true, out var parsedStatus))
                query = query.Where(s => s.Status == parsedStatus);
            if (from.HasValue)
                query = query.Where(s => s.CreatedAt >= from.Value.Date);
            if (to.HasValue)
                query = query.Where(s => s.CreatedAt < to.Value.Date.AddDays(1)); // inclusive end date

            var totalCount = await query.CountAsync();

            var raw = await query
                .OrderByDescending(s => s.LastMessageAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(s => new
                {
                    s.Id,
                    s.UserId,
                    UserEmail = s.User != null ? s.User.Email : null,
                    s.GuestIdentifier,
                    s.GuestEmail,
                    s.Status,
                    s.TelegramTopicId,
                    s.CreatedAt,
                    s.LastMessageAt,
                    MessageCount = s.Messages.Count
                })
                .ToListAsync();

            return Ok(new ChatSessionListResponseDto
            {
                Items = raw.Select(s => new ChatSessionListItemDto
                {
                    Id = s.Id,
                    UserId = s.UserId,
                    UserEmail = s.UserEmail,
                    GuestIdentifier = s.GuestIdentifier,
                    GuestEmail = s.GuestEmail,
                    Status = s.Status.ToString(),
                    TelegramTopicId = s.TelegramTopicId,
                    CreatedAt = s.CreatedAt,
                    LastMessageAt = s.LastMessageAt,
                    MessageCount = s.MessageCount
                }).ToList(),
                TotalCount = totalCount,
                Page = page,
                PageSize = pageSize,
                TotalPages = (int)Math.Ceiling(totalCount / (double)pageSize)
            });
        }

        /// <summary>Full ordered transcript for one session. Unlike the public endpoint,
        /// System audit rows (e.g. escalation reasons) ARE included — admins reviewing a
        /// session want them. ImagePath is the same site-relative URL the widget uses.</summary>
        [HttpGet("sessions/{sessionId:guid}/messages")]
        public async Task<ActionResult<ChatAdminTranscriptDto>> GetSessionTranscript(Guid sessionId)
        {
            var session = await _context.ChatAgentSessions
                .AsNoTracking()
                .Select(s => new
                {
                    s.Id,
                    s.Status,
                    UserEmail = s.User != null ? s.User.Email : null,
                    s.GuestIdentifier,
                    s.GuestEmail,
                    s.CreatedAt
                })
                .FirstOrDefaultAsync(s => s.Id == sessionId);
            if (session == null)
                return NotFound(new { message = "Session not found" });

            var messages = await _context.ChatAgentMessages
                .AsNoTracking()
                .Where(m => m.ChatSessionId == sessionId)
                .OrderBy(m => m.CreatedAt)
                .Take(500)
                .ToListAsync();

            // Agent names resolve at read time from the mapping table, so a mapping
            // added later retroactively names past replies. Unmapped → null → "Team".
            var senderIds = messages
                .Where(m => m.SenderTelegramUserId != null)
                .Select(m => m.SenderTelegramUserId!.Value)
                .Distinct()
                .ToList();
            var agentNames = senderIds.Count == 0
                ? new Dictionary<long, string>()
                : await _context.TelegramAgentDisplayNames
                    .AsNoTracking()
                    .Where(d => senderIds.Contains(d.TelegramUserId))
                    .ToDictionaryAsync(d => d.TelegramUserId, d => d.DisplayName);

            return Ok(new ChatAdminTranscriptDto
            {
                SessionId = session.Id,
                Status = session.Status.ToString(),
                UserEmail = session.UserEmail,
                GuestIdentifier = session.GuestIdentifier,
                GuestEmail = session.GuestEmail,
                CreatedAt = session.CreatedAt,
                Messages = messages.Select(m => new ChatAdminTranscriptMessageDto
                {
                    Id = m.Id,
                    Role = m.Role switch
                    {
                        ChatMessageRole.Assistant => "assistant",
                        ChatMessageRole.HumanAgent => "humanAgent",
                        ChatMessageRole.System => "system",
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

        /// <summary>Admin "Mark Resolved" — same effect as the customer's End chat
        /// (audit row + Telegram courtesy note); the widget picks the status up on
        /// its next poll and shows the ended state.</summary>
        [HttpPost("sessions/{sessionId:guid}/resolve")]
        public async Task<IActionResult> ResolveSession(Guid sessionId)
        {
            var adminEmail = User.FindFirst(ClaimTypes.Email)?.Value ?? "admin";
            var resolved = await _chatAgentService.ResolveSessionAsync(sessionId, $"marked resolved by {adminEmail}");
            if (!resolved)
                return NotFound(new { message = "Session not found" });
            return Ok(new { status = "Resolved" });
        }

        /// <summary>Hard-deletes a session and all its messages + chat-photo files.
        /// SuperAdmin ONLY — the method-level attribute AND-combines with the class-level
        /// "SuperAdmin,Admin", so an Admin is rejected. More destructive than resolve/view.</summary>
        [HttpDelete("sessions/{sessionId:guid}")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<IActionResult> DeleteSession(Guid sessionId)
        {
            var deleted = await _chatAgentService.DeleteSessionAsync(sessionId);
            if (!deleted)
                return NotFound(new { message = "Session not found" });
            return Ok(new { status = "deleted" });
        }

        // ===== Telegram agent display names =====
        // Admin-chosen, visitor-facing names for Telegram accounts replying in chat
        // topics — deliberately never auto-populated from Telegram profile names.
        // Messages store only the sender's numeric id; names resolve at read time, so
        // an upsert here retroactively (re)names that person's past replies everywhere.

        /// <summary>Current mappings plus Telegram senders seen in chat replies that
        /// have no mapping yet (so admins can name them without hunting for ids).</summary>
        [HttpGet("agent-names")]
        public async Task<ActionResult<TelegramAgentDisplayNamesResponseDto>> GetAgentDisplayNames()
        {
            var mappings = await _context.TelegramAgentDisplayNames
                .AsNoTracking()
                .OrderBy(d => d.DisplayName)
                .Select(d => new TelegramAgentDisplayNameDto
                {
                    TelegramUserId = d.TelegramUserId,
                    DisplayName = d.DisplayName,
                    CreatedAt = d.CreatedAt,
                    UpdatedAt = d.UpdatedAt
                })
                .ToListAsync();

            var mappedIds = mappings.Select(m => m.TelegramUserId).ToHashSet();

            var unmapped = (await _context.ChatAgentMessages
                    .AsNoTracking()
                    .Where(m => m.SenderTelegramUserId != null)
                    .GroupBy(m => m.SenderTelegramUserId!.Value)
                    .Select(g => new UnmappedTelegramSenderDto
                    {
                        TelegramUserId = g.Key,
                        LastSeenAt = g.Max(m => m.CreatedAt),
                        MessageCount = g.Count()
                    })
                    .ToListAsync())
                .Where(s => !mappedIds.Contains(s.TelegramUserId))
                .OrderByDescending(s => s.LastSeenAt)
                .ToList();

            return Ok(new TelegramAgentDisplayNamesResponseDto
            {
                Mappings = mappings,
                UnmappedSenders = unmapped
            });
        }

        /// <summary>Creates or updates the display name for a Telegram user id (upsert —
        /// the UI's "add new", "name this sender" and inline edit all land here).</summary>
        [HttpPut("agent-names/{telegramUserId:long}")]
        public async Task<ActionResult<TelegramAgentDisplayNameDto>> UpsertAgentDisplayName(
            long telegramUserId, [FromBody] UpsertTelegramAgentDisplayNameDto dto)
        {
            if (telegramUserId <= 0)
                return BadRequest(new { message = "Telegram user id must be a positive number" });

            var displayName = dto.DisplayName?.Trim();
            if (string.IsNullOrEmpty(displayName))
                return BadRequest(new { message = "Display name is required" });
            if (displayName.Length > 50)
                return BadRequest(new { message = "Display name must be 50 characters or fewer" });

            var now = DateTime.UtcNow;
            var entry = await _context.TelegramAgentDisplayNames
                .FirstOrDefaultAsync(d => d.TelegramUserId == telegramUserId);
            if (entry == null)
            {
                entry = new TelegramAgentDisplayName
                {
                    TelegramUserId = telegramUserId,
                    DisplayName = displayName,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                _context.TelegramAgentDisplayNames.Add(entry);
            }
            else
            {
                entry.DisplayName = displayName;
                entry.UpdatedAt = now;
            }
            await _context.SaveChangesAsync();

            return Ok(new TelegramAgentDisplayNameDto
            {
                TelegramUserId = entry.TelegramUserId,
                DisplayName = entry.DisplayName,
                CreatedAt = entry.CreatedAt,
                UpdatedAt = entry.UpdatedAt
            });
        }

        /// <summary>Removes a mapping — that sender's replies (past and future) fall
        /// back to "Team" until a new name is set.</summary>
        [HttpDelete("agent-names/{telegramUserId:long}")]
        public async Task<IActionResult> DeleteAgentDisplayName(long telegramUserId)
        {
            var entry = await _context.TelegramAgentDisplayNames
                .FirstOrDefaultAsync(d => d.TelegramUserId == telegramUserId);
            if (entry == null)
                return NotFound(new { message = "No mapping for that Telegram user id" });

            _context.TelegramAgentDisplayNames.Remove(entry);
            await _context.SaveChangesAsync();
            return Ok(new { status = "deleted" });
        }

        private async Task<ChatAgentSettings> GetOrCreateSettingsAsync()
        {
            var settings = await _context.ChatAgentSettings.FirstOrDefaultAsync();
            if (settings == null)
            {
                settings = new ChatAgentSettings { EscalationEmailEnabled = true };
                _context.ChatAgentSettings.Add(settings);
                await _context.SaveChangesAsync();
            }
            return settings;
        }

        private static ChatAgentSettingsDto ToDto(ChatAgentSettings s) => new()
        {
            EscalationEmailEnabled = s.EscalationEmailEnabled,
            VisibilityMode = s.VisibilityMode.ToString(),
            UpdatedAt = s.UpdatedAt,
            UpdatedByEmail = s.UpdatedByEmail
        };
    }
}
