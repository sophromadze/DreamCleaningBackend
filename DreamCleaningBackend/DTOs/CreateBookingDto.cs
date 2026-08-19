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
        // Public special offer applied without a per-user grant (guest flow). The frontend
        // has always sent this; it is used to re-derive the discount amount server-side.
        public int? SpecialOfferId { get; set; }

        public decimal Tax { get; set; }
        public decimal Total { get; set; }

        [Range(0, double.MaxValue)]
        public decimal Tips { get; set; }

        // CompanyDevelopmentTips is deliberately absent: "Tips for Company Development" is
        // retired and cannot be set by any client. New orders always store 0.
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

        /// <summary>"Apartment" or "House"; null for legacy orders and for service types that
        /// have no levels service. Normalized server-side by PropertyDetailsHelper - an unknown
        /// string is stored as null rather than trusted, so the column can only ever hold one of
        /// the two known values. The level count itself travels as an ordinary service line in
        /// Services, priced through the shared calculator like bedrooms or bathrooms.</summary>
        [StringLength(20)]
        public string? PropertyType { get; set; }

        /// <summary>
        /// Levels for a house on a service type with NO priced levels service - informational
        /// only, exactly like BedroomsQuantity / BathroomsQuantity above. Ignored whenever a
        /// priced levels line is present, so it can never override what was charged. Clamped
        /// server-side by PropertyDetailsHelper.
        /// </summary>
        public int? LevelsQuantity { get; set; }
        public List<PhotoUploadDto> UploadedPhotos { get; set; } = new List<PhotoUploadDto>();
        public int PointsToRedeem { get; set; } = 0;
        public bool UseCredits { get; set; } = false;
        public decimal CreditsToApply { get; set; } = 0;

        [StringLength(300)]
        public string? FloorTypes { get; set; }

        [StringLength(100)]
        public string? FloorTypeOther { get; set; }

        /// <summary>First-touch marketing attribution captured client-side (self-service flow only).
        /// Ignored for admin create-for-user, which is stamped "Phone/Unknown" server-side. Survives
        /// the prepare→confirm payment gap because the whole DTO is stored via BookingDataService.</summary>
        public AttributionDto? Attribution { get; set; }

        /// <summary>Converting-session attribution — the channel of the session in which this booking
        /// was placed (may differ from first-touch Attribution for a returning visitor). Same trust
        /// model: normalized/clamped server-side, self-service only.</summary>
        public AttributionDto? ConvertingAttribution { get; set; }

        /// <summary>Card-on-file opt-in ("save this card for faster checkout"). prepare-payment
        /// saves the card used for this payment via setup_future_usage; confirm-payment persists
        /// it on the User after the booking's own payment succeeds. Saving is all this does —
        /// every future charge still requires an explicit customer/admin action.</summary>
        public bool SaveCardForFutureUse { get; set; } = false;
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
