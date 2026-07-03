using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using DreamCleaningBackend.Helpers;

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

        // For the custom ("Pre-Arranged") service type only: the per-order display name an admin
        // chose at booking time (e.g. "Deep", "Office", "Move In/Out"). Stored WITHOUT the trailing
        // "Cleaning" word. Customer/cleaner-facing surfaces show "<this> Cleaning" instead of the
        // generic custom service-type name; the admin orders table shows just "<this>". Null for
        // non-custom orders and for legacy custom orders booked before this field existed.
        // Use GetDisplayServiceTypeName() (OrderServiceTypeNameExtensions) to resolve the effective name.
        [StringLength(100)]
        public string? CustomServiceDisplayName { get; set; }

        // Order details
        [Required]
        public DateTime OrderDate { get; set; }

        [Required]
        public DateTime ServiceDate { get; set; }

        public TimeSpan ServiceTime { get; set; }

        [StringLength(500)]
        public string? CancellationReason { get; set; }

        public bool IsLateCancellation { get; set; } = false;

        // When true, auto-cancellation of unpaid orders is disabled (e.g. admin reactivated this order)
        public bool IsAutoCancelExempt { get; set; } = false;

        // Duration in minutes
        public decimal TotalDuration { get; set; }

        // Number of maids/cleaners for this order
        public int MaidsCount { get; set; }

        // Informational only for cleaner+hours/custom modes (does not affect pricing/duration)
        public int? BedroomsQuantity { get; set; }
        public int? BathroomsQuantity { get; set; }

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

        // Loyalty Discount snapshot — value applied to this specific order. Stored as both
        // amount and percentage so the historical record survives even if the user's current
        // LoyaltyDiscountPercentage changes after the order. Used on cancellation to restore
        // the percentage back to the user account.
        [Column(TypeName = "decimal(10,2)")]
        public decimal LoyaltyDiscountAmount { get; set; } = 0;

        [Column(TypeName = "decimal(5,2)")]
        public decimal LoyaltyDiscountPercentage { get; set; } = 0;

        [StringLength(50)]
        public string? PromoCode { get; set; }

        [Column(TypeName = "decimal(10,2)")]
        public decimal GiftCardAmountUsed { get; set; } = 0;

        [StringLength(14)]
        public string? GiftCardCode { get; set; }

        // Bubble points redemption on this order
        public int PointsRedeemed { get; set; } = 0;

        [Column(TypeName = "decimal(10,2)")]
        public decimal PointsRedeemedDiscount { get; set; } = 0;

        // Bubble reward balance (credits) used on this order
        [Column(TypeName = "decimal(10,2)")]
        public decimal RewardBalanceUsed { get; set; } = 0;

        public int? UserSpecialOfferId { get; set; }

        [StringLength(100)]
        public string? SpecialOfferName { get; set; }

        // Subscription
        public int? SubscriptionId { get; set; }
        public virtual Subscription? Subscription { get; set; }

        // Entry method. Expanded from 100 to 500 so longer "Other" instructions fit — a value
        // exceeding the column previously failed the INSERT in confirm-payment AFTER the card was
        // charged, leaving customers charged with no order (and double-charged on retry).
        [StringLength(500)]
        public string? EntryMethod { get; set; }

        // Special instructions
        [StringLength(2000)]
        public string? SpecialInstructions { get; set; }

        // Floor types (comma-separated, may include "other:custom text")
        [StringLength(300)]
        public string? FloorTypes { get; set; }

        [StringLength(100)]
        public string? FloorTypeOther { get; set; }

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

        private string _contactPhone = string.Empty;
        [Required]
        [StringLength(20)]
        public string ContactPhone
        {
            get => _contactPhone;
            set => _contactPhone = PhoneHelper.NormalizeToDigitsOrEmpty(value);
        }

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

        /// <summary>
        /// Secret token embedded in emailed/SMSed payment links (/order/{id}/pay?t=...). Lets the
        /// recipient open the order's payment page WITHOUT logging in — but only while something
        /// is unpaid (initial payment or a pending additional payment); guest access dies once
        /// fully paid. Null on legacy orders until a link is (re)sent, which backfills it lazily.
        /// </summary>
        [StringLength(64)]
        public string? PaymentAccessToken { get; set; }

        // Payment info
        [StringLength(100)]
        public string? PaymentIntentId { get; set; } // Stripe payment intent

        public bool IsPaid { get; set; } = false;
        public DateTime? PaidAt { get; set; }

        // Manual payment tracking. Default Normal (=0) preserves the pre-existing Stripe/IsPaid
        // flow exactly — no behavioral change for existing rows after migration. When set to
        // anything else the order was paid outside Stripe and IsPaid stays false; statistics
        // queries must OR these two conditions to count manual-paid orders.
        public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.Normal;

        [StringLength(255)]
        public string? PaymentReference { get; set; }  // Zelle confirmation #, check #, receipt #

        [StringLength(1000)]
        public string? PaymentNotes { get; set; }

        public DateTime? ManualPaymentRecordedAt { get; set; }

        public int? ManualPaymentRecordedByUserId { get; set; }

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

        // Cleaner salary fields
        [Column(TypeName = "decimal(18,2)")]
        public decimal CleanerHourlyRate { get; set; } = 20m; // Default $20/hr for regular, $21 for deep cleaning

        [Column(TypeName = "decimal(18,2)")]
        public decimal CleanerTotalSalary { get; set; } = 0m; // totalDuration (hrs) * hourlyRate * maidsCount

        // Navigation property for update history
        public virtual ICollection<OrderUpdateHistory> UpdateHistory { get; set; } = new List<OrderUpdateHistory>();

        // The admin (User with Admin or SuperAdmin role) currently responsible for this
        // order. Determines bonus credit. History of changes lives in
        // OrderAdminAssignmentHistories so reassignments stay auditable.
        public int? AssignedAdminId { get; set; }

        [ForeignKey("AssignedAdminId")]
        public virtual User? AssignedAdmin { get; set; }

        public virtual ICollection<OrderAdminAssignmentHistory> AdminAssignmentHistory { get; set; }
            = new List<OrderAdminAssignmentHistory>();
    }
}