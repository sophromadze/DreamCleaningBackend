using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.Models
{
    public class Apartment
    {
        public int Id { get; set; }
        [Required]
        [StringLength(100)]
        public string Name { get; set; } // e.g., "My Home", "Beach House"
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
        // Special instructions for cleaners
        [StringLength(500)]
        public string? SpecialInstructions { get; set; }
        // Foreign key
        public int UserId { get; set; }
        public virtual User User { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public bool IsActive { get; set; } = true;
    }
}
