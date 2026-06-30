using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    /// <summary>
    /// One entry in a lead's timeline: a note, a stage change, a logged call/email/SMS,
    /// or a system event (e.g. "captured from contact form"). Gives each lead an auditable
    /// history that the pipeline detail panel renders chronologically.
    /// </summary>
    public class LeadActivity
    {
        [Key]
        public int Id { get; set; }

        public int LeadId { get; set; }

        [ForeignKey("LeadId")]
        public virtual Lead Lead { get; set; } = null!;

        /// <summary>One of LeadActivityType.* — see <see cref="LeadActivityType"/>.</summary>
        [Required]
        [StringLength(20)]
        public string Type { get; set; } = LeadActivityType.Note;

        [StringLength(2000)]
        public string? Content { get; set; }

        /// <summary>Admin who created the entry. Null for system-generated entries (e.g. auto-capture).</summary>
        public int? AdminId { get; set; }

        [ForeignKey("AdminId")]
        public virtual User? Admin { get; set; }

        /// <summary>Snapshot of the admin display name so the timeline survives admin renames/deletes.</summary>
        [StringLength(200)]
        public string? AdminName { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public static class LeadActivityType
    {
        public const string Note = "Note";
        public const string StageChange = "StageChange";
        public const string Update = "Update";
        public const string Call = "Call";
        public const string Email = "Email";
        public const string Sms = "Sms";
        public const string System = "System";

        public static readonly string[] All = { Note, StageChange, Update, Call, Email, Sms, System };
        public static bool IsValid(string? s) => s != null && All.Contains(s);
    }
}
