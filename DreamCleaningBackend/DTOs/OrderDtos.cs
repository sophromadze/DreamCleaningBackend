using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.DTOs
{
    public class OrderDto
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public int ServiceTypeId { get; set; }
        // Already resolved through OrderServiceTypeNameExtensions — "<label> Cleaning" for custom orders.
        public string ServiceTypeName { get; set; }
        // True when this order uses the custom ("Pre-Arranged") service type.
        public bool IsCustomServiceType { get; set; }
        // Bare admin-chosen label for custom orders (no "Cleaning" suffix), e.g. "Deep". Null otherwise.
        public string? CustomServiceDisplayName { get; set; }
        public DateTime OrderDate { get; set; }
        public DateTime ServiceDate { get; set; }
        public TimeSpan ServiceTime { get; set; }
        public string Status { get; set; }
        public decimal SubTotal { get; set; }
        public decimal Tax { get; set; }
        public decimal Tips { get; set; }
        public decimal CompanyDevelopmentTips { get; set; }
        public decimal Total { get; set; }
        public decimal DiscountAmount { get; set; }
        public decimal SubscriptionDiscountAmount { get; set; }
        // Loyalty Discount snapshot — what was actually applied on this order at booking time.
        // Stored so the breakdown survives changes to the user's current loyalty percentage.
        public decimal LoyaltyDiscountAmount { get; set; }
        public decimal LoyaltyDiscountPercentage { get; set; }
        public string? PromoCode { get; set; }
        public string? SpecialOfferName { get; set; }
        public int? UserSpecialOfferId { get; set; }
        public string? PromoCodeDetails { get; set; }
        public string? GiftCardDetails { get; set; }
        public int? SubscriptionId { get; set; }
        public string SubscriptionName { get; set; }
        public string? GiftCardCode { get; set; }
        public decimal GiftCardAmountUsed { get; set; }
        public int PointsRedeemed { get; set; }
        public decimal PointsRedeemedDiscount { get; set; }
        public decimal RewardBalanceUsed { get; set; }
        public int PointsEarned { get; set; }
        public string? EntryMethod { get; set; }
        public string? SpecialInstructions { get; set; }
        public string? FloorTypes { get; set; }
        public string? FloorTypeOther { get; set; }
        public string ContactFirstName { get; set; }
        public string ContactLastName { get; set; }
        public string ContactEmail { get; set; }
        public string ContactPhone { get; set; }

        /// <summary>True when the order OWNER'S ACCOUNT has no email address on file (a no-email
        /// cash customer, or a generated no-email.invalid placeholder). ContactEmail above is
        /// FROZEN on the order at booking time and can hold a perfectly real-looking address
        /// while this is true - that disagreement is exactly what made admins read a skipped
        /// send as a bug, so the panel warns off this flag rather than off ContactEmail.
        /// False when the User navigation wasn't Included: unknown is not "has no email".</summary>
        public bool CustomerHasNoAccountEmail { get; set; }

        /// <summary>The owner's real account email, or null when the account has none / the User
        /// wasn't loaded. Shown by the admin panel only when it DIFFERS from ContactEmail.</summary>
        public string? CustomerAccountEmail { get; set; }

        /// <summary>Where a payment reminder / updated-payment mail for this order would actually
        /// land (NoEmailHelper.ResolveOrderNotificationEmail — order contact first, account as
        /// fallback). Null means those notifications can only go by text.</summary>
        public string? NotificationEmailTarget { get; set; }

        public string ServiceAddress { get; set; }
        public string? AptSuite { get; set; }
        public string City { get; set; }
        public string State { get; set; }
        public string ZipCode { get; set; }
        public decimal TotalDuration { get; set; }
        public int MaidsCount { get; set; }
        public int? BedroomsQuantity { get; set; }
        public int? BathroomsQuantity { get; set; }

        /// <summary>"Apartment" or "House". Null for legacy orders, for service types with no
        /// levels service, and for custom pricing - every consumer must treat null as "do not
        /// render a property-type field" rather than as an empty value.</summary>
        public string? PropertyType { get; set; }

        /// <summary>Levels being cleaned, for a house. Null whenever PropertyType is not "House".
        /// Display only; the charge lives on the levels OrderService line.</summary>
        public int? LevelsQuantity { get; set; }

        public bool IsPaid { get; set; }
        public DateTime? PaidAt { get; set; }
        /// <summary>True when an admin created this order through create-for-user rather than
        /// the customer booking it themselves. Drives the payment page's consent gate.</summary>
        public bool BookedByAdmin { get; set; }
        /// <summary>When the payer accepted the SMS / cancellation-fee / terms consents on the
        /// payment page. Null = not yet — an admin-created order can't be paid until it is set.</summary>
        public DateTime? PaymentConsentAcceptedAt { get; set; }
        // Phase 1 manual payment tracking — string form of the enum so frontends don't have
        // to know the numeric values. Reference / Notes are admin-visible audit fields.
        public string PaymentMethod { get; set; } = "Normal";
        public string? PaymentReference { get; set; }
        public string? PaymentNotes { get; set; }
        /// <summary>Sum of unpaid additional payments created by order updates.</summary>
        public decimal PendingUpdateAmount { get; set; }
        /// <summary>Latest unpaid update-history id (if any).</summary>
        public int? PendingUpdateHistoryId { get; set; }
        public decimal InitialSubTotal { get; set; }
        public decimal InitialTax { get; set; }
        public decimal InitialTips { get; set; }
        public decimal InitialCompanyDevelopmentTips { get; set; }
        public decimal InitialTotal { get; set; }
        public decimal CleanerHourlyRate { get; set; }
        public decimal CleanerTotalSalary { get; set; }
        public bool HasCleanersService { get; set; }
        public string? CancellationReason { get; set; }
        public bool IsLateCancellation { get; set; }
        public List<OrderServiceDto> Services { get; set; } = new List<OrderServiceDto>();
        public List<OrderExtraServiceDto> ExtraServices { get; set; } = new List<OrderExtraServiceDto>();

        // Admin currently assigned to this order (for the "By: F. LastName" pill).
        // Null when no admin has been set. AssignedAdminDisplayName is the pre-formatted
        // pill label ("F. LastName") so the frontend doesn't have to replicate the rule.
        public int? AssignedAdminId { get; set; }
        public string? AssignedAdminFirstName { get; set; }
        public string? AssignedAdminLastName { get; set; }
        public string? AssignedAdminDisplayName { get; set; }

        // Marketing attribution (admin "Origin" line). First touch = how they first found us;
        // converting = the session that produced this order (shown only when it differs). Both null
        // for legacy/unknown; first-touch channel is "Phone/Unknown" for admin-booked orders.
        public string? AcquisitionChannel { get; set; }
        public string? AcquisitionSource { get; set; }
        public string? AcquisitionMedium { get; set; }
        public string? AcquisitionCampaign { get; set; }
        public string? ConvertingChannel { get; set; }
        public string? ConvertingSource { get; set; }
        public string? ConvertingMedium { get; set; }
        public string? ConvertingCampaign { get; set; }

        // Owner's admin-only problem flag ("None" | "Yellow" | "Red"), derived from User.Flag.
        // Lets the order detail panel show/set the flag. Internal only.
        public string Flag { get; set; } = "None";
        public string? FlagReason { get; set; }
    }

    public class OrderServiceDto
    {
        public int Id { get; set; }
        public int ServiceId { get; set; }
        public string ServiceName { get; set; }

        /// <summary>
        /// Stable identifier for the catalogue row ("bedrooms", "sqft", "levels"...). Exposed so
        /// a client can recognise a line without matching on the admin-editable display Name -
        /// the pending-edit diff needs to find the levels row to show a level change.
        /// </summary>
        public string? ServiceKey { get; set; }
        public int Quantity { get; set; }
        public decimal Cost { get; set; }
        public decimal Duration { get; set; }
        public decimal PriceMultiplier { get; set; }
    }

    public class OrderExtraServiceDto
    {
        public int Id { get; set; }
        public int ExtraServiceId { get; set; }
        public string ExtraServiceName { get; set; }
        public int Quantity { get; set; }
        public decimal Hours { get; set; }
        public decimal Cost { get; set; }
        public decimal Duration { get; set; }
    }

    public class UpdateOrderDto
    {
        public DateTime ServiceDate { get; set; }
        public string ServiceTime { get; set; }
        public int MaidsCount { get; set; }
        public decimal TotalDuration { get; set; }
        public int? BedroomsQuantity { get; set; }
        public int? BathroomsQuantity { get; set; }

        /// <summary>"Apartment" or "House"; null leaves the order's existing value alone only in
        /// the sense that null normalizes to null. Normalized by PropertyDetailsHelper. Sending
        /// anything other than "House" clears the level count server-side, so a customer cannot
        /// keep a stair charge on an order they have re-declared as an apartment.</summary>
        [StringLength(20)]
        public string? PropertyType { get; set; }

        /// <summary>
        /// Levels for a house on a service type with NO priced levels service - informational
        /// only. Ignored whenever a priced levels line is present.
        /// </summary>
        public int? LevelsQuantity { get; set; }

        // Capped to match the Order.EntryMethod column (500) so an edit can't fail the UPDATE.
        [StringLength(500)]
        public string EntryMethod { get; set; }
        public string? SpecialInstructions { get; set; }
        // Same reason — Order.FloorTypes is varchar(300), Order.FloorTypeOther is
        // varchar(100). An overflow here fails the UPDATE after the additional-amount
        // payment intent has already charged the card.
        [StringLength(300)]
        public string? FloorTypes { get; set; }
        [StringLength(100)]
        public string? FloorTypeOther { get; set; }
        public string ContactFirstName { get; set; }
        public string ContactLastName { get; set; }
        public string ContactEmail { get; set; }
        public string ContactPhone { get; set; }
        public string ServiceAddress { get; set; }
        public string? AptSuite { get; set; }
        public string City { get; set; }
        public string State { get; set; }
        public string ZipCode { get; set; }
        public List<BookingServiceDto> Services { get; set; } = new List<BookingServiceDto>();
        public List<BookingExtraServiceDto> ExtraServices { get; set; } = new List<BookingExtraServiceDto>();
        public decimal Tips { get; set; }
        // CompanyDevelopmentTips is deliberately absent: the field is retired and no client may
        // set it. UpdateOrder leaves whatever a legacy order already stores untouched.
        /// <summary>Recalculated discount when subtotal changes (e.g. order edit). If provided, used instead of existing order discount.</summary>
        public decimal? DiscountAmount { get; set; }
        /// <summary>Recalculated subscription discount when subtotal changes. If provided, used instead of existing.</summary>
        public decimal? SubscriptionDiscountAmount { get; set; }
        /// <summary>Recalculated loyalty discount when subtotal changes (scaled proportionally on edit).
        /// If provided, used instead of existing. Frontend computes via ratio of new vs old subTotal so
        /// the historical percentage snapshot still reads true after the edit.</summary>
        public decimal? LoyaltyDiscountAmount { get; set; }
    }

    public class OrderUpdatePaymentDto
    {
        public int OrderId { get; set; }
        public decimal AdditionalAmount { get; set; }
        public int? UpdateHistoryId { get; set; }
        public string PaymentIntentId { get; set; }
        public string PaymentClientSecret { get; set; }
    }

    public class ConfirmUpdatePaymentDto
    {
        public string PaymentIntentId { get; set; }
        public UpdateOrderDto UpdateOrderData { get; set; }
    }

    public class ConfirmPendingUpdatePaymentDto
    {
        public string PaymentIntentId { get; set; }
    }

    // SuperAdmin-only: change the display label of an existing custom ("Pre-Arranged") order.
    public class UpdateOrderCustomServiceNameDto
    {
        [StringLength(100)]
        public string? CustomServiceDisplayName { get; set; }
    }

    public class OrderCustomServiceNameResultDto
    {
        public int OrderId { get; set; }
        // Bare label (no "Cleaning"), e.g. "Deep". Null when cleared.
        public string? CustomServiceDisplayName { get; set; }
        // Effective customer/cleaner-facing name, e.g. "Deep Cleaning".
        public string ServiceTypeName { get; set; } = string.Empty;
    }

    // SuperAdmin-only: mark/unmark an order as admin-booked. Exists to backfill orders
    // created before BookedByAdminUserId (2026-07); new orders are stamped automatically.
    public class UpdateOrderBookedByAdminDto
    {
        public bool BookedByAdmin { get; set; }
    }

    public class OrderBookedByAdminResultDto
    {
        public int OrderId { get; set; }
        // Effective flag after the change (via OrderBookedByAdminExtensions) — legacy
        // orders with a creation-time manual-payment stamp stay true even when cleared.
        public bool BookedByAdmin { get; set; }
    }

    public class OrderListDto
    {
        public int Id { get; set; }
        public int UserId { get; set; } 
        public string ContactEmail { get; set; }  
        public string ContactFirstName { get; set; }  
        public string ContactLastName { get; set; }  
        public string ServiceTypeName { get; set; }
        public bool IsCustomServiceType { get; set; }
        // Bare admin-chosen label for custom orders (no "Cleaning" suffix), e.g. "Deep". Null otherwise.
        // The admin orders table shows this directly; legacy custom orders (null) fall back to "Arranged".
        public string? CustomServiceDisplayName { get; set; }
        public DateTime ServiceDate { get; set; }
        public TimeSpan ServiceTime { get; set; }
        public string Status { get; set; }
        public decimal Total { get; set; }
        public string ServiceAddress { get; set; }
        public string City { get; set; }
        public DateTime OrderDate { get; set; }
        public decimal TotalDuration { get; set; }
        // Surfaced on the list DTO so the admin table can render the staffing-review
        // badge (per-cleaner load > 6h) without fetching full details. HasCleanersService
        // is true for cleaner+hours service types (TotalDuration is per-cleaner there),
        // which the badge skips — cleaners × hours is already explicit for those.
        public int MaidsCount { get; set; }
        public bool HasCleanersService { get; set; }
        public decimal Tips { get; set; }
        public decimal CompanyDevelopmentTips { get; set; }
        public bool IsPaid { get; set; }
        public DateTime? PaidAt { get; set; }

        /// <summary>
        /// Sum of unpaid additional payments created by order updates (e.g. admin increased total after initial payment).
        /// </summary>
        public decimal PendingUpdateAmount { get; set; }

        /// <summary>
        /// Convenience: latest unpaid update-history id (if any). Useful to create a payment intent.
        /// </summary>
        public int? PendingUpdateHistoryId { get; set; }

        public string? CancellationReason { get; set; }
        public bool IsLateCancellation { get; set; }
        public int PointsRedeemed { get; set; }
        public decimal PointsRedeemedDiscount { get; set; }
        public decimal RewardBalanceUsed { get; set; }
        public int PointsEarned { get; set; }
        // Loyalty Discount snapshot — exposed on the list DTO so admin order tables can show
        // whether a given order consumed a loyalty discount without needing the full OrderDto.
        public decimal LoyaltyDiscountAmount { get; set; }
        public decimal LoyaltyDiscountPercentage { get; set; }

        // Phase 1 manual payment tracking — surfaced on the list DTO so the admin orders table
        // can show the "DoneM" badge + payment-method filter without fetching full details.
        public string PaymentMethod { get; set; } = "Normal";
        public string? PaymentReference { get; set; }
        public string? PaymentNotes { get; set; }

        // Assigned admin (drives the order-details pill and admin-bonus counts).
        public int? AssignedAdminId { get; set; }
        public string? AssignedAdminFirstName { get; set; }
        public string? AssignedAdminLastName { get; set; }
        public string? AssignedAdminDisplayName { get; set; }

        // True when an admin created the order (create-for-user flow) rather than the
        // customer booking it themselves. Drives the "booked by" filter in the admin table.
        public bool BookedByAdmin { get; set; }

        // Owner's admin-only problem flag ("None" | "Yellow" | "Red"), derived from User.Flag.
        // Drives the row tint in the admin orders table. Reason shown in the row tooltip.
        public string Flag { get; set; } = "None";
        public string? FlagReason { get; set; }

        // Money already refunded on this order. The admin table's header cards subtract it from
        // their revenue totals, so a retained cancellation fee still shows as income.
        public decimal TotalRefundedAmount { get; set; }

        // Soft-hidden from the default list view. Rows only appear when "Show hidden orders" is
        // ticked, and are dimmed with a HIDDEN badge so they can't be mistaken for live orders.
        public bool IsHidden { get; set; }
    }

    public class CancelOrderDto
    {
        public string Reason { get; set; }
    }
}