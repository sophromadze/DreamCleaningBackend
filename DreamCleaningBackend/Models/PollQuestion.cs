using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    public class PollQuestion
    {
        public int Id { get; set; }

        [Required]
        [StringLength(500)]
        public string Question { get; set; }

        [StringLength(50)]
        public string QuestionType { get; set; } = "text"; // text, textarea, dropdown, radio, checkbox

        [StringLength(1000)]
        public string? Options { get; set; } // JSON string for dropdown/radio/checkbox options

        public bool IsRequired { get; set; } = false;
        public int DisplayOrder { get; set; }
        public bool IsActive { get; set; } = true;

        // Service Type relationship
        public int ServiceTypeId { get; set; }
        public virtual ServiceType ServiceType { get; set; }

        // Navigation properties
        public virtual ICollection<PollAnswer> PollAnswers { get; set; } = new List<PollAnswer>();

        // Audit fields
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }
}