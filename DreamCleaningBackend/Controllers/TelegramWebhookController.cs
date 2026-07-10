using DreamCleaningBackend.Data;
using DreamCleaningBackend.Hubs;
using DreamCleaningBackend.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace DreamCleaningBackend.Controllers;

/// <summary>
/// Inbound Telegram updates. Two routing targets, checked in order:
///  1. DB-persisted AI chat-agent sessions (ChatAgentSessions.TelegramTopicId) —
///     agent replies are persisted as HumanAgent messages and reach the visitor
///     via the widget's history polling endpoint.
///  2. The legacy in-memory live-chat sessions (LiveChatSessionManager) — replies
///     are pushed over the LiveChatHub SignalR connection.
/// Gated by TelegramBot:Enabled (via TelegramBotService.IsConfigured): while the
/// integration is off, always respond 410 Gone so Telegram backs off.
/// </summary>
[ApiController]
[Route("api/telegram")]
public class TelegramWebhookController : ControllerBase
{
    private readonly LiveChatSessionManager _sessionManager;
    private readonly TelegramBotService _telegramBot;
    private readonly IHubContext<LiveChatHub> _hubContext;
    private readonly ApplicationDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TelegramWebhookController> _logger;

    public TelegramWebhookController(
        LiveChatSessionManager sessionManager,
        TelegramBotService telegramBot,
        IHubContext<LiveChatHub> hubContext,
        ApplicationDbContext context,
        IConfiguration configuration,
        ILogger<TelegramWebhookController> logger)
    {
        _sessionManager = sessionManager;
        _telegramBot = telegramBot;
        _hubContext = hubContext;
        _context = context;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpPost("webhook")]
    public async Task<IActionResult> HandleWebhook()
    {
        if (!_telegramBot.IsConfigured)
        {
            _logger.LogInformation("Telegram webhook hit while integration is disabled — returning 410 Gone.");
            return StatusCode(StatusCodes.Status410Gone, new { message = "Telegram integration is disabled." });
        }

        // Telegram.Bot v22 uses System.Text.Json with custom converters (snake_case,
        // Unix timestamps, ChatId, etc.) exposed via JsonBotAPI.Options.
        // We deserialize manually so ASP.NET Core's default STJ settings don't interfere.
        using var reader = new StreamReader(Request.Body);
        var json = await reader.ReadToEndAsync();

        Update? update;
        try
        {
            update = System.Text.Json.JsonSerializer.Deserialize<Update>(
                json, Telegram.Bot.JsonBotAPI.Options);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize Telegram webhook payload");
            return Ok(); // Return 200 so Telegram stops retrying
        }

        if (update?.Message == null) return Ok();

        var message = update.Message;

        // Only process messages inside a Forum Topic (have message_thread_id)
        if (message.MessageThreadId == null || message.MessageThreadId == 0)
            return Ok();

        // CRITICAL: Ignore messages sent by the bot itself (avoid echo loops)
        var botUserId = await _telegramBot.GetBotUserId();
        if (message.From?.Id == botUserId)
            return Ok();

        var topicThreadId = message.MessageThreadId.Value;

        // 1. AI chat-agent sessions (DB-persisted, escalated conversations)
        var chatAgentSession = await _context.ChatAgentSessions
            .FirstOrDefaultAsync(s => s.TelegramTopicId == topicThreadId);
        if (chatAgentSession != null)
        {
            await PersistAgentReplyAsync(chatAgentSession, message);
            return Ok();
        }

        // 2. Legacy in-memory live-chat sessions (SignalR push)
        var session = _sessionManager.GetSessionByTopicThreadId(topicThreadId);
        if (session == null)
        {
            _logger.LogWarning("Telegram webhook: No session for topic {ThreadId}", topicThreadId);
            return Ok();
        }

        if (!session.IsActive)
        {
            _logger.LogWarning("Telegram webhook: Session for topic {ThreadId} is not active", topicThreadId);
            return Ok();
        }

        // Text message from admin
        if (!string.IsNullOrEmpty(message.Text))
        {
            await _hubContext.Clients.Client(session.ConnectionId).SendAsync("ReceiveMessage", new
            {
                id = Guid.NewGuid().ToString(),
                content = message.Text,
                isFromVisitor = false,
                timestamp = DateTime.UtcNow
            });
        }

        // Photo from admin
        if (message.Photo != null && message.Photo.Length > 0)
        {
            var photo = message.Photo.OrderByDescending(p => p.FileSize).First();
            var imageData = await _telegramBot.DownloadFile(photo.FileId);

            if (imageData != null)
            {
                await _hubContext.Clients.Client(session.ConnectionId).SendAsync("ReceiveMessage", new
                {
                    id = Guid.NewGuid().ToString(),
                    imageBase64 = Convert.ToBase64String(imageData),
                    imageMimeType = "image/jpeg",
                    content = message.Caption ?? "",
                    isFromVisitor = false,
                    timestamp = DateTime.UtcNow
                });
            }
        }

        // Document (non-compressed image, e.g. PNG sent as file)
        if (message.Document != null && message.Document.MimeType?.StartsWith("image/") == true)
        {
            var imageData = await _telegramBot.DownloadFile(message.Document.FileId);

            if (imageData != null)
            {
                await _hubContext.Clients.Client(session.ConnectionId).SendAsync("ReceiveMessage", new
                {
                    id = Guid.NewGuid().ToString(),
                    imageBase64 = Convert.ToBase64String(imageData),
                    imageMimeType = message.Document.MimeType,
                    content = message.Caption ?? "",
                    isFromVisitor = false,
                    timestamp = DateTime.UtcNow
                });
            }
        }

        return Ok();
    }

