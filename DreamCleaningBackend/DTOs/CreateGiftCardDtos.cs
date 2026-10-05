// DreamCleaningBackend/DTOs/GiftCardDto.cs
using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.DTOs
{
    public class CreateGiftCardDto
    {
        [Required]
        [Range(25, 10000, ErrorMessage = "Gift card amount must be between $25 and $10,000")]
        public decimal Amount { get; set; }

        // "Buy for myself - send later": no recipient yet, requires a signed-in buyer.
        // Recipient fields are then ignored; otherwise the controller requires them.
        public bool SendLater { get; set; }

        [StringLength(100, ErrorMessage = "Recipient name cannot exceed 100 characters")]
        public string? RecipientName { get; set; }

        [EmailAddress(ErrorMessage = "Please enter a valid recipient email address")]
        [StringLength(255)]
        public string? RecipientEmail { get; set; }

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

    // Profile -> Gift Cards: one card the signed-in user purchased.
    public class MyGiftCardDto
    {
        public int Id { get; set; }
        // Full code while the card is unsent; only the last 4 characters once it has been sent.
        public string Code { get; set; }
        public bool IsCodeMasked { get; set; }
        public decimal OriginalAmount { get; set; }
        public decimal CurrentBalance { get; set; }
        public decimal AmountUsed { get; set; }
        public DateTime PurchasedAt { get; set; }
        // "NotSent" | "Sent" | "FullyUsed"
        public string Status { get; set; }
        public bool IsPendingSend { get; set; }
        public bool IsActive { get; set; }
        public string? RecipientName { get; set; }
        public string? RecipientEmail { get; set; }
        public DateTime? SentAt { get; set; }
        public string SenderName { get; set; }
        public string? Message { get; set; }
        public bool CanSend { get; set; }
        public bool CanResend { get; set; }
        public List<MyGiftCardUsageDto> Usages { get; set; } = new();
    }

    public class MyGiftCardUsageDto
    {
        public DateTime UsedAt { get; set; }
        public decimal AmountUsed { get; set; }
    }

    public class SendMyGiftCardDto
    {
        [Required(ErrorMessage = "Recipient name is required")]
        [StringLength(100, ErrorMessage = "Recipient name cannot exceed 100 characters")]
        public string RecipientName { get; set; }

        [Required(ErrorMessage = "Recipient email is required")]
        [StringLength(255)]
        public string RecipientEmail { get; set; }

        [Required(ErrorMessage = "Your name is required")]
        [StringLength(100, ErrorMessage = "Sender name cannot exceed 100 characters")]
        public string SenderName { get; set; }

        [StringLength(500, ErrorMessage = "Message cannot exceed 500 characters")]
        public string? Message { get; set; }
    }

    // Outcome of a profile send / resend; the controller maps Kind to the HTTP status.
    public class GiftCardSendResult
    {
        public enum ResultKind { Ok, NotFound, Invalid, TooMany, EmailFailed, Failed }

        public ResultKind Kind { get; private set; }
        public string? Message { get; private set; }
        public MyGiftCardDto? Card { get; private set; }

        public static GiftCardSendResult Ok(MyGiftCardDto card) => new() { Kind = ResultKind.Ok, Card = card };
        public static GiftCardSendResult NotFound() => new() { Kind = ResultKind.NotFound, Message = "Gift card not found" };
        public static GiftCardSendResult Invalid(string message) => new() { Kind = ResultKind.Invalid, Message = message };
        public static GiftCardSendResult TooMany(string message) => new() { Kind = ResultKind.TooMany, Message = message };
        public static GiftCardSendResult EmailFailed(MyGiftCardDto card, string message) => new() { Kind = ResultKind.EmailFailed, Card = card, Message = message };
        public static GiftCardSendResult Failed(string message) => new() { Kind = ResultKind.Failed, Message = message };
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