using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.DTOs
{
    public class RefreshTokenDto
    {
        [Required]
        public string Token { get; set; }

        [Required]
        public string RefreshToken { get; set; }
    }
}
