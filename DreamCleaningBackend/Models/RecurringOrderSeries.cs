using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    /// <summary>
    /// The unit an admin picks a recurrence interval in.
    ///
    /// DAILY IS DELIBERATELY OUT OF SCOPE. <see cref="Days"/> with an interval of 1 is refused
    /// (see <c>RecurrenceCalculator.Validate</c>) rather than silently accepted: a daily cleaning
    /// needs staffing, payment-cadence and reminder behaviour this system does not have, and
    /// generating thirty orders a month through the ordinary path would look like it worked.
    /// Two days and up are ordinary.
    /// </summary>
    public enum RecurrenceIntervalUnit
    {
        Days = 0,
        Weeks = 1,
        Months = 2
    }

    /// <summary>
    /// "Repeat this cleaning every N days/weeks/months."
    ///
    /// A series is a SCHEDULE, not an order. It owns nothing financial: every occurrence is an
    /// ordinary <see cref="Order"/>, priced by the ordinary calculator, visible in Admin Orders and
    /// in the customer's My Orders like any other. The series only decides WHICH DATES exist and
    /// which configuration they are built from — which is why nothing here duplicates a column the
    /// order already has.
    ///
    /// Rules worth keeping:
    ///
    ///  • <b>Existing orders are never converted.</b> A series exists only because an admin created
    ///    one on a specific order. Nothing backfills, and an order with no
    ///    <c>Order.RecurringSeriesId</c> behaves exactly as it did before this feature.
    ///  • <b>The template order is a SOURCE, not a parent.</b> Occurrences copy its booking
    ///    configuration and nothing transactional — see <c>RecurringOrderSeriesService</c> for the
    ///    explicit copy/don't-copy lists. Deleting or cancelling the template does not retro-price
    ///    or delete the occurrences it produced.
    ///  • <b>Generation is idempotent</b>, enforced by the unique index on
    ///    (RecurringSeriesId, RecurrenceOccurrenceDate) — not by a flag, and not by the generator
    ///    remembering what it did. A second sweep in the same minute writes nothing.
    /// </summary>
    public class RecurringOrderSeries
    {
        public int Id { get; set; }

        /// <summary>The customer every occurrence belongs to. Copied onto each order.</summary>
        public int UserId { get; set; }

        [ForeignKey("UserId")]
        public virtual User? User { get; set; }

        /// <summary>
        /// The order every occurrence is modelled on. Kept as a plain reference with no cascade:
        /// the template is a real order in its own right and may be cancelled, refunded or hidden
        /// without that meaning anything about the schedule.
        /// </summary>
        public int TemplateOrderId { get; set; }

        [ForeignKey("TemplateOrderId")]
        public virtual Order? TemplateOrder { get; set; }

        /// <summary>How many <see cref="IntervalUnit"/>s between visits. Always &gt;= 1.</summary>
        public int IntervalValue { get; set; } = 1;

        public RecurrenceIntervalUnit IntervalUnit { get; set; } = RecurrenceIntervalUnit.Weeks;

        /// <summary>
        /// The FIRST service date of the series — the date every later occurrence is measured
        /// from, so the schedule cannot drift. Stored as a date; the time of day is
        /// <see cref="ServiceTime"/>.
        ///
        /// Counting from the anchor rather than from "the last order we made" is what makes a
        /// monthly series land on the 31st again after a February that clamped it to the 28th.
        /// </summary>
        public DateTime AnchorDate { get; set; }

        /// <summary>Wall-clock start time, NY, copied onto every occurrence.</summary>
        public TimeSpan ServiceTime { get; set; }

        /// <summary>
        /// Paused series generate nothing. Deactivating NEVER deletes orders already generated —
        /// those are real bookings a customer may already have been told about.
        /// </summary>
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// Optional hard stop. Null = runs until an admin pauses it. An occurrence falling after
        /// this date is not generated.
        /// </summary>
        public DateTime? EndDate { get; set; }

        /// <summary>
        /// Copy the template order's cleaner assignments onto each generated occurrence.
        ///
        /// COPYING AN ASSIGNMENT IS NOT NOTIFYING A CLEANER. The copied
        /// <see cref="OrderCleaner"/> row leaves <c>AssignmentNotificationSentAt</c> null and is
        /// stamped with <c>AutoAssignedFromSeriesId</c>, so the admin panel can say "auto-assigned
        /// from recurring series — cleaner has not been notified" and the existing Send/Resend
        /// controls remain the only thing that mails or texts anybody.
        /// </summary>
        public bool CopyCleanerAssignments { get; set; }

        /// <summary>
        /// Whether the system may PROACTIVELY ask the customer to pay a generated occurrence.
        ///
        /// DEFAULT TRUE for newly created series. Existing explicit choices are preserved. A future recurring cleaning is visible and voluntarily
        /// payable from the moment it is generated; that is a customer choice. An automatic
        /// request is a different thing, and when it is switched on it is still governed by
        /// <c>RecurringPaymentPolicy</c>: nothing may be requested for an occurrence until at
        /// least 24 hours after the PREVIOUS cleaning in the series has happened.
        /// </summary>
        public bool AutoRequestPayment { get; set; } = true;

        /// <summary>
        /// The standing discount this arrangement carries, written as a PERCENTAGE of each
        /// occurrence's own subtotal. Null when the admin entered a fixed amount instead, or when
        /// the series carries no discount of its own.
        /// </summary>
        [Range(0, 100)]
        public decimal? RecurringLoyaltyDiscountPercent { get; set; }

        /// <summary>
        /// The same standing discount written as a FIXED DOLLAR AMOUNT off every cleaning.
        ///
        /// AT MOST ONE OF THE TWO COLUMNS IS EVER SET — they are two ways of writing one
        /// agreement, not two discounts, and <c>RecurringDiscountPolicy</c> is the only thing that
        /// reads either of them. A fixed amount is converted into that occurrence's equivalent
        /// percentage when the order is priced, so the customer is taken off the same number of
        /// dollars on every cleaning even when the cleanings are priced differently, and every
        /// existing loyalty surface keeps reading the percentage column it has always read.
        /// </summary>
        [Column(TypeName = "decimal(10,2)")]
        public decimal? RecurringLoyaltyDiscountAmount { get; set; }

        /// <summary>Permanent stop, distinct from the reversible IsActive pause.</summary>
        public DateTime? StoppedAt { get; set; }

        /// <summary>Keep-existing edits start the new rule strictly after this retained date.</summary>
        public DateTime? GenerateAfterDate { get; set; }

        /// <summary>
        /// How far ahead the rolling generator has already materialized occurrences. A cache for
        /// logging and the admin panel — the actual duplicate guard is the unique index, never
        /// this column, so a stale value can only cause a harmless re-scan.
        /// </summary>
        public DateTime? GeneratedThroughDate { get; set; }

        /// <summary>Free-text note an admin can leave about the arrangement.</summary>
        [StringLength(500)]
        public string? Notes { get; set; }

        public int CreatedByUserId { get; set; }

        [ForeignKey("CreatedByUserId")]
        public virtual User? CreatedByUser { get; set; }

        public int? UpdatedByUserId { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>Every order this series has produced, including the ones already served.</summary>
        public virtual ICollection<Order> Occurrences { get; set; } = new List<Order>();
    }
}
