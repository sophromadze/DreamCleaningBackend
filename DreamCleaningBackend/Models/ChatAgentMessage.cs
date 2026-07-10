using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.Models
{
    public enum ChatMessageRole
    {
        User = 0,
        Assistant = 1,
        /// <summary>Internal audit notes (e.g. escalation events). Never replayed to the model or shown to visitors.</summary>
        System = 2,
        /// <summary>A human support agent's reply relayed from the Telegram topic. Replayed to the model as an assistant turn.</summary>
        HumanAgent = 3
    }

    public class ChatAgentMessage
    {
        public Guid Id { get; set; }

        public Guid ChatSessionId { get; set; }
        public virtual ChatAgentSession ChatSession { get; set; }

        public ChatMessageRole Role { get; set; }

        /// <summary>Message text — null for image-only messages. longtext in MySQL (no StringLength).</summary>
        public string? Content { get; set; }

        /// <summary>Relative URL under the uploads root, e.g. /chat-photos/{guid}.jpg. Purged after 30 days.</summary>
        [StringLength(256)]
        public string? ImagePath { get; set; }

        /// <summary>CreatedAt + 30 days when an image is attached; nulled (with ImagePath) once the file is purged.</summary>
        public DateTime? ImageExpiresAt { get; set; }

        public DateTime CreatedAt { get; set; }
    }
}
