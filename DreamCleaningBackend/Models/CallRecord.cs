using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    /// <summary>
    /// A single completed phone call pulled from a telephony provider (currently RingCentral)
    /// by the call-tracking sync. Provider-neutral on purpose: nothing here is RingCentral-specific,
    /// so swapping the provider (e.g. to Dialpad) only means replacing the ICallProvider adapter.
    /// Deduplicated on (Provider, ExternalId). Optionally linked to a CRM <see cref="Lead"/> when
    /// the caller's number matches an existing lead.
    /// </summary>
    public class CallRecord
    {
        [Key]
        public int Id { get; set; }

        /// <summary>Telephony provider this record came from, e.g. "RingCentral".</summary>
        [Required]
        [StringLength(50)]
        public string Provider { get; set; } = string.Empty;

        /// <summary>The provider's own call-record id. Combined with Provider for dedupe.</summary>
        [Required]
        [StringLength(128)]
        public string ExternalId { get; set; } = string.Empty;

        /// <summary>Provider session id (groups call legs). Nullable — not all providers expose it.</summary>
        [StringLength(128)]
        public string? SessionId { get; set; }

        /// <summary>"Inbound" or "Outbound".</summary>
        [Required]
        [StringLength(20)]
        public string Direction { get; set; } = string.Empty;

        /// <summary>Provider result/disposition, e.g. Accepted, Missed, Voicemail, Rejected.</summary>
        [Required]
        [StringLength(50)]
        public string Result { get; set; } = string.Empty;

        [StringLength(50)]
        public string? FromNumber { get; set; }

        [StringLength(200)]
        public string? FromName { get; set; }

        [StringLength(50)]
        public string? ToNumber { get; set; }

        /// <summary>Call start time, stored in UTC.</summary>
        public DateTime StartTimeUtc { get; set; }

        public int DurationSeconds { get; set; }

        [StringLength(1000)]
        public string? RecordingUrl { get; set; }

        /// <summary>CRM lead this call was matched to, if any.</summary>
        public int? LeadId { get; set; }

        [ForeignKey("LeadId")]
        public virtual Lead? Lead { get; set; }

        /// <summary>
        /// Auto-computed classification of who called. One of <see cref="CallCategory"/> —
        /// Customer / Cleaner / Spam / Unknown. Recomputed at sync time and by the reclassify
        /// endpoint; never edited by hand.
        /// </summary>
        [Required]
        [StringLength(20)]
        public string CallCategory { get; set; } = Models.CallCategory.Unknown;

        /// <summary>Set when the caller's number matches a <see cref="Cleaner"/> (bare-digit match).</summary>
        public int? MatchedCleanerId { get; set; }

        [ForeignKey("MatchedCleanerId")]
        public virtual Cleaner? MatchedCleaner { get; set; }

        /// <summary>
        /// True when the call was placed TO the dedicated ad tracking number (the frontend swaps
        /// the displayed number for Google Ads visitors). An INDEPENDENT dimension from
        /// <see cref="CallCategory"/> — an ad call can also be Customer/Cleaner/Spam. Recomputed at
        /// sync time and by the reclassify endpoint; never edited by hand.
        /// </summary>
        public bool IsAdCall { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>Canonical call classifications. String constants so the column stays readable.</summary>
    public static class CallCategory
    {
        public const string Customer = "Customer";
        public const string Cleaner = "Cleaner";
        public const string Spam = "Spam";
        public const string Unknown = "Unknown";

        public static readonly string[] All = { Customer, Cleaner, Spam, Unknown };
        public static bool IsValid(string? s) => s != null && All.Contains(s);
    }
}
