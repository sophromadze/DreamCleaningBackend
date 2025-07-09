using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.DTOs
{
    public class RegisterDto
    {
        [Required]
        [StringLength(50)]
        public string FirstName { get; set; }

        [Required]
        [StringLength(50)]
        public string LastName { get; set; }

        [Required]
        [EmailAddress]
        public string Email { get; set; }

        [StringLength(20)]
        public string? Phone { get; set; } // Optional

        [Required]
        [StringLength(100, MinimumLength = 8)]
        public string Password { get; set; }

        [Required]
        [Compare("Password")]
        public string ConfirmPassword { get; set; }
    }
}
