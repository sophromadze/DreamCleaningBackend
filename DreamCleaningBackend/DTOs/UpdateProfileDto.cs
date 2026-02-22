using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.DTOs
{
    public class UpdateProfileDto
    {
        [Required]
        [StringLength(50)]
        public string FirstName { get; set; }

        [Required]
        [StringLength(50)]
        public string LastName { get; set; }

        [Required]
        [EmailAddress]
        [StringLength(100)]
        public string Email { get; set; }

        [StringLength(20)]
        public string? Phone { get; set; }

        /// <summary>Optional. When provided, updates the user's preference to receive emails/SMS from the company.</summary>
        public bool? CanReceiveCommunications { get; set; }

        /// <summary>Optional. When provided, updates the user's preference to receive emails from the company.</summary>
        public bool? CanReceiveEmails { get; set; }

        /// <summary>Optional. When provided, updates the user's preference to receive SMS/messages from the company.</summary>
        public bool? CanReceiveMessages { get; set; }
    }
}
