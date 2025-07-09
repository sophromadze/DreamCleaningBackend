using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.DTOs
{
    public class ValidatePromoCodeDto
    {
        [Required]
        public string Code { get; set; }
    }
}
