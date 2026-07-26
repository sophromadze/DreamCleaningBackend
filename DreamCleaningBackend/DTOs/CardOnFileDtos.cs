using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.DTOs
{
    public class SaveCardDto
    {
        [Required]
        public string PaymentMethodId { get; set; }
    }
}
