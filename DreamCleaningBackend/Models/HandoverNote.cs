using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    public class HandoverNote
    {
        [Key]
        public int Id { get; set; }

        [Required]
        [Column(TypeName = "TEXT")]
        public string Content { get; set; } = string.Empty;

        public int AdminId { get; set; }

        [ForeignKey("AdminId")]
        public virtual User Admin { get; set; } = null!;

        [Required]
        [StringLength(50)]
        public string TargetAudience { get; set; } = "ForNextAdmin";

        public int TaskCount { get; set; }
        public int InteractionCount { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
