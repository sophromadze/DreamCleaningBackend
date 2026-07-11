using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.Models
{
    /// <summary>
    /// Admin-configurable mapping from a Telegram account (numeric user id) to the
    /// display name the chat widget shows for that agent's replies (e.g. "Tamar").
    /// Deliberately decoupled from real Telegram profile names — never auto-populated
    /// from Telegram. Unmapped senders render as "Team" (resolved at read time from
    /// ChatAgentMessage.SenderTelegramUserId, so adding a mapping retroactively names
    /// that person's past messages).
    /// </summary>
    public class TelegramAgentDisplayName
    {
        public int Id { get; set; }

        /// <summary>Telegram's numeric user id (message.from.id) — unique.</summary>
        public long TelegramUserId { get; set; }

        /// <summary>Admin-chosen, first-name-style label shown to visitors.</summary>
        [Required]
        [StringLength(50)]
        public string DisplayName { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}
