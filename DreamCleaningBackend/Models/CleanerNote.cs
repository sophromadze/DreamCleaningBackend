using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    public class CleanerNote
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public int CleanerId { get; set; }

        [ForeignKey("CleanerId")]
        public virtual Cleaner Cleaner { get; set; } = null!;

        public int? AdminId { get; set; }

        [ForeignKey("AdminId")]
        public virtual User? Admin { get; set; }

        [StringLength(100)]
        public string? AdminDisplayName { get; set; }

        [Required]
        [StringLength(4000)]
        public string Text { get; set; } = string.Empty;

        public int? OrderId { get; set; }

        [ForeignKey("OrderId")]
        public virtual Order? Order { get; set; }

        [StringLength(500)]
        public string? OrderPerformance { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
