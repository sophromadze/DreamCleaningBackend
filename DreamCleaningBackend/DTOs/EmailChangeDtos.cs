using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.DTOs
{
    /// <summary>
    /// Deliberately carries NO [Required]/[EmailAddress] attributes. [ApiController] answers a
    /// failed attribute with a ValidationProblemDetails body, which has an `errors` dictionary and
    /// no `message` — so the change-email page could only show Angular's transport text ("Http
    /// failure response ...: 400") instead of naming the mistake. AuthService.InitiateEmailChange
    /// validates both fields itself and answers with the usual { message } shape. Same arrangement
    /// as AdminRegisterUserDto — see Helpers/EmailAddressValidator.
    /// </summary>
    public class InitiateEmailChangeDto
    {
        public string NewEmail { get; set; } = string.Empty;

        public string CurrentPassword { get; set; } = string.Empty;
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
