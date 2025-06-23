using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.DTOs
{
    // For creating a new special offer
    public class CreateSpecialOfferDto
    {
        [Required]
        [StringLength(100)]
        public string Name { get; set; }

        [StringLength(500)]
        public string Description { get; set; }

        [Required]
        public bool IsPercentage { get; set; } = true;

        [Required]
        [Range(0.01, 100)]
        public decimal DiscountValue { get; set; }

        [Required]
        public int Type { get; set; } // 0=FirstTime, 1=Seasonal, 2=Holiday, 3=Custom

        public DateTime? ValidFrom { get; set; }
        public DateTime? ValidTo { get; set; }

        public string? Icon { get; set; }
        public string? BadgeColor { get; set; }
        public decimal? MinimumOrderAmount { get; set; }
        public bool RequiresFirstTimeCustomer { get; set; } = false;
    }

    // For updating a special offer
    public class UpdateSpecialOfferDto
    {
        [StringLength(100)]
        public string Name { get; set; }

        [StringLength(500)]
        public string Description { get; set; }

        public bool IsPercentage { get; set; }
        public decimal DiscountValue { get; set; }
        public int? Type { get; set; }
        public DateTime? ValidFrom { get; set; }
        public DateTime? ValidTo { get; set; }
        public string? Icon { get; set; }
        public string? BadgeColor { get; set; }
        public decimal? MinimumOrderAmount { get; set; }
        public bool IsActive { get; set; }
    }

    // For displaying special offers in admin panel
    public class SpecialOfferAdminDto
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public bool IsPercentage { get; set; }
        public decimal DiscountValue { get; set; }
        public string Type { get; set; }
        public DateTime? ValidFrom { get; set; }
        public DateTime? ValidTo { get; set; }
        public bool IsActive { get; set; }
        public int TotalUsersGranted { get; set; }
        public int TimesUsed { get; set; }
        public DateTime CreatedAt { get; set; }
        public string? Icon { get; set; }
        public string? BadgeColor { get; set; }
        public decimal? MinimumOrderAmount { get; set; } 
        public bool RequiresFirstTimeCustomer { get; set; }
    }

    // For displaying user's available offers
    public class UserSpecialOfferDto
    {
        public int Id { get; set; }
        public int SpecialOfferId { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public bool IsPercentage { get; set; }
        public decimal DiscountValue { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public bool IsUsed { get; set; }
        public string? Icon { get; set; }
        public string? BadgeColor { get; set; }
        public decimal? MinimumOrderAmount { get; set; }
    }
}