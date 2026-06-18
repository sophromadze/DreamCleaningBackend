using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    /// <summary>
    /// An inclusive date range during which a cleaner is on vacation / away and
    /// should be treated as busy. Whole-day granularity — no hours.
    /// </summary>
    public class CleanerVacation
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public int CleanerId { get; set; }

        [ForeignKey("CleanerId")]
        public virtual Cleaner Cleaner { get; set; }

        // Inclusive start/end dates (date component only; time is ignored).
        [Column(TypeName = "date")]
        public DateTime StartDate { get; set; }

        [Column(TypeName = "date")]
        public DateTime EndDate { get; set; }

        [StringLength(200)]
        public string? Note { get; set; }

        public int? CreatedByAdminId { get; set; }

        [ForeignKey("CreatedByAdminId")]
        public virtual User? CreatedByAdmin { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
