using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.DTOs
{
    public class QuoteRequestDto
    {
        [Required(ErrorMessage = "First name is required")]
        public string FirstName { get; set; }

        public string LastName { get; set; }

        [Required(ErrorMessage = "Phone number is required")]
        [RegularExpression(@"^\d{10}$", ErrorMessage = "Phone number must be 10 digits")]
        public string Phone { get; set; }

        [Required(ErrorMessage = "Email is required")]
        [EmailAddress(ErrorMessage = "Invalid email address")]
        public string Email { get; set; }

        [Required(ErrorMessage = "Home address is required")]
        public string HomeAddress { get; set; }

        [Required(ErrorMessage = "Cleaning type is required")]
        public string CleaningType { get; set; }

        [Required(ErrorMessage = "Message is required")]
        public string Message { get; set; }
    }
}
