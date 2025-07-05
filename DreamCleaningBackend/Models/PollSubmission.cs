using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.Models
{
    public class PollSubmission
    {
        public int Id { get; set; }

        // User and Service Type relationship
        public int? UserId { get; set; } // CHANGED: Made nullable for anonymous submissions
        public virtual User? User { get; set; } // CHANGED: Made nullable

        public int ServiceTypeId { get; set; }
        public virtual ServiceType ServiceType { get; set; }

        // Contact information
        [Required]
        [StringLength(100)]
        public string ContactFirstName { get; set; }

        [Required]
        [StringLength(100)]
        public string ContactLastName { get; set; }

        [Required]
        [StringLength(200)]
        public string ContactEmail { get; set; }

        [StringLength(20)]
        public string? ContactPhone { get; set; }

        // Service address
        [Required]
        [StringLength(200)]
        public string ServiceAddress { get; set; }

        [StringLength(50)]
        public string? AptSuite { get; set; }

        [Required]
        [StringLength(100)]
        public string City { get; set; }

        [Required]
        [StringLength(50)]
        public string State { get; set; }

        [Required]
        [StringLength(10)]
        public string PostalCode { get; set; }

        // Status
        [StringLength(50)]
        public string Status { get; set; } = "Pending"; // Pending, Reviewed, Quoted, Converted

        [StringLength(1000)]
        public string? AdminNotes { get; set; }

        // Navigation properties
        public virtual ICollection<PollAnswer> PollAnswers { get; set; } = new List<PollAnswer>();

        // Audit fields
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }
}