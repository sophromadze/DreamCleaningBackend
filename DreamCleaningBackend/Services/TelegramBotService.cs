using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace DreamCleaningBackend.Services;

public class TelegramBotService
{
    private readonly TelegramBotClient? _bot;
    private readonly long _groupChatId;
    private readonly ILogger<TelegramBotService> _logger;
    private readonly bool _isConfigured;
    private long? _botUserId;

    public TelegramBotService(IConfiguration config, ILogger<TelegramBotService> logger)
    {
        _logger = logger;

        var token = config["TelegramBot:Token"];
        var chatId = config["TelegramBot:GroupChatId"];

        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(chatId))
        {
            _logger.LogWarning("TelegramBotService: Token or GroupChatId not configured — Telegram integration disabled.");
            _isConfigured = false;
            return;
        }

        _bot = new TelegramBotClient(token);
        _groupChatId = long.Parse(chatId);
        _isConfigured = true;
    }

    public bool IsConfigured => _isConfigured;

    /// <summary>
    /// Returns the bot's own user ID (used to filter out the bot's own messages in webhook).
    /// </summary>
    public async Task<long> GetBotUserId()
    {
        if (_botUserId == null && _bot != null)
        {
            var me = await _bot.GetMe();
            _botUserId = me.Id;
        }
        return _botUserId ?? 0;
    }

    /// <summary>
    /// Creates a new Forum Topic for a visitor's chat session.
    /// Returns the topic's message_thread_id.
    /// </summary>
    public async Task<int> CreateTopicForVisitor(string visitorName)
    {
        if (!_isConfigured || _bot == null) return 0;

        var topicName = $"💬 {visitorName} - {DateTime.UtcNow:MMM dd, h:mm tt}";

        var topic = await _bot.CreateForumTopic(
            chatId: _groupChatId,
            name: topicName,
            iconColor: 0x6FB9F0
        );

        _logger.LogInformation("Created Telegram Forum Topic: {TopicName} (ThreadId: {ThreadId})",
            topicName, topic.MessageThreadId);

        await _bot.SendMessage(
            chatId: _groupChatId,
            messageThreadId: topic.MessageThreadId,
            text: $"🟢 New chat started by *{EscapeMarkdown(visitorName)}*\n\nJust type your reply here — the visitor will see it on the website\\.",
            parseMode: ParseMode.MarkdownV2
        );

        return topic.MessageThreadId;
    }

    /// <summary>
    /// Sends a text message from a visitor to their Forum Topic.
    /// </summary>
    public async Task SendTextToTopic(int topicThreadId, string visitorName, string message)
    {
        if (!_isConfigured || _bot == null) return;

        await _bot.SendMessage(
            chatId: _groupChatId,
            messageThreadId: topicThreadId,
            text: $"*{EscapeMarkdown(visitorName)}:* {EscapeMarkdown(message)}",
            parseMode: ParseMode.MarkdownV2
        );
    }

    /// <summary>
    /// Sends a photo from a visitor to their Forum Topic.
    /// </summary>
    public async Task SendPhotoToTopic(int topicThreadId, string visitorName, byte[] imageData, string mimeType)
    {
        if (!_isConfigured || _bot == null) return;

        using var stream = new MemoryStream(imageData);
        var extension = mimeType.Contains("png") ? "png" : mimeType.Contains("gif") ? "gif" : "jpg";

        await _bot.SendPhoto(
            chatId: _groupChatId,
            messageThreadId: topicThreadId,
            photo: InputFile.FromStream(stream, $"image.{extension}"),
            caption: $"📷 {visitorName}"
        );
    }

    /// <summary>
    /// Sends a session-end notification to a visitor's Forum Topic.
    /// </summary>
    public async Task SendSessionEndNotification(int topicThreadId, string visitorName)
    {
        if (!_isConfigured || _bot == null) return;

        await _bot.SendMessage(
            chatId: _groupChatId,
            messageThreadId: topicThreadId,
            text: $"🔴 *{EscapeMarkdown(visitorName)}* left the chat\\.",
            parseMode: ParseMode.MarkdownV2
        );
    }

    /// <summary>
    /// Closes (archives) a Forum Topic when the chat ends.
    /// </summary>
    public async Task CloseForumTopic(int topicThreadId)
    {
        if (!_isConfigured || _bot == null) return;

        try
        {
            await _bot.CloseForumTopic(_groupChatId, topicThreadId);
        }
        catch (Exception ex) when (ex.Message.Contains("TOPIC_NOT_MODIFIED"))
        {
            _logger.LogDebug("Forum topic {ThreadId} was already closed", topicThreadId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to close forum topic {ThreadId}", topicThreadId);
        }
    }

    /// <summary>
    /// Downloads a file from Telegram by file_id.
    /// </summary>
    public async Task<byte[]?> DownloadFile(string fileId)
    {
        if (!_isConfigured || _bot == null) return null;

        try
        {
            var file = await _bot.GetFile(fileId);
            if (file.FilePath == null) return null;

            using var stream = new MemoryStream();
            await _bot.DownloadFile(file.FilePath, stream);
            return stream.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download Telegram file {FileId}", fileId);
            return null;
        }
    }

    /// <summary>
    /// Registers the webhook URL with Telegram.
    /// </summary>
    public async Task SetWebhook(string webhookUrl)
    {
        if (!_isConfigured || _bot == null) return;

        await _bot.SetWebhook(webhookUrl);
        _logger.LogInformation("Telegram webhook set to: {Url}", webhookUrl);
    }

    private static string EscapeMarkdown(string text)
    {
        var specialChars = new[] { '_', '*', '[', ']', '(', ')', '~', '`', '>', '#', '+', '-', '=', '|', '{', '}', '.', '!' };
        foreach (var c in specialChars)
            text = text.Replace(c.ToString(), $"\\{c}");
        return text;
    }
}
