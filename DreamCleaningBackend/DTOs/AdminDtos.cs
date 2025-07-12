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

    // User Management DTOs
    public class UserAdminDto
    {
        public int Id { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string Email { get; set; }
        public string? Phone { get; set; }
        public string Role { get; set; }
        public string? AuthProvider { get; set; }
        public string? SubscriptionName { get; set; }
        public bool FirstTimeOrder { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class UpdateUserRoleDto
    {
        [Required]
        public string Role { get; set; }
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
    }
}