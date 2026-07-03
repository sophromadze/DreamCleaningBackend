using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.DTOs
{
    // Service Type DTOs
    public class ServiceTypeDto
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public decimal BasePrice { get; set; }
        public string? Description { get; set; }
        public bool IsActive { get; set; }
        public int DisplayOrder { get; set; }
        public bool HasPoll { get; set; }
        public bool IsCustom { get; set; }
        public decimal TimeDuration { get; set; }
        public List<ServiceDto> Services { get; set; } = new List<ServiceDto>();
        public List<ExtraServiceDto> ExtraServices { get; set; } = new List<ExtraServiceDto>();
    }

    public class ServiceDto
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string ServiceKey { get; set; }
        public decimal Cost { get; set; }
        public decimal TimeDuration { get; set; }
        public int ServiceTypeId { get; set; }
        public string InputType { get; set; }
        public int? MinValue { get; set; }
        public int? MaxValue { get; set; }
        public int? StepValue { get; set; }
        public bool IsRangeInput { get; set; }
        public string? Unit { get; set; }
        public string? ServiceRelationType { get; set; }
        public bool IsActive { get; set; }
        public int DisplayOrder { get; set; }
    }

    public class ExtraServiceDto
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string? Description { get; set; }
        public decimal Price { get; set; }
        public decimal Duration { get; set; }
        public string? Icon { get; set; }
        public bool HasQuantity { get; set; }
        public bool HasHours { get; set; }
        public bool IsDeepCleaning { get; set; }
        public bool IsSuperDeepCleaning { get; set; }
        public bool IsSameDayService { get; set; }
        public decimal PriceMultiplier { get; set; }
        public bool IsAvailableForAll { get; set; }
        public bool IsActive { get; set; }
        public int DisplayOrder { get; set; }
    }
    public class CreateServiceTypeDto
    {
        [Required]
        public string Name { get; set; }
        [Required]
        public decimal BasePrice { get; set; }
        public string? Description { get; set; }
        public int DisplayOrder { get; set; }
        public bool HasPoll { get; set; } = false;
        public bool IsCustom { get; set; }
        [Required]
        public decimal TimeDuration { get; set; } = 90;
    }

    public class UpdateServiceTypeDto
    {
        [Required]
        public string Name { get; set; }
        [Required]
        public decimal BasePrice { get; set; }
        public string? Description { get; set; }
        public bool HasPoll { get; set; } = false;
        public bool IsCustom { get; set; }
        public int DisplayOrder { get; set; }
        [Required]
        public decimal TimeDuration { get; set; }
    }

    // Service DTOs
    public class CreateServiceDto
    {
        [Required]
        public string Name { get; set; }
        [Required]
        public string ServiceKey { get; set; }
        [Required]
        public decimal Cost { get; set; }
        [Required]
        public decimal TimeDuration { get; set; }
        [Required]
        public int ServiceTypeId { get; set; }
        [Required]
        public string InputType { get; set; } = "dropdown";
        public int? MinValue { get; set; }
        public int? MaxValue { get; set; }
        public int? StepValue { get; set; }
        public bool IsRangeInput { get; set; } = false;
        public string? Unit { get; set; }
        public string? ServiceRelationType { get; set; } 
        public int DisplayOrder { get; set; }
    }

    public class UpdateServiceDto
    {
        [Required]
        public string Name { get; set; }
        [Required]
        public string ServiceKey { get; set; }
        [Required]
        public decimal Cost { get; set; }
        [Required]
        public decimal TimeDuration { get; set; }
        [Required]
        public int ServiceTypeId { get; set; }
        [Required]
        public string InputType { get; set; }
        public int? MinValue { get; set; }
        public int? MaxValue { get; set; }
        public int? StepValue { get; set; }
        public bool IsRangeInput { get; set; }
        public string? Unit { get; set; }
        public string? ServiceRelationType { get; set; } 
        public int DisplayOrder { get; set; }
    }


    // Extra Service DTOs
    public class CreateExtraServiceDto
    {
        [Required]
        public string Name { get; set; }
        public string? Description { get; set; }
        [Required]
        public decimal Price { get; set; }
        [Required]
        public decimal Duration { get; set; }
        public string? Icon { get; set; }
        public bool HasQuantity { get; set; }
        public bool HasHours { get; set; }
        public bool IsDeepCleaning { get; set; }
        public bool IsSuperDeepCleaning { get; set; }
        public bool IsSameDayService { get; set; }
        public decimal PriceMultiplier { get; set; } = 1.0m;
        public int? ServiceTypeId { get; set; }
        public bool IsAvailableForAll { get; set; } = true;
        public int DisplayOrder { get; set; }
    }

    public class UpdateExtraServiceDto
    {
        [Required]
        public string Name { get; set; }
        public string? Description { get; set; }
        [Required]
        public decimal Price { get; set; }
        [Required]
        public decimal Duration { get; set; }
        public string? Icon { get; set; }
        public bool HasQuantity { get; set; }
        public bool HasHours { get; set; }
        public bool IsDeepCleaning { get; set; }
        public bool IsSuperDeepCleaning { get; set; }
        public bool IsSameDayService { get; set; }
        public decimal PriceMultiplier { get; set; }
        public int? ServiceTypeId { get; set; }
        public bool IsAvailableForAll { get; set; }
        public int DisplayOrder { get; set; }
    }

    // Subscription DTOs
    public class CreateSubscriptionDto
    {
        [Required]
        public string Name { get; set; }
        public string? Description { get; set; }
        [Required]
        public decimal DiscountPercentage { get; set; }
        [Required]
        public int SubscriptionDays { get; set; }
        public int DisplayOrder { get; set; }
    }

    public class UpdateSubscriptionDto
    {
        [Required]
        public string Name { get; set; }
        public string? Description { get; set; }
        [Required]
        public decimal DiscountPercentage { get; set; }
        [Required]
        public int SubscriptionDays { get; set; }
        public int DisplayOrder { get; set; }
    }

    // Promo Code DTOs
    public class PromoCodeDto
    {
        public int Id { get; set; }
        public string Code { get; set; }
        public string? Description { get; set; }
        public bool IsPercentage { get; set; }
        public decimal DiscountValue { get; set; }
        public int? MaxUsageCount { get; set; }
        public int CurrentUsageCount { get; set; }
        public int? MaxUsagePerUser { get; set; }
        public DateTime? ValidFrom { get; set; }
        public DateTime? ValidTo { get; set; }
        public decimal? MinimumOrderAmount { get; set; }
        public bool IsActive { get; set; }
    }

    public class CreatePromoCodeDto : IValidatableObject
    {
        [Required]
        [StringLength(50)]
        public string Code { get; set; }

        [StringLength(200)]
        public string? Description { get; set; }

        public bool IsPercentage { get; set; } = true;

        [Required]
        [Range(0.01, double.MaxValue, ErrorMessage = "Discount value must be greater than 0")]
        public decimal DiscountValue { get; set; }

        [Range(1, int.MaxValue, ErrorMessage = "Max usage count must be at least 1")]
        public int? MaxUsageCount { get; set; }

        [Range(1, int.MaxValue, ErrorMessage = "Max usage per user must be at least 1")]
        public int? MaxUsagePerUser { get; set; }

        public DateTime? ValidFrom { get; set; }
        public DateTime? ValidTo { get; set; }

        [Range(0.01, double.MaxValue, ErrorMessage = "Minimum order amount must be greater than 0")]
        public decimal? MinimumOrderAmount { get; set; }

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            // Validate percentage discount is not over 100%
            if (IsPercentage && DiscountValue > 100)
            {
                yield return new ValidationResult(
                    "Percentage discount cannot be greater than 100%",
                    new[] { nameof(DiscountValue) }
                );
            }

            // Validate date range
            if (ValidFrom.HasValue && ValidTo.HasValue && ValidFrom.Value > ValidTo.Value)
            {
                yield return new ValidationResult(
                    "Valid From date must be before Valid To date",
                    new[] { nameof(ValidFrom), nameof(ValidTo) }
                );
            }

            // Validate that MaxUsagePerUser is not greater than MaxUsageCount
            if (MaxUsagePerUser.HasValue && MaxUsageCount.HasValue && MaxUsagePerUser.Value > MaxUsageCount.Value)
            {
                yield return new ValidationResult(
                    "Max usage per user cannot be greater than total max usage count",
                    new[] { nameof(MaxUsagePerUser) }
                );
            }
        }
    }

    public class UpdatePromoCodeDto
    {
        public string? Description { get; set; }
        public bool IsPercentage { get; set; }
        [Required]
        public decimal DiscountValue { get; set; }
        public int? MaxUsageCount { get; set; }
        public int? MaxUsagePerUser { get; set; }
        public DateTime? ValidFrom { get; set; }
        public DateTime? ValidTo { get; set; }
        public decimal? MinimumOrderAmount { get; set; }
        public bool IsActive { get; set; }
    }

    public class GiftCardAdminDto
    {
        public int Id { get; set; }
        public string Code { get; set; }
        public decimal OriginalAmount { get; set; }
        public decimal CurrentBalance { get; set; }
        public string RecipientName { get; set; }
        public string RecipientEmail { get; set; }
        public string SenderName { get; set; }
        public string SenderEmail { get; set; }
        public string? Message { get; set; }
        public bool IsActive { get; set; }
        public bool IsPaid { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? PaidAt { get; set; }
        public string PurchasedByUserName { get; set; }

        // Calculated fields
        public decimal TotalAmountUsed { get; set; }
        public int TimesUsed { get; set; }
        public DateTime? LastUsedAt { get; set; }
        public bool IsFullyUsed => CurrentBalance <= 0;

        // Usage history
        public List<GiftCardUsageDto> Usages { get; set; } = new List<GiftCardUsageDto>();
    }

    /// <summary>Admin/SuperAdmin: register a new customer manually (e.g. when they call and don't register themselves).</summary>
    public class AdminRegisterUserDto
    {
        [Required]
        public string FirstName { get; set; }
        [Required]
        public string LastName { get; set; }
        /// <summary>Required unless NoEmail is true (validated in the controller — a placeholder is generated then).</summary>
        [EmailAddress]
        public string? Email { get; set; }
        public string? Phone { get; set; }
        /// <summary>Customer has no email at all (cash customer). Phone becomes required; account cannot log in.</summary>
        public bool NoEmail { get; set; }
    }

    // ── SuperAdmin order transfer ──

    public class TransferOrderRequestDto
    {
        [Required]
        public int TargetUserId { get; set; }
        [StringLength(500)]
        public string? Notes { get; set; }
    }

    public class OrderTransferDto
    {
        public int Id { get; set; }
        public int OrderId { get; set; }
        public int FromUserId { get; set; }
        public string FromUserName { get; set; } = "";
        public int ToUserId { get; set; }
        public string ToUserName { get; set; } = "";
        public int TransferredByUserId { get; set; }
        public string TransferredByName { get; set; } = "";
        public string? Notes { get; set; }
        public DateTime CreatedAt { get; set; }
        public bool IsUndone { get; set; }
        public DateTime? UndoneAt { get; set; }
        public string? UndoneByName { get; set; }
        public int PointsMoved { get; set; }
        public decimal SpentAmountMoved { get; set; }
        public int PhotosMoved { get; set; }
    }

    /// <summary>SuperAdmin users-list export request. Columns is the set of column keys to include;
    /// an empty/missing list exports all columns. Recognized keys: userId, fullName, phone, email,
    /// lastServiceType, lastServiceAt, lastAddress, lastBorough, lastZip, lastBedsBaths,
    /// lastSquareFeet, totalSpent.</summary>
    public class UsersExportRequestDto
    {
        public List<string>? Columns { get; set; }
    }

    // User Management DTOs
    public class UserAdminDto
    {
        public int Id { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string Email { get; set; }
        /// <summary>True for admin-created cash customers with no email; Email is blanked in responses.</summary>
        public bool IsNoEmailUser { get; set; }
        public string? Phone { get; set; }
        public string Role { get; set; }
        public string? AuthProvider { get; set; }
        public string? SubscriptionName { get; set; }
        public bool FirstTimeOrder { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
        /// <summary>Restricted-admin-page keys this (Admin-role) user has been granted read-only access to.</summary>
        public List<string> ViewablePages { get; set; } = new();
        /// <summary>When true, user can receive emails and (in future) SMS from the company.</summary>
        public bool CanReceiveCommunications { get; set; }
        public bool CanReceiveEmails { get; set; }
        public bool CanReceiveMessages { get; set; }
        /// <summary>Admin-only notes about this user. Not visible to the user.</summary>
        public string? AdminNotes { get; set; }
        /// <summary>True if user has an active SignalR connection (on site).</summary>
        public bool IsOnline { get; set; }

        // ── New customer-care snapshot fields ──
        /// <summary>Date of the user's most recent non-cancelled order (service date).</summary>
        public DateTime? LastCleaningDate { get; set; }
        /// <summary>Service type name of the user's most recent non-cancelled order.</summary>
        public string? LastCleaningServiceType { get; set; }
        /// <summary>Bedrooms quantity from the user's most recent order, if recorded.</summary>
        public int? LastBedrooms { get; set; }
        /// <summary>Bathrooms quantity from the user's most recent order, if recorded.</summary>
        public int? LastBathrooms { get; set; }
        /// <summary>Total number of non-cancelled orders this user has placed.</summary>
        public int TotalOrdersCount { get; set; }
    }

    // ── Customer-care notes (multi-row) ──

    public class UserNoteDto
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public string Type { get; set; } = "General";
        public string Content { get; set; } = string.Empty;
        public int? CreatedByAdminId { get; set; }
        public string? CreatedByAdminName { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }

    public class CreateUserNoteDto
    {
        [Required]
        [StringLength(20)]
        public string Type { get; set; } = "General";

        [Required]
        [StringLength(4000)]
        public string Content { get; set; } = string.Empty;
    }

    public class UpdateUserNoteDto
    {
        [Required]
        [StringLength(4000)]
        public string Content { get; set; } = string.Empty;
    }

    // ── Cleaning photos ──

    public class UserCleaningPhotoDto
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public int? OrderId { get; set; }
        public string PhotoUrl { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public string? UploadedByAdminName { get; set; }
        public string? Caption { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class UserCleaningPhotosByOrderDto
    {
        public int? OrderId { get; set; }
        public DateTime? OrderServiceDate { get; set; }
        public string? OrderServiceTypeName { get; set; }
        public List<UserCleaningPhotoDto> Photos { get; set; } = new();
    }

    public class UserCleaningPhotoUploadResultDto
    {
        public UserCleaningPhotoDto Photo { get; set; } = new();
        /// <summary>Photos that were pruned because they belonged to orders older than the most recent two.</summary>
        public int PrunedCount { get; set; }
    }

    public class UpdateUserRoleDto
    {
        [Required]
        public string Role { get; set; }
    }

    /// <summary>Restricted-admin-page keys to grant a regular Admin read-only access to.</summary>
    public class UpdateViewablePagesDto
    {
        public List<string> Pages { get; set; } = new();
    }

    public class UpdateUserStatusDto
    {
        [Required]
        public bool IsActive { get; set; }
    }

    // Copy Service/ExtraService DTOs
    public class CopyServiceDto
    {
        [Required]
        public int SourceServiceId { get; set; }
        [Required]
        public int TargetServiceTypeId { get; set; }
    }

    public class CopyExtraServiceDto
    {
        [Required]
        public int SourceExtraServiceId { get; set; }
        [Required]
        public int TargetServiceTypeId { get; set; }
    }

    public class UpdateOrderStatusDto
    {
        [Required]
        public string Status { get; set; }

        // Manual payment tracking (Phase 1). Optional — when Status == "Done" and these are
        // provided, the order's PaymentMethod / Reference / Notes are updated. When omitted
        // the existing values on the order are preserved (no clobber). Parsed case-insensitively.
        public string? PaymentMethod { get; set; }
        public string? PaymentReference { get; set; }
        public string? PaymentNotes { get; set; }
    }

    // SuperAdmin-only: record a non-Stripe payment for a single additional-amount (order-edit) row.
    // Used when the order top-up was collected outside Stripe (e.g. Zelle/Cash/Check). PaymentMethod
    // must be a non-Normal value; parsed case-insensitively.
    public class RecordManualAdditionalPaymentDto
    {
        [Required]
        public string PaymentMethod { get; set; }
        public string? PaymentReference { get; set; }
        public string? PaymentNotes { get; set; }
    }

    // SuperAdmin-only: switch an existing order between the Stripe (Normal) flow and a manual
    // payment method (Cash/Zelle/Check/Other) from the admin order panel — e.g. the order was
    // created expecting Stripe but the customer decided to pay cash. Parsed case-insensitively.
    // StringLength caps mirror Order.PaymentReference / Order.PaymentNotes.
    public class UpdateOrderPaymentMethodDto
    {
        [Required]
        public string PaymentMethod { get; set; }
        [StringLength(255)]
        public string? PaymentReference { get; set; }
        [StringLength(1000)]
        public string? PaymentNotes { get; set; }
    }

    // SuperAdmin-only: full user edit (all changes are audit-logged)
    public class SuperAdminUpdateUserDto
    {
        [Required]
        [StringLength(50)]
        public string FirstName { get; set; }

        [Required]
        [StringLength(50)]
        public string LastName { get; set; }

        // Optional so a no-email (cash) account can be edited with the field left blank;
        // the controller only applies a change when a real address is provided.
        [EmailAddress]
        [StringLength(100)]
        public string? Email { get; set; }

        [StringLength(20)]
        public string? Phone { get; set; }

        [Required]
        public string Role { get; set; }

        public bool IsActive { get; set; }

        public bool FirstTimeOrder { get; set; }

        /// <summary>When true, user can receive emails and (in future) SMS from the company.</summary>
        public bool CanReceiveCommunications { get; set; }
        public bool CanReceiveEmails { get; set; }
        public bool CanReceiveMessages { get; set; }
    }

    /// <summary>Admin/SuperAdmin: update only the communication preference. Requires canUpdate.</summary>
    public class CommunicationPreferenceDto
    {
        public bool CanReceiveCommunications { get; set; }
        public bool? CanReceiveEmails { get; set; }
        public bool? CanReceiveMessages { get; set; }
    }

    /// <summary>Admin/SuperAdmin: update admin notes for a user. Requires canUpdate.</summary>
    public class UpdateUserAdminNotesDto
    {
        [StringLength(2000)]
        public string? AdminNotes { get; set; }
    }

    // SuperAdmin-only: full order edit (all changes are audit-logged)
    public class SuperAdminUpdateOrderDto
    {
        public string? ContactFirstName { get; set; }
        public string? ContactLastName { get; set; }
        public string? ContactEmail { get; set; }
        public string? ContactPhone { get; set; }
        public string? ServiceAddress { get; set; }
        public string? AptSuite { get; set; }
        public string? City { get; set; }
        public string? State { get; set; }
        public string? ZipCode { get; set; }
        public DateTime? ServiceDate { get; set; }
        public string? ServiceTime { get; set; }
        public int? MaidsCount { get; set; }
        public decimal? TotalDuration { get; set; }
        public int? BedroomsQuantity { get; set; }
        public int? BathroomsQuantity { get; set; }
        public string? EntryMethod { get; set; }
        public string? SpecialInstructions { get; set; }
        public string? FloorTypes { get; set; }
        public string? FloorTypeOther { get; set; }
        public decimal? Tips { get; set; }
        public decimal? CompanyDevelopmentTips { get; set; }
        public string? Status { get; set; }
        public string? CancellationReason { get; set; }
        public decimal? SubTotal { get; set; }
        public decimal? Tax { get; set; }
        public decimal? Total { get; set; }
        public decimal? DiscountAmount { get; set; }
        public decimal? SubscriptionDiscountAmount { get; set; }
        /// <summary>Recalculated loyalty discount on subtotal change (scaled proportionally on
        /// edit). Frontend computes via ratio of new vs old subTotal so the historical
        /// LoyaltyDiscountPercentage snapshot still reads true after the edit. When omitted,
        /// the existing order.LoyaltyDiscountAmount is preserved.</summary>
        public decimal? LoyaltyDiscountAmount { get; set; }
        public decimal? CleanerHourlyRate { get; set; }
        public decimal? CleanerTotalSalary { get; set; }
        /// <summary>Display label for custom ("Pre-Arranged") orders. Empty string clears it
        /// (back to "Arranged"); null means "no change". Ignored for non-custom service types.</summary>
        public string? CustomServiceDisplayName { get; set; }
        public List<SuperAdminOrderServiceUpdateDto>? Services { get; set; }
        public List<SuperAdminOrderExtraServiceUpdateDto>? ExtraServices { get; set; }
    }

    public class SuperAdminOrderServiceUpdateDto
    {
        public int OrderServiceId { get; set; }
        public int Quantity { get; set; }
        public decimal Cost { get; set; }
    }

    public class SuperAdminOrderExtraServiceUpdateDto
    {
        /// <summary>Existing row: set to OrderExtraService.Id. New row: set to 0.</summary>
        public int OrderExtraServiceId { get; set; }
        /// <summary>Required when adding a new extra (OrderExtraServiceId == 0).</summary>
        public int? ExtraServiceId { get; set; }
        public int Quantity { get; set; }
        public decimal Hours { get; set; }
        public decimal Cost { get; set; }
    }

    /// <summary>List item for pending order edits (SuperAdmin view).</summary>
    public class PendingOrderEditListDto
    {
        public int Id { get; set; }
        public int OrderId { get; set; }
        public string OrderSummary { get; set; } = ""; // e.g. "Order #123 - John Doe - 2025-03-10"
        public int RequestedByUserId { get; set; }
        public string RequestedByName { get; set; } = "";
        public DateTime RequestedAt { get; set; }
        public string Status { get; set; } = "Pending";
    }

    /// <summary>Single pending edit with current order state and proposed changes (for diff/approve).</summary>
    public class PendingOrderEditDetailDto
    {
        public int Id { get; set; }
        public int OrderId { get; set; }
        public int RequestedByUserId { get; set; }
        public string RequestedByName { get; set; } = "";
        public DateTime RequestedAt { get; set; }
        public string Status { get; set; } = "Pending";
        public OrderDto? CurrentOrder { get; set; }
        public SuperAdminUpdateOrderDto? ProposedChanges { get; set; }
    }

    public class RejectPendingOrderEditDto
    {
        [StringLength(500)]
        public string? RejectReason { get; set; }
    }

    /// <summary>Response DTO for order statistics (SuperAdmin only).</summary>
    /// <remarks>
    /// TotalCompanyRevenue is NET — it already subtracts TotalExpenses. The frontend's
    /// breakdown panel rebuilds the formula from these labelled components:
    ///   Subtotal − Taxes − Cleaner Salaries − Expenses = Company Revenue (net)
    /// TotalCompanyRevenueGross is the pre-expense figure for reference.
    /// </remarks>
    public class OrderStatisticsDto
    {
        public int TotalOrders { get; set; }
        public decimal TotalAmount { get; set; }
        public decimal TotalTaxes { get; set; }
        public decimal TotalTips { get; set; }
        public decimal TotalCleanersSalary { get; set; }
        // TotalExpenses is the GRAND total: table expenses + Stripe fees + admin bonuses (USD).
        public decimal TotalExpenses { get; set; }
        public decimal TotalCompanyRevenueGross { get; set; }
        public decimal TotalCompanyRevenue { get; set; }
        public ExpenseBreakdownDto? ExpensesBreakdown { get; set; }

        // ── Computed expense lines (not stored in the Expenses table) ──────────────────
        // Stripe processing fees (2.9% + $0.30 per real Stripe-charged order). Statistics-only;
        // order amounts shown to users/admins are never altered.
        public decimal StripeFees { get; set; }
        // Admin bonuses for the window, converted GEL→USD per-month at each month's locked rate.
        public decimal AdminBonusesUsd { get; set; }
        // The same bonuses in raw GEL, for reference in the breakdown panel.
        public decimal AdminBonusesGel { get; set; }
    }

    /// <summary>Daily data point for statistics chart. CompanyRevenue is NET.</summary>
    public class DailyStatisticsDto
    {
        public string Date { get; set; } = "";
        public int Orders { get; set; }
        public decimal Amount { get; set; }
        public decimal Taxes { get; set; }
        public decimal Tips { get; set; }
        public decimal CleanersSalary { get; set; }
        // Expenses here is the GRAND total for the day (table + Stripe fees + admin bonuses),
        // so summing across days reconciles with the headline TotalExpenses.
        public decimal Expenses { get; set; }
        public decimal CompanyRevenue { get; set; }
        // Itemised computed expenses for the day (already included in Expenses above).
        public decimal StripeFees { get; set; }
        public decimal AdminBonuses { get; set; }
    }
}