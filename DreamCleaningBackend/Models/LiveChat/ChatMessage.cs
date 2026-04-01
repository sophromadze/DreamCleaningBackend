namespace DreamCleaningBackend.Models.LiveChat;

public class ChatMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string SessionId { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? ImageBase64 { get; set; }
    public string? ImageMimeType { get; set; }
    public bool IsFromVisitor { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
