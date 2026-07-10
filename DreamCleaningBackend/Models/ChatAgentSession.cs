using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.Models
{
    public enum ChatSessionStatus
    {
        AiHandling = 0,
        EscalatedToHuman = 1,
        Resolved = 2
    }

    /// <summary>
    /// DB-persisted AI chat-agent session. Named ChatAgentSession (not ChatSession)
    /// to avoid colliding with the in-memory live-chat Models.LiveChat.ChatSession.
    /// TelegramTopicId links an escalated session to its Telegram Forum Topic —
    /// the first durable session↔topic mapping (the live-chat one is in-memory only).
    /// </summary>
    public class ChatAgentSession
    {
        public Guid Id { get; set; }

        public int? UserId { get; set; } // null for guests
        public virtual User? User { get; set; }

        /// <summary>Anonymous identifier for guests (widget-generated, cookie/localStorage-based).</summary>
        [StringLength(64)]
        public string? GuestIdentifier { get; set; }

        public ChatSessionStatus Status { get; set; } = ChatSessionStatus.AiHandling;

        /// <summary>Telegram Forum Topic message_thread_id — set once escalated (null while AI handles or Telegram is disabled).</summary>
        public int? TelegramTopicId { get; set; }

        /// <summary>Guest-provided contact email, captured post-escalation from a
        /// message containing an email address (asked once in the escalation reply).
        /// Collected for potential future use — nothing sends to it currently.</summary>
        [StringLength(255)]
        public string? GuestEmail { get; set; }

        public DateTime CreatedAt { get; set; }
        public DateTime LastMessageAt { get; set; }

        public virtual ICollection<ChatAgentMessage> Messages { get; set; } = new List<ChatAgentMessage>();
    }
}
