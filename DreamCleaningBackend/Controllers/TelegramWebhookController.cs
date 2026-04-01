using DreamCleaningBackend.Hubs;
using DreamCleaningBackend.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace DreamCleaningBackend.Controllers;

[ApiController]
[Route("api/telegram")]
public class TelegramWebhookController : ControllerBase
{
    private readonly LiveChatSessionManager _sessionManager;
    private readonly TelegramBotService _telegramBot;
    private readonly IHubContext<LiveChatHub> _hubContext;
    private readonly ILogger<TelegramWebhookController> _logger;

    public TelegramWebhookController(
        LiveChatSessionManager sessionManager,
        TelegramBotService telegramBot,
        IHubContext<LiveChatHub> hubContext,
        ILogger<TelegramWebhookController> logger)
    {
        _sessionManager = sessionManager;
        _telegramBot = telegramBot;
        _hubContext = hubContext;
        _logger = logger;
    }

    [HttpPost("webhook")]
    public async Task<IActionResult> HandleWebhook()
    {
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
}
