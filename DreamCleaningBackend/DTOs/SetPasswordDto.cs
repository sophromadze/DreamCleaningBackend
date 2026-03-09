using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.DTOs
{
    public class SetPasswordDto
    {
        [Required]
        [StringLength(100, MinimumLength = 8)]
        public string NewPassword { get; set; }
    }
}
