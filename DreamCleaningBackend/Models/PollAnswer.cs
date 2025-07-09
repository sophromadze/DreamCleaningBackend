using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.Models
{
    public class PollAnswer
    {
        public int Id { get; set; }

        // Relationships
        public int PollSubmissionId { get; set; }
        public virtual PollSubmission PollSubmission { get; set; }

        public int PollQuestionId { get; set; }
        public virtual PollQuestion PollQuestion { get; set; }

        // Answer content
        [StringLength(2000)]
        public string Answer { get; set; }

        // Audit fields
        public DateTime CreatedAt { get; set; }
    }
}