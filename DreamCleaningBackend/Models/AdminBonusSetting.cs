using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    // Single-row configuration for the per-order admin bonus. SuperAdmin can edit
    // RatePerOrder; everything else snapshots the value at the time it was needed
    // (see OrderAdminAssignmentHistory.BonusRateAtChange).
    public class AdminBonusSetting
    {
        [Key]
        public int Id { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal RatePerOrder { get; set; } = 10m;

        [Required]
        [StringLength(10)]
        public string Currency { get; set; } = "GEL";

        public int? UpdatedByUserId { get; set; }

        [ForeignKey("UpdatedByUserId")]
        public virtual User? UpdatedByUser { get; set; }

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
