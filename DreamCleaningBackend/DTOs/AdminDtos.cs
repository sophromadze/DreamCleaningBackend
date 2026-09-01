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

        /// <summary>Whether this type asks apartment vs house. See ServiceType.CollectsPropertyType.</summary>
        public bool CollectsPropertyType { get; set; } = true;

        public decimal TimeDuration { get; set; }

        /// <summary>Floor for base price + services. 0 = no floor.</summary>
        public decimal MinimumPrice { get; set; }

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

        // Threshold / tier billing. All optional — a service with none of these configured
        // prices exactly as it always did.
        public bool ChargeAboveThreshold { get; set; }
        public decimal? ZeroQuantityCost { get; set; }
        public decimal? ZeroQuantityDuration { get; set; }

        /// <summary>Included allowances granted to this service. Also sent to the booking page
        /// so the frontend calculator mirrors the backend exactly.</summary>
        public List<ServiceThresholdDto> Thresholds { get; set; } = new();

        /// <summary>Marginal rate bands. Empty = flat Cost/TimeDuration.</summary>
        public List<ServiceRateTierDto> RateTiers { get; set; } = new();
    }

    public class ServiceThresholdDto
    {
        public int Id { get; set; }
        public int ServiceId { get; set; }
        public int SourceServiceId { get; set; }

        /// <summary>Convenience for the UI and the frontend calculator; never a resolution key here.</summary>
        public string? SourceServiceKey { get; set; }
        public string? SourceServiceName { get; set; }

        public int SourceQuantity { get; set; }
        public decimal IncludedQuantity { get; set; }
    }

    public class ServiceRateTierDto
    {
        public int Id { get; set; }
        public int ServiceId { get; set; }

        /// <summary>Measured ABOVE the included allowance, not in absolute units.</summary>
        public decimal FromQuantity { get; set; }
        public decimal Cost { get; set; }
        public decimal TimeDuration { get; set; }
        public int DisplayOrder { get; set; }
    }

    /// <summary>Create/update payload for one included-amount row.</summary>
    public class SaveServiceThresholdDto
    {
        [Required]
        public int SourceServiceId { get; set; }
        public int SourceQuantity { get; set; }
        public decimal IncludedQuantity { get; set; }
    }

    /// <summary>Create/update payload for one rate tier.</summary>
    public class SaveServiceRateTierDto
    {
        public decimal FromQuantity { get; set; }
        public decimal Cost { get; set; }
        public decimal TimeDuration { get; set; }
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

        /// <summary>Whether this type asks apartment vs house. Defaults true, matching the column.</summary>
        public bool CollectsPropertyType { get; set; } = true;
        [Required]
        public decimal TimeDuration { get; set; } = 90;

        /// <summary>Floor for base price + services. 0 = no floor.</summary>
        public decimal MinimumPrice { get; set; } = 0m;
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

        /// <summary>Whether this type asks apartment vs house. Defaults true, matching the column.</summary>
        public bool CollectsPropertyType { get; set; } = true;
        public int DisplayOrder { get; set; }
        [Required]
        public decimal TimeDuration { get; set; }

        /// <summary>Floor for base price + services. 0 = no floor.</summary>
        public decimal MinimumPrice { get; set; } = 0m;
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

        // Threshold / tier billing. Defaults preserve the original bill-from-zero behaviour.
        public bool ChargeAboveThreshold { get; set; } = false;
        public decimal? ZeroQuantityCost { get; set; }
        public decimal? ZeroQuantityDuration { get; set; }
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

        // Threshold / tier billing. Nested threshold and tier rows are managed through their
        // own CRUD endpoints, not through this payload.
        public bool ChargeAboveThreshold { get; set; }
        public decimal? ZeroQuantityCost { get; set; }
        public decimal? ZeroQuantityDuration { get; set; }
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
        /// <summary>
        /// Required unless NoEmail is true (validated in the controller — a placeholder is generated then).
        /// FORMAT is deliberately validated in the controller too, via EmailAddressValidator, rather
        /// than with [EmailAddress]: the attribute rejects through automatic model validation, whose
        /// ValidationProblemDetails body carries no "message" property, so the admin panel showed the
        /// bare "Http failure response ...: 400" instead of naming the missing "@". See
        /// Helpers/EmailAddressValidator.cs.
        /// </summary>
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

    /// <summary>SuperAdmin orders-list export request. Columns is the set of column keys to include
    /// (empty/missing = all). OrderIds limits the export to those orders — the admin UI passes the
    /// currently filtered rows so the file matches what's on screen; empty/missing = all orders.</summary>
    public class OrdersExportRequestDto
    {
        public List<string>? Columns { get; set; }
        public List<int>? OrderIds { get; set; }
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
        /// <summary>Avatar image (Google/Apple photo from social login, or an uploaded picture). Null = show initials.</summary>
        public string? ProfilePictureUrl { get; set; }
        public string? Phone { get; set; }
        public string Role { get; set; }
        public string? AuthProvider { get; set; }
        public string? SubscriptionName { get; set; }
        public bool FirstTimeOrder { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
        /// <summary>Restricted-admin-page keys this (Admin-role) user has been granted read-only access to.</summary>
        public List<string> ViewablePages { get; set; } = new();
        /// <summary>True when a SuperAdmin has granted this Admin direct order-edit saves (no approval step).</summary>
        public bool CanEditOrdersWithoutApproval { get; set; }
        /// <summary>"Administrator" or "Manager". Only meaningful on an Admin-role row.</summary>
        public string AdminPosition { get; set; } = nameof(Models.AdminPosition.Administrator);
        /// <summary>The Manager this Administrator reports to, if any.</summary>
        public int? ManagerId { get; set; }
        public string? ManagerName { get; set; }
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

        /// <summary>Admin-only problem-customer flag: "None" | "Yellow" | "Red".</summary>
        public string Flag { get; set; } = "None";
        /// <summary>Optional admin note on why this customer is flagged.</summary>
        public string? FlagReason { get; set; }
    }

    /// <summary>Admin sets/clears a customer's problem flag. Level is "None" | "Yellow" | "Red".</summary>
    public class SetCustomerFlagDto
    {
        public string Level { get; set; } = "None";
        public string? Reason { get; set; }
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

    /// <summary>
    /// Manager-vs-Administrator and, for an administrator, who they report to. SuperAdmin-only.
    /// ManagerId is ignored when Position is Manager — a manager does not report to a manager.
    /// </summary>
    public class UpdateAdminPositionDto
    {
        [Required]
        public string Position { get; set; } = string.Empty;
        public int? ManagerId { get; set; }
    }

    /// <summary>Restricted-admin-page keys to grant a regular Admin read-only access to.</summary>
    public class UpdateViewablePagesDto
    {
        public List<string> Pages { get; set; } = new();
    }

    /// <summary>SuperAdmin grant: may this Admin save order edits directly, skipping the approval step?</summary>
    public class UpdateOrderEditApprovalDto
    {
        public bool CanEditOrdersWithoutApproval { get; set; }
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

    /// <summary>Admin/SuperAdmin: internal free-text note on a single order. Requires canUpdate.</summary>
    public class UpdateOrderAdminNotesDto
    {
        [StringLength(2000)]
        public string? Notes { get; set; }
    }

    /// <summary>The internal note on one order, plus who last saved it.</summary>
    public class OrderAdminNoteDto
    {
        public int OrderId { get; set; }
        public string? Notes { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public string? UpdatedByName { get; set; }
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

        /// <summary>"Apartment" or "House". Null means NO CHANGE on this path, unlike the
        /// customer-facing UpdateOrderDto: an admin editing only the service date must not
        /// silently strip the property type off the order. Normalized by PropertyDetailsHelper.
        /// Serialized into PendingOrderEdit.ProposedChangesJson like every other field here, so
        /// it survives the submit/approve gap with no special handling.</summary>
        [StringLength(20)]
        public string? PropertyType { get; set; }

        /// <summary>
        /// Levels for a house on a service type with NO priced levels service - informational
        /// only, exactly like BedroomsQuantity / BathroomsQuantity above. Ignored whenever a
        /// priced levels line is present, so it can never override what was charged. Clamped
        /// server-side by PropertyDetailsHelper.
        /// </summary>
        public int? LevelsQuantity { get; set; }

        public string? EntryMethod { get; set; }
        public string? SpecialInstructions { get; set; }
        // Capped to match the Order columns (300 / 100) so an admin edit can't
        // fail the UPDATE.
        [StringLength(300)]
        public string? FloorTypes { get; set; }
        [StringLength(100)]
        public string? FloorTypeOther { get; set; }
        public decimal? Tips { get; set; }
        // CompanyDevelopmentTips is deliberately absent: the field is retired and not editable
        // by anyone. A legacy order's stored value is preserved as-is by the update path.
        public string? Status { get; set; }
        public string? CancellationReason { get; set; }
        public decimal? SubTotal { get; set; }
        // Tax and Total are NOT applied from the DTO — the update path recomputes both through
        // OrderPricingCalculator.CalculateTotals so the server stays the authority on price.
        // They are still sent so the admin's preview and the audit trail agree with the request.
        public decimal? Tax { get; set; }
        public decimal? Total { get; set; }
        /// <summary>
        /// Set when the admin typed a TOTAL rather than a subtotal: the exact tax contained in
        /// that tax-inclusive figure, so the charged total matches it to the cent instead of
        /// drifting by one (see OrderPricingCalculator.SplitTaxInclusiveAmount).
        ///
        /// Verified, not trusted: it is honoured only while <see cref="TaxOverrideBase"/> still
        /// equals the subtotal actually being taxed after this order's discounts. Anything else
        /// falls back to the ordinary rate math.
        /// </summary>
        public decimal? TaxOverride { get; set; }
        /// <summary>The discounted subtotal <see cref="TaxOverride"/> was split out of.</summary>
        public decimal? TaxOverrideBase { get; set; }
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

    /// <summary>
    /// One requested field change, captured AT SUBMIT TIME.
    ///
    /// <c>Current</c> is the value as it stood when the request was made, not as it stands now —
    /// that is the whole point. The review screen compares it against the live order and warns
    /// when they no longer agree, because approving a request built against a stale order applies
    /// a change nobody reviewed.
    ///
    /// Values are the already-FORMATTED display strings the requesting admin saw ("$289.50",
    /// "8/14/2026"), not raw values. The formatting lives in <c>computeOrderEditChanges</c> in
    /// orders.component.ts, which is deliberately the ONE implementation of an order-edit diff —
    /// a server-side mirror of it would be free to drift, and the reviewer would then read a
    /// different table from the one the requester confirmed.
    /// </summary>
    public class PendingOrderEditFieldChangeDto
    {
        /// <summary>Human-readable field label, e.g. "Service Date" or "Total".</summary>
        public string Field { get; set; } = "";
        /// <summary>Formatted value at submit time.</summary>
        public string Current { get; set; } = "";
        /// <summary>Formatted value the requester wants.</summary>
        public string Proposed { get; set; } = "";
        /// <summary>Signed numeric delta where the field is numeric, otherwise a dash.</summary>
        public string Difference { get; set; } = "";
        /// <summary>True for the pinned Total row (rendered emphasised in both review tables).</summary>
        public bool Emphasised { get; set; }
        /// <summary>
        /// Set by the SERVER when the request is read back: the live order no longer matches the
        /// submit-time <see cref="Current"/>, so the order drifted underneath the request. Never
        /// sent by the client.
        /// </summary>
        public string? LiveCurrent { get; set; }
        /// <summary>True when <see cref="LiveCurrent"/> differs from <see cref="Current"/>.</summary>
        public bool HasDrifted { get; set; }
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

        /// <summary>What was asked for, as captured at submit time. Null for legacy rows.</summary>
        public List<PendingOrderEditFieldChangeDto>? RequestedChanges { get; set; }
        /// <summary>
        /// True when the request predates the field-level payload (or its JSON is unreadable). The
        /// review screen says so plainly instead of rendering an empty table — order #296 is the
        /// row this was found on.
        /// </summary>
        public bool RequestedChangesUnavailable { get; set; }
        /// <summary>Why the requester says the change is needed.</summary>
        public string? Reason { get; set; }

        // Decision record. A decided request stays readable forever (the endpoint used to 400 on
        // anything but "Pending", so an approved request could never be inspected again).
        public int? ReviewedByUserId { get; set; }
        public string? ReviewedByName { get; set; }
        public DateTime? ReviewedAt { get; set; }
        public string? RejectReason { get; set; }
    }

    public class RejectPendingOrderEditDto
    {
        [StringLength(500)]
        public string? RejectReason { get; set; }
    }

    /// <summary>
    /// Body of <c>POST orders/{orderId}/pending-edit</c>. Wraps the update DTO so the request can
    /// also carry the submit-time field payload and the requester's reason.
    ///
    /// Both extras are OPTIONAL: a client that posts a bare SuperAdminUpdateOrderDto (as every
    /// caller did before 2026-08-31) still submits successfully and simply produces a request with
    /// no readable payload — the same shape as the legacy rows already in the table.
    /// </summary>
    public class SubmitPendingOrderEditDto
    {
        public SuperAdminUpdateOrderDto? Changes { get; set; }
        public List<PendingOrderEditFieldChangeDto>? FieldChanges { get; set; }
        [StringLength(1000)]
        public string? Reason { get; set; }
    }

    /// <summary>Response DTO for order statistics (SuperAdmin only).</summary>
    /// <remarks>
    /// UI vocabulary (the property names predate it and were left alone to avoid a rename
    /// across the API): TotalAmount is shown as "Company Revenue", TotalCompanyRevenue as
    /// "Net Income".
    ///
    /// TotalCompanyRevenue is NET — it already subtracts TotalExpenses. The frontend's
    /// breakdown panel rebuilds the formula from these labelled components:
    ///   Company Revenue − Cleaner Salaries − Expenses = Net Income
    /// TotalCompanyRevenueGross is the pre-expense figure for reference.
    ///
    /// Sales tax is deliberately NOT part of that formula. It is charged on top of the price,
    /// so it never sat inside TotalAmount — it is collected for the state and reported on its
    /// own. Every money figure here comes from OrderRevenueMath.Split.
    ///
    /// The exception is TotalTaxRetained (2026-08): sales tax charged on a payment collected
    /// OUTSIDE Stripe (Cash/Zelle/Check/Other) is not remitted, so it counts as company money
    /// and is reported INSIDE TotalAmount, with TotalTaxes carrying only the remitted part.
    /// TotalTaxes + TotalTaxRetained is the whole tax the customers were charged, and it is
    /// that SUM — not TotalTaxes alone — that is SalesTaxRate × (TotalAmount − TotalTaxRetained).
    /// </remarks>
    public class OrderStatisticsDto
    {
        public int TotalOrders { get; set; }
        /// <summary>
        /// Taxable cleaning revenue (subtotals after discounts, before tax, without tips, net of
        /// refunds) PLUS TotalTaxRetained — the sales tax collected outside Stripe, which the
        /// company keeps. Shown as "Company Revenue".
        /// </summary>
        public decimal TotalAmount { get; set; }
        /// <summary>Sales tax owed to the state: the part collected through Stripe.</summary>
        public decimal TotalTaxes { get; set; }
        /// <summary>
        /// Sales tax charged on Cash/Zelle/Check/Other payments — never remitted, so it is
        /// company money. ALREADY INCLUDED in TotalAmount; reported separately only so the page
        /// can say how much of the revenue it is. Never add it to TotalAmount again.
        /// </summary>
        public decimal TotalTaxRetained { get; set; }
        public decimal TotalTips { get; set; }
        /// <summary>Promo/first-time + subscription + loyalty discounts granted. Informational only.</summary>
        public decimal TotalDiscounts { get; set; }
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

        // ── Not-yet-performed orders ───────────────────────────────────────────────────
        // Booked orders inside the window that still have to happen (Active/Pending).
        // ALWAYS reported, whatever includeUpcoming was, so the finances page can label its
        // "include unfinished cleanings" toggle before the user turns it on.
        public int UpcomingOrders { get; set; }
        // True when the figures above are a PROJECTION: the caller passed includeUpcoming=true,
        // so the UpcomingOrders above are folded into every total on this DTO.
        public bool IncludesUpcoming { get; set; }

        // ── Google Ads daily run-rate ──────────────────────────────────────────────────
        // Ad spend is synced one Expense row per day, so unlike every other expense it has a
        // meaningful per-day rate — and the remaining days of a running period can be forecast
        // from it. See AdminStatisticsController for how these are derived.

        // Ad spend ACTUALLY recorded inside the window (never includes the projection below).
        public decimal GoogleAdsSpend { get; set; }
        // Days of the window that have already happened — the average's denominator. Days with
        // no spend count too: a $0 day is a real day that simply cost nothing.
        public int GoogleAdsCoveredDays { get; set; }
        // GoogleAdsSpend / GoogleAdsCoveredDays (0 when the window has no elapsed days).
        public decimal GoogleAdsDailyAverage { get; set; }
        // Days of the window still to come. Non-zero only for a period running past today.
        public int GoogleAdsProjectedDays { get; set; }
        // DailyAverage × ProjectedDays, and 0 unless includeUpcoming was set. When non-zero it is
        // ALREADY folded into the Google Ads category, ExpensesBreakdown.Total, TotalExpenses and
        // TotalCompanyRevenue — so callers must not add it again.
        public decimal GoogleAdsProjectedSpend { get; set; }
    }

    /// <summary>
    /// Daily data point for statistics chart. CompanyRevenue is NET. Amount/Taxes/Tips come from
    /// OrderRevenueMath.Split, so summing days reconciles with the OrderStatisticsDto totals.
    /// </summary>
    public class DailyStatisticsDto
    {
        public string Date { get; set; } = "";
        public int Orders { get; set; }
        /// <summary>
        /// Taxable cleaning revenue (after discounts, before tax, without tips, net of refunds)
        /// plus TaxRetained — same basis as OrderStatisticsDto.TotalAmount.
        /// </summary>
        public decimal Amount { get; set; }
        /// <summary>Sales tax owed to the state: the part collected through Stripe.</summary>
        public decimal Taxes { get; set; }
        /// <summary>Sales tax collected outside Stripe. ALREADY INSIDE Amount — never add it again.</summary>
        public decimal TaxRetained { get; set; }
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