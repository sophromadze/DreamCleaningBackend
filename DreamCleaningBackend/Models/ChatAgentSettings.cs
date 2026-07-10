using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.Models
{
    /// <summary>Who can see (and use) the chat widget. Enforced server-side on the
    /// chat API endpoints as well as reported to the widget — hiding the button alone
    /// would leave the Anthropic-backed API publicly callable.</summary>
    public enum ChatWidgetVisibility
    {
        Disabled = 0,
        AdminOnly = 1,
        Public = 2
    }

    /// <summary>
    /// Single-row runtime settings for the AI chat agent (MaintenanceMode pattern:
    /// admin-toggleable without a redeploy). Row is created on first read.
    /// </summary>
    public class ChatAgentSettings
    {
        public int Id { get; set; }

        /// <summary>When true, every escalation also sends a transcript email to the company address.</summary>
        public bool EscalationEmailEnabled { get; set; } = true;

        /// <summary>Widget + chat API visibility. Deliberately defaults to Disabled —
        /// the owner flips it to AdminOnly/Public via the admin endpoint after deploy.</summary>
        public ChatWidgetVisibility VisibilityMode { get; set; } = ChatWidgetVisibility.Disabled;

        public DateTime? UpdatedAt { get; set; }

        [StringLength(255)]
        public string? UpdatedByEmail { get; set; }
    }
}
