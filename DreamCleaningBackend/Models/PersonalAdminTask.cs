using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    public class PersonalAdminTask
    {
        [Key]
        public int Id { get; set; }

        [Required]
        [StringLength(500)]
        public string Title { get; set; } = string.Empty;

        [StringLength(2000)]
        public string? Description { get; set; }

        [Required]
        [StringLength(20)]
        public string Priority { get; set; } = "Normal";

        [Required]
        [StringLength(20)]
        public string Status { get; set; } = "Todo";

        public DateTime? DueDate { get; set; }

        public int AssignedToAdminId { get; set; }

        [ForeignKey("AssignedToAdminId")]
        public virtual User AssignedToAdmin { get; set; } = null!;

        public int CreatedByAdminId { get; set; }

        [ForeignKey("CreatedByAdminId")]
        public virtual User CreatedByAdmin { get; set; } = null!;

        [StringLength(2000)]
        public string? CompletionNote { get; set; }

        public DateTime? CompletedAt { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
