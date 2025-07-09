using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.DTOs
{
    public class GoogleLoginDto
    {
        [Required]
        public string IdToken { get; set; }
    }
}
