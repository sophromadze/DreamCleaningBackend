namespace DreamCleaningBackend.DTOs
{
    // DTOs for the AI chat-agent endpoints (ChatController message/upload/history).

    public class ChatMessageRequestDto
    {
        public Guid? SessionId { get; set; }
        public string? Message { get; set; }
        /// <summary>Relative path returned by /api/chat/upload-image, e.g. /chat-photos/{guid}.jpg.</summary>
        public string? ImagePath { get; set; }
        /// <summary>Widget-generated anonymous identifier for guests (stored on the session at creation).</summary>
        public string? GuestIdentifier { get; set; }
        /// <summary>Optional contact email from the widget's start-of-chat field. Stored on the
        /// session at creation for guests only (logged-in users use their account email).
        /// Collected for potential future outreach — nothing sends to it currently.</summary>
        public string? GuestEmail { get; set; }
    }

    public class ChatMessageResponseDto
    {
        public Guid SessionId { get; set; }
        /// <summary>Null after escalation (message relayed to the team, no bot reply) —
        /// the widget renders no bubble and relies on polling for the human's answer.</summary>
        public string? Reply { get; set; }
        public bool Escalated { get; set; }
        /// <summary>Clickable quick-reply options (from the present_choices pseudo-tool).
        /// Transient UI hint — not persisted; clicking one sends it as a normal message.</summary>
        public List<string>? QuickReplies { get; set; }
    }

    public class ChatImageUploadResponseDto
    {
        public string ImagePath { get; set; } = string.Empty;
        public DateTime ExpiresAt { get; set; }
    }

    public class ChatHistoryMessageDto
    {
        public Guid Id { get; set; }
        public string Role { get; set; } = string.Empty; // user | assistant | humanAgent
        public string? Content { get; set; }
        public string? ImagePath { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class ChatSessionHistoryDto
    {
        public Guid SessionId { get; set; }
        public string Status { get; set; } = string.Empty; // aiHandling | escalatedToHuman | resolved
        public List<ChatHistoryMessageDto> Messages { get; set; } = new();
    }

    // ===== Admin chat-history viewer =====

    public class ChatSessionListItemDto
    {
        public Guid Id { get; set; }
        public int? UserId { get; set; }
        public string? UserEmail { get; set; }
        public string? GuestIdentifier { get; set; }
        /// <summary>Guest-provided contact email (guests only) — surfaced so admins can see who left contact info.</summary>
        public string? GuestEmail { get; set; }
        public string Status { get; set; } = "AiHandling"; // AiHandling | EscalatedToHuman | Resolved
        public int? TelegramTopicId { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime LastMessageAt { get; set; }
        public int MessageCount { get; set; }
    }

    public class ChatSessionListResponseDto
    {
        public List<ChatSessionListItemDto> Items { get; set; } = new();
        public int TotalCount { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
        public int TotalPages { get; set; }
    }

    public class ChatAdminTranscriptMessageDto
    {
        public Guid Id { get; set; }
        public string Role { get; set; } = "user"; // user | assistant | humanAgent | system
        public string? Content { get; set; }
        public string? ImagePath { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class ChatAdminTranscriptDto
    {
        public Guid SessionId { get; set; }
        public string Status { get; set; } = "AiHandling";
        public string? UserEmail { get; set; }
        public string? GuestIdentifier { get; set; }
        /// <summary>Guest-provided contact email (guests only) — shown in the transcript panel header.</summary>
        public string? GuestEmail { get; set; }
        public DateTime CreatedAt { get; set; }
        public List<ChatAdminTranscriptMessageDto> Messages { get; set; } = new();
    }

    public class ChatAgentSettingsDto
    {
        public bool EscalationEmailEnabled { get; set; }
        public string VisibilityMode { get; set; } = "Disabled"; // Disabled | AdminOnly | Public
        public DateTime? UpdatedAt { get; set; }
        public string? UpdatedByEmail { get; set; }
    }

    public class ChatAgentVisibilityDto
    {
        public string Mode { get; set; } = string.Empty; // Disabled | AdminOnly | Public
    }

    public class ChatWidgetVisibilityDto
    {
        public bool Visible { get; set; }
    }
}
