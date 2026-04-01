using DreamCleaningBackend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace DreamCleaningBackend.Hubs;

[AllowAnonymous]
public class LiveChatHub : Hub
{
    private readonly LiveChatSessionManager _sessionManager;
    private readonly TelegramBotService _telegramBot;
    private readonly ILogger<LiveChatHub> _logger;

    public LiveChatHub(
        LiveChatSessionManager sessionManager,
        TelegramBotService telegramBot,
        ILogger<LiveChatHub> logger)
    {
        _sessionManager = sessionManager;
        _telegramBot = telegramBot;
        _logger = logger;
    }

    /// <summary>
    /// Called when a visitor starts a new chat. Creates a Telegram Forum Topic for them.
    /// </summary>
    public async Task StartChat(string visitorName)
    {
        var session = _sessionManager.CreateSession(Context.ConnectionId, visitorName);
        _logger.LogInformation("Chat started: {SessionId} by {Visitor}", session.SessionId, session.VisitorName);

        try
        {
            var topicThreadId = await _telegramBot.CreateTopicForVisitor(session.VisitorName);
            _sessionManager.SetTopicThreadId(session.SessionId, topicThreadId);

            await Clients.Caller.SendAsync("ChatStarted", session.SessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create Telegram topic for session {SessionId}", session.SessionId);
            await Clients.Caller.SendAsync("ChatError", "Unable to start chat. Please try again later.");
        }
    }

    /// <summary>
    /// Called when a visitor reconnects (e.g. page reload). Restores the existing session.
    /// </summary>
    public async Task ReconnectChat(string sessionId)
    {
        var session = _sessionManager.GetSession(sessionId);
        if (session != null)
        {
            _sessionManager.UpdateConnectionId(sessionId, Context.ConnectionId);
            session.IsActive = true;
            session.LastActivityAt = DateTime.UtcNow;
            await Clients.Caller.SendAsync("ChatReconnected", sessionId);
        }
        else
        {
            await Clients.Caller.SendAsync("SessionExpired");
        }
    }

    /// <summary>
    /// Called when the visitor sends a text message.
    /// </summary>
    public async Task SendMessage(string message)
    {
        var session = _sessionManager.GetSessionByConnectionId(Context.ConnectionId);
        if (session == null || session.TopicThreadId == 0) return;

        session.LastActivityAt = DateTime.UtcNow;

        try
        {
            await _telegramBot.SendTextToTopic(session.TopicThreadId, session.VisitorName, message);

            await Clients.Caller.SendAsync("MessageSent", new
            {
                id = Guid.NewGuid().ToString(),
                content = message,
                isFromVisitor = true,
                timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send message to Telegram for session {SessionId}", session.SessionId);
            await Clients.Caller.SendAsync("MessageError", "Failed to send message. Please try again.");
        }
    }

    /// <summary>
    /// Called when the visitor sends an image.
    /// </summary>
    public async Task SendImage(string base64Data, string mimeType)
    {
        var session = _sessionManager.GetSessionByConnectionId(Context.ConnectionId);
        if (session == null || session.TopicThreadId == 0) return;

        var imageBytes = Convert.FromBase64String(base64Data);
        if (imageBytes.Length > 5 * 1024 * 1024)
        {
            await Clients.Caller.SendAsync("MessageError", "Image too large. Maximum size is 5MB.");
            return;
        }

        session.LastActivityAt = DateTime.UtcNow;

        try
        {
            await _telegramBot.SendPhotoToTopic(session.TopicThreadId, session.VisitorName, imageBytes, mimeType);

            await Clients.Caller.SendAsync("MessageSent", new
            {
                id = Guid.NewGuid().ToString(),
                imageBase64 = base64Data,
                imageMimeType = mimeType,
                isFromVisitor = true,
                timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send image to Telegram for session {SessionId}", session.SessionId);
            await Clients.Caller.SendAsync("MessageError", "Failed to send image. Please try again.");
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var session = _sessionManager.GetSessionByConnectionId(Context.ConnectionId);
        if (session != null)
        {
            var sessionId = session.SessionId;
            _sessionManager.RemoveConnection(Context.ConnectionId);

            // Delay 30s before notifying — prevents spam when visitor just refreshes the page.
            // If they reconnect via ReconnectChat within that window, IsActive becomes true again and we skip.
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30));
                var currentSession = _sessionManager.GetSession(sessionId);
                if (currentSession != null && !currentSession.IsActive && currentSession.TopicThreadId != 0)
                {
                    await _telegramBot.SendSessionEndNotification(currentSession.TopicThreadId, currentSession.VisitorName);
                    // Topic stays open so admin can review chat history
                }
            });
        }
        await base.OnDisconnectedAsync(exception);
    }
}
