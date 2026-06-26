using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using DreamCleaningBackend.Helpers;

namespace DreamCleaningBackend.Models
{
    /// <summary>
    /// A sales lead / prospect in the CRM pipeline. Captured from the contact form,
    /// free-quote requests, live chat, or entered manually by an admin. A lead is the
    /// pre-booking record: once it converts to a real Order it is marked Won and linked
    /// via <see cref="ConvertedOrderId"/>. Distinct from <see cref="User"/> (an account)
    /// and <see cref="Order"/> (a booking) — a lead may exist with neither.
    /// </summary>
    public class Lead
    {
        [Key]
        public int Id { get; set; }

        [StringLength(100)]
        public string? FirstName { get; set; }

        [StringLength(100)]
        public string? LastName { get; set; }

        [StringLength(255)]
        public string? Email { get; set; }

        private string? _phone;
        [StringLength(50)]
        public string? Phone
        {
            get => _phone;
            set => _phone = PhoneHelper.NormalizeToDigits(value);
        }

        [StringLength(500)]
        public string? ServiceAddress { get; set; }

        /// <summary>Free-text cleaning type the prospect asked about (e.g. "Deep Cleaning").</summary>
        [StringLength(100)]
        public string? CleaningType { get; set; }

        /// <summary>Property category for the lead. One of LeadType.* — see <see cref="LeadType"/>.</summary>
        [Required]
        [StringLength(20)]
        public string Type { get; set; } = LeadType.Residential;

        /// <summary>The message the prospect submitted, if any.</summary>
        [StringLength(2000)]
        public string? Message { get; set; }

        /// <summary>Pipeline stage. One of LeadStage.* — see <see cref="LeadStage"/>.</summary>
        [Required]
        [StringLength(20)]
        public string Stage { get; set; } = LeadStage.New;

        /// <summary>Where the lead came from. One of LeadSource.* — see <see cref="LeadSource"/>.</summary>
        [Required]
        [StringLength(30)]
        public string Source { get; set; } = LeadSource.Manual;

        /// <summary>Optional admin estimate of the deal value, used for pipeline value reporting.</summary>
        [Column(TypeName = "decimal(10,2)")]
        public decimal? EstimatedValue { get; set; }

        /// <summary>Admin currently working this lead. Null = unassigned.</summary>
        public int? AssignedToAdminId { get; set; }

        [ForeignKey("AssignedToAdminId")]
        public virtual User? AssignedToAdmin { get; set; }

        /// <summary>If the lead matches/becomes a registered user, link it here.</summary>
        public int? ClientId { get; set; }

        /// <summary>Set when the lead is Won and converted into a booking.</summary>
        public int? ConvertedOrderId { get; set; }

        /// <summary>Reason captured when a lead is moved to Lost.</summary>
        [StringLength(255)]
        public string? LostReason { get; set; }

        /// <summary>Optional reminder date for the next outreach. Drives the follow-up view.</summary>
        public DateTime? NextFollowUpDate { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>Last time the lead was touched (stage move, note, contact). Drives sort + staleness.</summary>
        public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;

        public virtual ICollection<LeadActivity> Activities { get; set; } = new List<LeadActivity>();

        [NotMapped]
        public string FullName =>
            string.Join(" ", new[] { FirstName, LastName }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
    }

    /// <summary>Canonical pipeline stages. Kept as string constants so the column stays human-readable.</summary>
    public static class LeadStage
    {
        public const string New = "New";
        public const string Contacted = "Contacted";
        public const string Quoted = "Quoted";
        public const string Won = "Won";
        public const string Lost = "Lost";

        public static readonly string[] All = { New, Contacted, Quoted, Won, Lost };
        public static bool IsValid(string? s) => s != null && All.Contains(s);
    }

    /// <summary>Where a lead originated.</summary>
    public static class LeadSource
    {
        public const string ContactForm = "ContactForm";
        public const string QuoteRequest = "QuoteRequest";
        public const string LiveChat = "LiveChat";
        public const string Manual = "Manual";
        public const string Booking = "Booking";

        public static readonly string[] All = { ContactForm, QuoteRequest, LiveChat, Manual, Booking };
        public static bool IsValid(string? s) => s != null && All.Contains(s);
    }

    /// <summary>Property category a lead is for. Defaults to Residential.</summary>
    public static class LeadType
    {
        public const string Residential = "Residential";
        public const string Commercial = "Commercial";

        public static readonly string[] All = { Residential, Commercial };
        public static bool IsValid(string? s) => s != null && All.Contains(s);
    }
}
