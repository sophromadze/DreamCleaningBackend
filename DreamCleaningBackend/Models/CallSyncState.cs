using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.Models
{
    /// <summary>
    /// Single-row-per-provider watermark for the call-tracking sync. Persists the last point in
    /// time we successfully pulled calls up to, so restarts don't re-scan from the beginning and
    /// a failed run can be retried without advancing past the gap. Unique on Provider.
    /// </summary>
    public class CallSyncState
    {
        [Key]
        public int Id { get; set; }

        [Required]
        [StringLength(50)]
        public string Provider { get; set; } = string.Empty;

        /// <summary>UTC timestamp of the most recent successful sync watermark.</summary>
        public DateTime LastSyncedUtc { get; set; }
    }
}
