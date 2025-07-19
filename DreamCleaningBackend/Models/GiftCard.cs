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

        [Required]
        [StringLength(100)]
        public string RecipientName { get; set; }

        [Required]
        [StringLength(255)]
        public string RecipientEmail { get; set; }

        [Required]
        [StringLength(100)]
        public string SenderName { get; set; }

        [Required]
        [StringLength(255)]
        public string SenderEmail { get; set; }

        [StringLength(500)]
        public string? Message { get; set; }

        public bool IsActive { get; set; } = true;

        // Foreign keys
        public int PurchasedByUserId { get; set; }

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
