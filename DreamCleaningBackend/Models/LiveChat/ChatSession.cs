namespace DreamCleaningBackend.Models.LiveChat;

public class ChatSession
{
    public string SessionId { get; set; } = string.Empty;
    public string ConnectionId { get; set; } = string.Empty;
    public string VisitorName { get; set; } = "Visitor";
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; } = true;

    // The Telegram Forum Topic thread ID for this visitor's conversation
    public int TopicThreadId { get; set; }
}
