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
    }
}
