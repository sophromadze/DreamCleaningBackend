using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.Models
{
    public class OrderUpdateHistory
    {
        public int Id { get; set; }

        // Order relationship
        public int OrderId { get; set; }
        public virtual Order Order { get; set; }

        // Who made the update
        public int UpdatedByUserId { get; set; }
        public virtual User UpdatedByUser { get; set; }

        // When
        public DateTime UpdatedAt { get; set; }

        // Original values (before this update)
        [Column(TypeName = "decimal(18,2)")]
        public decimal OriginalSubTotal { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal OriginalTax { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal OriginalTips { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal OriginalCompanyDevelopmentTips { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal OriginalTotal { get; set; }

        // New values (after this update)
        [Column(TypeName = "decimal(18,2)")]
        public decimal NewSubTotal { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal NewTax { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal NewTips { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal NewCompanyDevelopmentTips { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal NewTotal { get; set; }

        // The additional amount for this update
        [Column(TypeName = "decimal(18,2)")]
        public decimal AdditionalAmount { get; set; }

        // Payment details if additional payment was made
        [StringLength(100)]
        public string? PaymentIntentId { get; set; }

        // Optional: Store update reason/notes
        [StringLength(500)]
        public string? UpdateNotes { get; set; }

        // Track if this update was paid
        public bool IsPaid { get; set; } = false;
        public DateTime? PaidAt { get; set; }

        // Manual payment tracking for the additional amount. Mirrors the same fields on Order.
        // Default Normal (=0) preserves the pre-existing Stripe/PaymentIntentId flow exactly — every
        // existing row keeps its behavior after migration. When set to anything else, the additional
        // amount was collected outside Stripe (e.g. customer paid the top-up by Zelle while the base
        // order was paid by card). The base order's PaymentMethod stays Normal so it still counts as
        // a Stripe order; only this row carries the manual method. Statistics subtract these
        // manually-paid additional amounts from the Stripe-fee base so no card fee is charged on them.
        public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.Normal;

        [StringLength(255)]
        public string? PaymentReference { get; set; }  // Zelle confirmation #, check #, receipt #

        [StringLength(1000)]
        public string? PaymentNotes { get; set; }

        public DateTime? ManualPaymentRecordedAt { get; set; }

        public int? ManualPaymentRecordedByUserId { get; set; }

        // When the admin manually triggered the "updated payment" notification (email + SMS) for
        // this update row. Null = customer has not yet been told about this update's additional
        // amount. The admin panel uses this to flip between "Send Updated Payment" (first send)
        // and "Send Payment Reminder" (follow-ups).
        public DateTime? UpdatedPaymentNotificationSentAt { get; set; }
    }
}