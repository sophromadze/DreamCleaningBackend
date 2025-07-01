using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.DTOs
{
    public class InitiateEmailChangeDto
    {
        [Required]
        [EmailAddress]
        public string NewEmail { get; set; }

        [Required]
        public string CurrentPassword { get; set; }
    }

    public class ConfirmEmailChangeDto
    {
        [Required]
        public string Token { get; set; }
    }

    public class EmailChangeResponseDto
    {
        public string Message { get; set; }
        public bool RequiresVerification { get; set; }
    }
}