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

        /// <summary>
        /// Set when the recurring generator copied this assignment from the series' template order
        /// rather than an admin choosing it on this job (2026-09).
        ///
        /// COPYING AN ASSIGNMENT IS NOT NOTIFYING A CLEANER, and this column is what lets the
        /// panel say so. The row is created with <see cref="AssignmentNotificationSentAt"/> null
        /// and NOTHING is emailed or texted — the existing Send / Resend assignment controls stay
        /// the only thing that contacts anybody, exactly as they were before recurring orders
        /// existed. The admin sees "Auto-assigned from recurring series — cleaner has not been
        /// notified" until they use them.
        ///
        /// Kept after the notification is sent: it stays the record of HOW the cleaner got onto
        /// this job, which "notification sent" alone cannot say.
        /// </summary>
        public int? AutoAssignedFromSeriesId { get; set; }

        // ===== Tips withheld from this cleaner (2026-10) =====
        //
        // Set per cleaner per order from the order panel's Cleaners tab, when the office decides
        // a cleaner did not earn the customer's tip (behaviour, damage). Both false by default.

        /// <summary>
        /// The cleaner is not TOLD about the tip: the assignment email / SMS (send, send-all and
        /// resend all build through EmailService) prints no tip figure. The cleaner portal does not
        /// show a cleaner any tip at all, so there is nothing to hide there.
        /// </summary>
        public bool TipsHiddenFromCleaner { get; set; }

        /// <summary>
        /// The cleaner is not PAID the tip: CleanerPayrollCalculator drops this line's tip share,
        /// so Outgoing Payments owes salary only. Turned on together with
        /// <see cref="TipsHiddenFromCleaner"/>; the office can turn it back off ("pay tips") while
        /// the tip stays hidden from the cleaner's notifications.
        /// </summary>
        public bool TipsWithheldFromPayout { get; set; }

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
        /// What has been handed over, frozen at the moment of payment. Deliberately NOT re-derived
        /// on read: the rate or the order's duration can change afterwards, and the record of what
        /// left the company must not move with it.
        ///
        /// It is a RUNNING TOTAL, not a single payment. When the order's hours grow after the
        /// cleaner was paid, the Outgoing Payments page reports the difference as still owed
        /// (Helpers/CleanerPayoutSettlement) and paying it ADDS to this figure — so it always
        /// answers "how much has this person had for this order", which is exactly what the
        /// shortfall has to be measured against. The individual payment events live in the audit
        /// log, under CleanerPayout / PayoutRecorded and PayoutToppedUp.
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