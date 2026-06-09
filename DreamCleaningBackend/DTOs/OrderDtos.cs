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
        public string ServiceTypeName { get; set; }
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
        public string ServiceAddress { get; set; }
        public string? AptSuite { get; set; }
        public string City { get; set; }
        public string State { get; set; }
        public string ZipCode { get; set; }
        public decimal TotalDuration { get; set; }
        public int MaidsCount { get; set; }
        public int? BedroomsQuantity { get; set; }
        public int? BathroomsQuantity { get; set; }
        public bool IsPaid { get; set; }
        public DateTime? PaidAt { get; set; }
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
    }

    public class OrderServiceDto
    {
        public int Id { get; set; }
        public int ServiceId { get; set; }
        public string ServiceName { get; set; }
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
        // Capped to match the Order.EntryMethod column (500) so an edit can't fail the UPDATE.
        [StringLength(500)]
        public string EntryMethod { get; set; }
        public string? SpecialInstructions { get; set; }
        public string? FloorTypes { get; set; }
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
        public decimal CompanyDevelopmentTips { get; set; }
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

    public class OrderListDto
    {
        public int Id { get; set; }
        public int UserId { get; set; } 
        public string ContactEmail { get; set; }  
        public string ContactFirstName { get; set; }  
        public string ContactLastName { get; set; }  
        public string ServiceTypeName { get; set; }
        public bool IsCustomServiceType { get; set; }
        public DateTime ServiceDate { get; set; }
        public TimeSpan ServiceTime { get; set; }
        public string Status { get; set; }
        public decimal Total { get; set; }
        public string ServiceAddress { get; set; }
        public DateTime OrderDate { get; set; }
        public decimal TotalDuration { get; set; }
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
    }

    public class CancelOrderDto
    {
        public string Reason { get; set; }
    }
}