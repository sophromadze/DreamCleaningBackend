using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.Models
{
    public class GiftCard
    {
        [Key]
        public int Id { get; set; }

        [Required]
        [StringLength(14)] // Format: XXXX-XXXX-XXXX
        public string Code { get; set; }

        [Required]
        [Column(TypeName = "decimal(10,2)")]
        public decimal OriginalAmount { get; set; }

        [Required]
        [Column(TypeName = "decimal(10,2)")]
        public decimal CurrentBalance { get; set; }

        // Null while a "buy for myself - send later" card has not been sent yet.
        [StringLength(100)]
        public string? RecipientName { get; set; }

        [StringLength(255)]
        public string? RecipientEmail { get; set; }

        [Required]
        [StringLength(100)]
        public string SenderName { get; set; }

        [Required]
        [StringLength(255)]
        public string SenderEmail { get; set; }

        [StringLength(500)]
        public string? Message { get; set; }

        public bool IsActive { get; set; } = true;

        // "Buy for myself - send later": true until the buyer sends the card from their profile.
        // Defaults to false so every card from the "send now" flow (and all existing rows) counts as sent.
        public bool IsPendingSend { get; set; } = false;

        // When a send-later card was sent from the profile (null for "send now" cards - use PaidAt/CreatedAt).
        public DateTime? SentAt { get; set; }

        // Last time the recipient email went out from the profile (send or resend) - resend cooldown.
        public DateTime? LastEmailSentAt { get; set; }

        // confirm-payment has sent the purchase emails - a repeated confirm must not send them again.
        // (IsPaid can't answer this: the webhook marks a card paid without sending anything.)
        public bool EmailsSentOnPurchase { get; set; } = false;

        // Foreign keys
        public int? PurchasedByUserId { get; set; } // Nullable to support anonymous purchases

        // Payment tracking
        [StringLength(100)]
        public string? PaymentIntentId { get; set; }
        public bool IsPaid { get; set; } = false;
        public DateTime? PaidAt { get; set; }

        // Timestamps
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? UpdatedAt { get; set; }

        // Navigation properties
        public virtual User PurchasedByUser { get; set; }
        public virtual ICollection<GiftCardUsage> GiftCardUsages { get; set; } = new List<GiftCardUsage>();

        // Computed property to check if fully used
        [NotMapped]
        public bool IsFullyUsed => CurrentBalance <= 0;

        // Computed property for total amount used
        [NotMapped]
        public decimal TotalAmountUsed => OriginalAmount - CurrentBalance;
    }

    public class GiftCardUsage
    {
        [Key]
        public int Id { get; set; }

        public int GiftCardId { get; set; }
        public int OrderId { get; set; }
        public int UserId { get; set; } // ADD: Track who used it

        [Required]
        [Column(TypeName = "decimal(10,2)")]
        public decimal AmountUsed { get; set; }

        [Required]
        [Column(TypeName = "decimal(10,2)")]
        public decimal BalanceAfterUsage { get; set; }

        public DateTime UsedAt { get; set; } = DateTime.UtcNow;

        // Navigation properties
        public virtual GiftCard GiftCard { get; set; }
        public virtual Order Order { get; set; }
        public virtual User User { get; set; } // ADD: Navigation to user
    }

    public class GiftCardConfig
    {
        public int Id { get; set; } = 1; // Only one record needed
        public string BackgroundImagePath { get; set; }
        public DateTime? LastUpdated { get; set; }
    }
}
