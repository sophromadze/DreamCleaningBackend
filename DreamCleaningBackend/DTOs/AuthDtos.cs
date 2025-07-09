using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace DreamCleaningBackend.DTOs
{
    public class AuthResponseDto
    {
        public UserDto User { get; set; }
        public string Token { get; set; }
        public string RefreshToken { get; set; }
        public bool RequiresEmailVerification { get; set; }
        public string Message { get; set; }
    }

    public class VerifyEmailDto
    {
        public string Token { get; set; }
    }

    public class ForgotPasswordDto
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; }
    }

    public class ResetPasswordDto
    {
        [Required]
        public string Token { get; set; }

        [Required]
        [MinLength(8)]
        public string NewPassword { get; set; }
    }
}
