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

        /// <summary>See SpecialOffer.OfferKey. Blank or missing = no key. SuperAdmin only.</summary>
        [StringLength(50)]
        public string? OfferKey { get; set; }
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

        private decimal? _minimumOrderAmount;

        /// <summary>
        /// ABSENT from the body keeps the stored minimum (the admin inline editor did not send it, and
        /// a page cached from before this fix still won't); present-but-null clears it.
        /// </summary>
        public decimal? MinimumOrderAmount
        {
            get => _minimumOrderAmount;
            set { _minimumOrderAmount = value; MinimumOrderAmountProvided = true; }
        }

        /// <summary>True when the request body carried <see cref="MinimumOrderAmount"/> at all.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool MinimumOrderAmountProvided { get; private set; }

        public bool IsActive { get; set; }

        private string? _offerKey;

        /// <summary>
        /// See SpecialOffer.OfferKey. ABSENT from the body leaves the stored key alone - an admin
        /// page loaded before the key existed must not wipe the key the AddOfferKeyAndMostPopular
        /// migration filled in. Present-but-blank (or null) clears it. SuperAdmin only.
        /// </summary>
        [StringLength(50)]
        public string? OfferKey
        {
            get => _offerKey;
            set { _offerKey = value; OfferKeyProvided = true; }
        }

        /// <summary>True when the request body carried <see cref="OfferKey"/> at all.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool OfferKeyProvided { get; private set; }
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
        public string? OfferKey { get; set; }
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
        /// <summary>See SpecialOffer.OfferKey - how the booking page recognises the first-time offer.</summary>
        public string? OfferKey { get; set; }
    }
}