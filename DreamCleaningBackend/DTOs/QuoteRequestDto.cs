using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.DTOs
{
    public class QuoteRequestDto
    {
        [Required(ErrorMessage = "Name is required")]
        public string Name { get; set; }

        [Required(ErrorMessage = "Phone number is required")]
        [RegularExpression(@"^\d{10}$", ErrorMessage = "Phone number must be 10 digits")]
        public string Phone { get; set; }

        public string Message { get; set; }
    }
}
