using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.DTOs
{
    /// <summary>
    /// The /services/commercial-cleaning hero form. Its own DTO rather than
    /// <see cref="QuoteRequestDto"/>: that one is shaped for the residential free-quote form, and
    /// squeezing a business into it printed the business name as "First Name", the contact as
    /// "Last Name", the business address as "Home Address", and frequency / size / notes as one
    /// blob under "Message" in the notification email.
    /// </summary>
    public class CommercialQuoteRequestDto
    {
        [Required(ErrorMessage = "Business name is required")]
        [StringLength(200)]
        public string BusinessName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Contact name is required")]
        [StringLength(200)]
        public string ContactName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Phone number is required")]
        [RegularExpression(@"^\d{10}$", ErrorMessage = "Phone number must be 10 digits")]
        public string Phone { get; set; } = string.Empty;

        [Required(ErrorMessage = "Email is required")]
        [EmailAddress(ErrorMessage = "Invalid email address")]
        public string Email { get; set; } = string.Empty;

        [Required(ErrorMessage = "Business address is required")]
        [StringLength(500)]
        public string BusinessAddress { get; set; } = string.Empty;

        [Required(ErrorMessage = "Type of space is required")]
        [StringLength(100)]
        public string FacilityType { get; set; } = string.Empty;

        [StringLength(50)]
        public string? SquareFootage { get; set; }

        [Required(ErrorMessage = "Cleaning frequency is required")]
        [StringLength(100)]
        public string Frequency { get; set; } = string.Empty;

        [StringLength(4000)]
        public string? Notes { get; set; }
    }
}
