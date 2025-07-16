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
    }
}