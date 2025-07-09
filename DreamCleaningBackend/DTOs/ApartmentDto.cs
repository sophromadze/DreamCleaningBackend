using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.DTOs
{
    public class ApartmentDto
    {
        public int Id { get; set; }

        [Required]
        [StringLength(100)]
        public string Name { get; set; }

        [Required]
        [StringLength(200)]
        public string Address { get; set; }

        [StringLength(50)]
        public string? AptSuite { get; set; }

        [Required]
        [StringLength(100)]
        public string City { get; set; }

        [Required]
        [StringLength(50)]
        public string State { get; set; }

        [Required]
        [StringLength(5, MinimumLength = 5)]
        [RegularExpression(@"^\d{5}$", ErrorMessage = "Postal code must be exactly 5 digits")]
        public string PostalCode { get; set; }

        [StringLength(500)]
        public string? SpecialInstructions { get; set; }
    }
}
