using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    public class BlockedTimeSlot
    {
        public int Id { get; set; }

        /// <summary>
        /// The date that is blocked (date part only, time ignored).
        /// </summary>
        [Required]
        public DateTime Date { get; set; }

        /// <summary>
        /// If true, the entire day is blocked. When false, only specific hours are blocked.
        /// </summary>
        public bool IsFullDay { get; set; }

        /// <summary>
        /// Comma-separated list of blocked time slots (e.g. "08:00,08:30,09:00").
        /// Only used when IsFullDay is false.
        /// </summary>
        [StringLength(500)]
        public string? BlockedHours { get; set; }

        /// <summary>
        /// Optional reason shown to users (e.g. "We are fully booked").
        /// </summary>
        [StringLength(200)]
        public string? Reason { get; set; }

        /// <summary>
        /// Who created this block.
        /// </summary>
        public int CreatedByUserId { get; set; }
        public virtual User CreatedByUser { get; set; } = null!;

        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }
}
