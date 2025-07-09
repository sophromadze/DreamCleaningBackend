using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.DTOs
{
    public class ChangePasswordDto
    {
        [Required]
        public string CurrentPassword { get; set; }

        [Required]
        [StringLength(100, MinimumLength = 8)]
        public string NewPassword { get; set; }
    }
}
