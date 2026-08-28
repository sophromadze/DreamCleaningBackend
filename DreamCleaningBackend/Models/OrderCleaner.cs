using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    public class OrderCleaner
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public int OrderId { get; set; }

        [ForeignKey("OrderId")]
        public virtual Order Order { get; set; }

        [Required]
        public int CleanerId { get; set; }

        [ForeignKey("CleanerId")]
        public virtual Cleaner Cleaner { get; set; }

        public DateTime AssignedAt { get; set; } = DateTime.UtcNow;

        [Required]
        public int AssignedBy { get; set; } // Admin/Moderator who assigned

        [ForeignKey("AssignedBy")]
        public virtual User AssignedByUser { get; set; }

        // Tips for cleaner (visible to cleaners)
        [StringLength(1000)]
        public string? TipsForCleaner { get; set; }

        /// <summary>
        /// When the admin sent the assignment notification email to this cleaner for this order.
        /// Null until "Send assignment email" is used; reminders only run after this is set.
        /// </summary>
        public DateTime? AssignmentNotificationSentAt { get; set; }

        // ===== Payroll (Outgoing Payments page, SuperAdmin) =====
        //
        // This row is the payout record for ONE cleaner on ONE order. Both overrides below are
        // NULL by default, meaning "use the order's automatic figure" — CleanerPayrollCalculator
        // is the single place they are resolved. A null is not the same as a value that happens
        // to equal the automatic one: an explicit value stays put when the order is re-priced,
        // a null keeps tracking the order.

        /// <summary>
        /// SuperAdmin override of this cleaner's hourly rate for this order. Null = use
        /// Order.CleanerHourlyRate. Set when one cleaner on a job is paid differently from the
        /// others (a trainee, a lead), which is why the rate cannot live on the order alone.
        /// </summary>
        [Column(TypeName = "decimal(18,2)")]
        public decimal? SalaryHourlyRate { get; set; }

        /// <summary>
        /// SuperAdmin override of the minutes this cleaner is paid for on this order. Null = use
        /// the automatic even split of Order.TotalDuration across the ASSIGNED cleaners. Set when
        /// one cleaner left early or stayed late.
        /// </summary>
        [Column(TypeName = "decimal(18,2)")]
        public decimal? SalaryBillableMinutes { get; set; }

        /// <summary>True once this cleaner has actually been paid for this order.</summary>
        public bool IsPaid { get; set; } = false;

        /// <summary>
        /// What was handed over, frozen at the moment of payment. Deliberately NOT re-derived on
        /// read: the rate or the order's duration can change afterwards, and the record of what
        /// left the company must not move with it.
        /// </summary>
        [Column(TypeName = "decimal(18,2)")]
        public decimal? PaidAmount { get; set; }

        /// <summary>
        /// How the money was actually sent. Defaults to the cleaner's saved method, but
        /// overridable at pay time because the saved one is only a hint.
        /// </summary>
        public CleanerPaymentMethod? PaidVia { get; set; }

        public DateTime? PaidAt { get; set; }

        /// <summary>The SuperAdmin who marked it paid. Kept for the audit trail.</summary>
        public int? PaidByUserId { get; set; }

        [ForeignKey("PaidByUserId")]
        public virtual User? PaidByUser { get; set; }

        [StringLength(500)]
        public string? PaymentNote { get; set; }
    }
}