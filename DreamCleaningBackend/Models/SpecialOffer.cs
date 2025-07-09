using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.Models
{
    public class SpecialOffer
    {
        public int Id { get; set; }

        [Required]
        [StringLength(100)]
        public string Name { get; set; } // e.g., "First Time Discount", "Mother's Day Special"

        [StringLength(500)]
        public string Description { get; set; }

        // Discount configuration
        public bool IsPercentage { get; set; } = true;

        [Column(TypeName = "decimal(18,2)")]
        public decimal DiscountValue { get; set; }

        // Offer type
        public OfferType Type { get; set; } // FirstTime, Seasonal, Holiday, Custom

        // Validity period (null means always valid)
        public DateTime? ValidFrom { get; set; }
        public DateTime? ValidTo { get; set; }

        // Display settings
        public string? Icon { get; set; } // Icon to show in UI
        public string? BadgeColor { get; set; } // Color for the offer badge
        public int DisplayOrder { get; set; }

        // Conditions
        [Column(TypeName = "decimal(18,2)")]
        public decimal? MinimumOrderAmount { get; set; }

        public bool IsActive { get; set; } = true;
        public bool RequiresFirstTimeCustomer { get; set; } = false;

        // Audit fields
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public int? CreatedByUserId { get; set; }

        // Navigation properties
        public virtual ICollection<UserSpecialOffer> UserSpecialOffers { get; set; } = new List<UserSpecialOffer>();
    }

    public class UserSpecialOffer
    {
        public int Id { get; set; }

        public int UserId { get; set; }
        public virtual User User { get; set; }

        public int SpecialOfferId { get; set; }
        public virtual SpecialOffer SpecialOffer { get; set; }

        // Track usage
        public bool IsUsed { get; set; } = false;
        public DateTime? UsedAt { get; set; }
        public int? UsedOnOrderId { get; set; }
        public virtual Order? UsedOnOrder { get; set; }

        // When the offer was made available to this user
        public DateTime GrantedAt { get; set; }

        // Optional: Expiry for this specific user (overrides general offer expiry)
        public DateTime? ExpiresAt { get; set; }
    }
}