    /// <summary>
    /// Persists a human agent's Telegram reply onto the DB chat session (text and/or
    /// photo — photos are saved under chat-photos with the standard 30-day expiry).
    /// The visitor receives it through GET /api/chat/session/{id}/messages polling.
    /// </summary>
    private async Task PersistAgentReplyAsync(Models.ChatAgentSession chatSession, Message message)
    {
        var now = DateTime.UtcNow;
        string? imagePath = null;

        // Photo (or an image sent as a document)
        var fileId = message.Photo is { Length: > 0 }
            ? message.Photo.OrderByDescending(p => p.FileSize).First().FileId
            : message.Document?.MimeType?.StartsWith("image/") == true ? message.Document.FileId : null;

        if (fileId != null)
        {
            try
            {
                var imageData = await _telegramBot.DownloadFile(fileId);
                var uploadRoot = _configuration["FileUpload:Path"];
                if (imageData != null && !string.IsNullOrEmpty(uploadRoot))
                {
                    var directory = Path.Combine(Path.GetFullPath(uploadRoot), "chat-photos");
                    Directory.CreateDirectory(directory);
                    var fileName = $"{Guid.NewGuid():N}.jpg"; // Telegram photos arrive as JPEG
                    await System.IO.File.WriteAllBytesAsync(Path.Combine(directory, fileName), imageData);
                    imagePath = $"/chat-photos/{fileName}";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to store Telegram agent photo for chat session {SessionId}", chatSession.Id);
            }
        }

        var text = message.Text ?? message.Caption;
        if (string.IsNullOrWhiteSpace(text) && imagePath == null)
            return; // nothing relayable (sticker, voice note, etc.)

        _context.ChatAgentMessages.Add(new Models.ChatAgentMessage
        {
            Id = Guid.NewGuid(),
            ChatSessionId = chatSession.Id,
            Role = Models.ChatMessageRole.HumanAgent,
            Content = string.IsNullOrWhiteSpace(text) ? null : text,
            ImagePath = imagePath,
            ImageExpiresAt = imagePath != null ? now.AddDays(30) : null,
            CreatedAt = now
        });
        chatSession.LastMessageAt = now;
        await _context.SaveChangesAsync();
        // Note: the visitor is intentionally NOT emailed when the team replies — they
        // pick up replies via the widget's polling. GuestEmail may still be collected
        // at escalation for potential future use, but nothing sends to it now.
    }
}
