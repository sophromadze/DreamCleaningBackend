using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    public class PromoCode
    {
        public int Id { get; set; }

        [Required]
        [StringLength(50)]
        public string Code { get; set; }

        [StringLength(200)]
        public string? Description { get; set; }

        // Discount type
        public bool IsPercentage { get; set; } = true;

        // Discount value (percentage or fixed amount)
        [Column(TypeName = "decimal(18,2)")]
        public decimal DiscountValue { get; set; }

        // Usage limits
        public int? MaxUsageCount { get; set; }
        public int CurrentUsageCount { get; set; } = 0;
        public int? MaxUsagePerUser { get; set; }

        // Validity
        public DateTime? ValidFrom { get; set; }
        public DateTime? ValidTo { get; set; }

        // Minimum order amount
        [Column(TypeName = "decimal(18,2)")]
        public decimal? MinimumOrderAmount { get; set; }

        public bool IsActive { get; set; } = true;

        // Audit fields
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }
}