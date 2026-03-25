using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    public class AdminShift
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public DateTime ShiftDate { get; set; }

        [Required]
        public int AdminId { get; set; }

        [ForeignKey("AdminId")]
        public virtual User Admin { get; set; } = null!;

        [StringLength(500)]
        public string? Notes { get; set; }

        public int CreatedByUserId { get; set; }

        [ForeignKey("CreatedByUserId")]
        public virtual User CreatedByUser { get; set; } = null!;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
