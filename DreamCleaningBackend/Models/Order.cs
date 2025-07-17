using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    public class Order
    {
        public int Id { get; set; }

        // User relationship
        public int UserId { get; set; }
        public virtual User User { get; set; }

        // Apartment relationship (optional - user might enter new address)
        public int? ApartmentId { get; set; }
        public virtual Apartment? Apartment { get; set; }

        // Add this temporary field to store apartment name from booking
        // This is used when auto-creating apartments after payment
        [StringLength(100)]
        public string? ApartmentName { get; set; }

        // Service Type
        public int ServiceTypeId { get; set; }
        public virtual ServiceType ServiceType { get; set; }

        // Order details
        [Required]
        public DateTime OrderDate { get; set; }

        [Required]
        public DateTime ServiceDate { get; set; }

        public TimeSpan ServiceTime { get; set; }

        [StringLength(500)]
        public string? CancellationReason { get; set; }

        // Duration in minutes
        public decimal TotalDuration { get; set; }

        // Number of maids/cleaners for this order
        public int MaidsCount { get; set; }

        // Pricing
        [Column(TypeName = "decimal(18,2)")]
        public decimal SubTotal { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal Tax { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal Tips { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal CompanyDevelopmentTips { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal Total { get; set; }

        // Discount applied (if any)
        [Column(TypeName = "decimal(18,2)")]
        public decimal DiscountAmount { get; set; }
        public decimal SubscriptionDiscountAmount { get; set; } = 0;

        [StringLength(50)]
        public string? PromoCode { get; set; }

        [Column(TypeName = "decimal(10,2)")]
        public decimal GiftCardAmountUsed { get; set; } = 0;

        [StringLength(14)]
        public string? GiftCardCode { get; set; }

        public int? UserSpecialOfferId { get; set; }

        [StringLength(100)]
        public string? SpecialOfferName { get; set; }

        // Subscription
        public int SubscriptionId { get; set; }
        public virtual Subscription Subscription { get; set; }

        // Entry method
        [StringLength(100)]
        public string? EntryMethod { get; set; }

        // Special instructions
        [StringLength(500)]
        public string? SpecialInstructions { get; set; }

        // Contact info (stored separately in case different from user profile)
        [Required]
        [StringLength(50)]
        public string ContactFirstName { get; set; }

        [Required]
        [StringLength(50)]
        public string ContactLastName { get; set; }

        [Required]
        [EmailAddress]
        [StringLength(100)]
        public string ContactEmail { get; set; }

        [Required]
        [StringLength(20)]
        public string ContactPhone { get; set; }

        // Service address (stored separately in case different from apartment)
        [Required]
        [StringLength(200)]
        public string ServiceAddress { get; set; }

        [StringLength(50)]
        public string? AptSuite { get; set; }

        [Required]
        [StringLength(100)]
        public string City { get; set; }

        [Required]
        [StringLength(50)]
        public string State { get; set; }

        [Required]
        [StringLength(20)]
        public string ZipCode { get; set; }

        // Order status
        [StringLength(50)]
        public string Status { get; set; } = "Pending";

        // Payment info
        [StringLength(100)]
        public string? PaymentIntentId { get; set; } // Stripe payment intent

        public bool IsPaid { get; set; } = false;
        public DateTime? PaidAt { get; set; }

        // Navigation properties
        public virtual ICollection<OrderService> OrderServices { get; set; } = new List<OrderService>();
        public virtual ICollection<OrderExtraService> OrderExtraServices { get; set; } = new List<OrderExtraService>();

        // Audit fields
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }

        // Add to existing Order model
        public virtual ICollection<OrderCleaner> OrderCleaners { get; set; } = new List<OrderCleaner>();

        [Column(TypeName = "decimal(18,2)")]
        public decimal InitialSubTotal { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal InitialTax { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal InitialTips { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal InitialCompanyDevelopmentTips { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal InitialTotal { get; set; }

        // Navigation property for update history
        public virtual ICollection<OrderUpdateHistory> UpdateHistory { get; set; } = new List<OrderUpdateHistory>();
    }
}