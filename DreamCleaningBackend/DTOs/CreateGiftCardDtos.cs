// DreamCleaningBackend/DTOs/GiftCardDto.cs
using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.DTOs
{
    public class CreateGiftCardDto
    {
        [Required]
        [Range(25, 10000, ErrorMessage = "Gift card amount must be between $25 and $10,000")]
        public decimal Amount { get; set; }

        [Required]
        [StringLength(100, ErrorMessage = "Recipient name cannot exceed 100 characters")]
        public string RecipientName { get; set; }

        [Required]
        [EmailAddress(ErrorMessage = "Please enter a valid recipient email address")]
        [StringLength(255)]
        public string RecipientEmail { get; set; }

        [Required]
        [StringLength(100, ErrorMessage = "Sender name cannot exceed 100 characters")]
        public string SenderName { get; set; }

        [Required]
        [EmailAddress(ErrorMessage = "Please enter a valid sender email address")]
        [StringLength(255)]
        public string SenderEmail { get; set; }

        [StringLength(500, ErrorMessage = "Message cannot exceed 500 characters")]
        public string? Message { get; set; }
    }

    public class GiftCardDto
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
        public bool IsUsed { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? UsedAt { get; set; }
        public string PurchasedByUserName { get; set; }
        public string? UsedByUserName { get; set; }
    }

    public class GiftCardPurchaseResponseDto
    {
        public int GiftCardId { get; set; }
        public string Code { get; set; }
        public decimal Amount { get; set; }
        public string Status { get; set; }
        public string PaymentIntentId { get; set; }
        public string PaymentClientSecret { get; set; }
    }

    public class ApplyGiftCardDto
    {
        [Required]
        [StringLength(14, MinimumLength = 14)]
        public string Code { get; set; }
    }

    public class GiftCardValidationDto
    {
        public bool IsValid { get; set; }
        public decimal AvailableBalance { get; set; }
        public string? Message { get; set; }
        public string? RecipientName { get; set; }
    }

    public class GiftCardUsageDto
    {
        public int Id { get; set; }
        public string GiftCardCode { get; set; }
        public decimal AmountUsed { get; set; }
        public decimal BalanceAfterUsage { get; set; }
        public DateTime UsedAt { get; set; }
        public string OrderReference { get; set; }
        public string UsedByName { get; set; }
        public string UsedByEmail { get; set; }
    }

    public class ApplyGiftCardToOrderDto
    {
        public string Code { get; set; }
        public decimal OrderAmount { get; set; }
        public int OrderId { get; set; }
    }

    public class UpdateGiftCardBackgroundDto
    {
        public string BackgroundImagePath { get; set; }
    }
}