using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.DTOs
{
    public class CreateBookingDto
    {
        [Required]
        public int ServiceTypeId { get; set; }

        // Admin-chosen display label for the custom ("Pre-Arranged") service type (e.g. "Deep").
        // Stored without the "Cleaning" suffix; ignored unless the selected service type IsCustom.
        [StringLength(100)]
        public string? CustomServiceDisplayName { get; set; }

        [Required]
        public List<BookingServiceDto> Services { get; set; } = new List<BookingServiceDto>();

        public List<BookingExtraServiceDto> ExtraServices { get; set; } = new List<BookingExtraServiceDto>();

        [Required]
        public int SubscriptionId { get; set; }

        [Required]
        public DateTime ServiceDate { get; set; }

        [Required]
        public string ServiceTime { get; set; }

        // Length-capped to match the Order.EntryMethod column (500). [ApiController] auto-validates
        // ModelState, so an over-long "Other" entry method is rejected with a 400 by prepare-payment
        // BEFORE any Stripe charge — preventing the "charged but order failed" double-charge.
        [Required]
        [StringLength(500)]
        public string EntryMethod { get; set; }

        [StringLength(2000)]
        public string? SpecialInstructions { get; set; }

        [Required]
        public string ContactFirstName { get; set; }

        [Required]
        public string ContactLastName { get; set; }

        // Required for all public booking flows (enforced in the controller actions). Nullable at
        // the DTO level only so admins can book for no-email (cash) customers via create-for-user.
        [EmailAddress]
        public string? ContactEmail { get; set; }

        [Required]
        [Phone]
        public string ContactPhone { get; set; }

        [Required]
        public string ServiceAddress { get; set; }

        public string? AptSuite { get; set; }

        [Required]
        public string City { get; set; }

        [Required]
        public string State { get; set; }

        [Required]
        public string ZipCode { get; set; }

        public int? ApartmentId { get; set; }
        public string? ApartmentName { get; set; }
        public string? PromoCode { get; set; }
        public string? GiftCardCode { get; set; }
        public string? ReferralCode { get; set; }
        public decimal GiftCardAmountToUse { get; set; }
        public int? UserSpecialOfferId { get; set; }

        public decimal Tax { get; set; }
        public decimal Total { get; set; }

        [Range(0, double.MaxValue)]
        public decimal Tips { get; set; }

        [Range(0, double.MaxValue)]
        public decimal CompanyDevelopmentTips { get; set; }
        public int MaidsCount { get; set; }
        public decimal TotalDuration { get; set; }
        public decimal DiscountAmount { get; set; }
        public decimal SubscriptionDiscountAmount { get; set; } = 0;
        // Loyalty Discount amount the client computed for the breakdown preview. The backend
        // re-evaluates this from the user's actual LoyaltyDiscountPercentage and applies the
        // stacking rules itself — the client value is read for UX correlation but never trusted
        // for the persisted snapshot on the order.
        public decimal LoyaltyDiscountAmount { get; set; } = 0;
        public decimal SubTotal { get; set; }
        public bool IsCustomPricing { get; set; } = false;
        public decimal? CustomAmount { get; set; }
        public int? CustomCleaners { get; set; }
        public decimal? CustomDuration { get; set; }
        public int? BedroomsQuantity { get; set; }
        public int? BathroomsQuantity { get; set; }
        public List<PhotoUploadDto> UploadedPhotos { get; set; } = new List<PhotoUploadDto>();
        public int PointsToRedeem { get; set; } = 0;
        public bool UseCredits { get; set; } = false;
        public decimal CreditsToApply { get; set; } = 0;

        [StringLength(300)]
        public string? FloorTypes { get; set; }

        [StringLength(100)]
        public string? FloorTypeOther { get; set; }
    }

    public class CreateBookingForUserDto
    {
        [Required]
        public int TargetUserId { get; set; }

        [Required]
        public CreateBookingDto BookingData { get; set; }

        // Manual payment tracking (Phase 1). All optional — default Normal preserves the
        // existing Stripe-only behavior. Parsed case-insensitively in the controller; anything
        // unrecognised falls back to Normal. Reference/Notes are ignored when method=Normal.
        public string? PaymentMethod { get; set; }
        public string? PaymentReference { get; set; }
        public string? PaymentNotes { get; set; }
    }
}
