using System.ComponentModel.DataAnnotations;
using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.DTOs
{
    /// <summary>
    /// Turn an existing order into a recurring series, or edit the schedule on one.
    ///
    /// Carries NO money and no service configuration: an occurrence is built from the template
    /// ORDER, and its price comes from the ordinary calculator. There is deliberately nothing here
    /// a caller could use to set a total.
    /// </summary>
    public class SaveRecurringSeriesDto
    {
        /// <summary>
        /// The series' standing discount as a percentage. Mutually exclusive with
        /// <see cref="RecurringLoyaltyDiscountAmount"/> — sending both is rejected rather than
        /// silently preferring one, because the two would disagree about what was agreed.
        /// </summary>
        [Range(0, 100)]
        public decimal? RecurringLoyaltyDiscountPercent { get; set; }

        /// <summary>The same standing discount as a fixed dollar amount off every cleaning.</summary>
        [Range(0, 100000)]
        public decimal? RecurringLoyaltyDiscountAmount { get; set; }

        [Range(1, 90)]
        public int IntervalValue { get; set; } = 1;

        public RecurrenceIntervalUnit IntervalUnit { get; set; } = RecurrenceIntervalUnit.Weeks;

        /// <summary>
        /// First service date of the series. Null on create means "the template order's own
        /// service date", which is what an admin means by "repeat this one".
        /// </summary>
        public DateTime? AnchorDate { get; set; }

        /// <summary>Optional last date. Null runs until the series is paused.</summary>
        public DateTime? EndDate { get; set; }

        /// <summary>Reuse the template order's cleaners. Copies the assignment and NOTIFIES
        /// NOBODY — see RecurringOrderSeries.CopyCleanerAssignments.</summary>
        public bool CopyCleanerAssignments { get; set; }

        /// <summary>Let the system proactively ask the customer to pay each occurrence. On for new series; null preserves the current setting on update.
        /// Still governed by the 24-hour rule when on.</summary>
        public bool? AutoRequestPayment { get; set; }

        public TimeSpan? ServiceTime { get; set; }

        /// <summary>Required on schedule changes with generated future orders: Keep or Regenerate.</summary>
        public string? FutureOrdersAction { get; set; }

        public bool IsActive { get; set; } = true;

        [StringLength(500)]
        public string? Notes { get; set; }
    }

    /// <summary>One generated (or template) order, as the series panel lists it.</summary>
    public class RecurringOccurrenceDto
    {
        public int OrderId { get; set; }
        public DateTime ServiceDate { get; set; }
        public TimeSpan ServiceTime { get; set; }
        public DateTime? OccurrenceDate { get; set; }
        public string Status { get; set; } = "";
        public decimal Total { get; set; }
        public bool IsSkipped { get; set; }
        public bool IsPaid { get; set; }
        public bool IsTemplate { get; set; }
        public bool WasGenerated { get; set; }
        public string PaymentMethod { get; set; } = "Normal";

        /// <summary>Cleaners on this occurrence that the series auto-assigned and NOBODY has
        /// notified yet. What the admin badge is rendered from.</summary>
        public int AutoAssignedNotNotifiedCount { get; set; }

        public int AssignedCleanerCount { get; set; }
    }

    public class RecurringSeriesDto
    {
        public decimal? RecurringLoyaltyDiscountPercent { get; set; }
        public decimal? RecurringLoyaltyDiscountAmount { get; set; }
        public List<string> GenerationWarnings { get; set; } = new();
        public DateTime? StoppedAt { get; set; }
        public int Id { get; set; }
        public int UserId { get; set; }
        public string CustomerName { get; set; } = "";
        public int TemplateOrderId { get; set; }
        public int IntervalValue { get; set; }
        public RecurrenceIntervalUnit IntervalUnit { get; set; }
        public string IntervalLabel { get; set; } = "";
        public DateTime AnchorDate { get; set; }
        public TimeSpan ServiceTime { get; set; }
        public DateTime? EndDate { get; set; }
        public bool IsActive { get; set; }
        public bool CopyCleanerAssignments { get; set; }
        public bool AutoRequestPayment { get; set; }
        public DateTime? GeneratedThroughDate { get; set; }
        public string? Notes { get; set; }
        public DateTime CreatedAt { get; set; }
        public string? CreatedByName { get; set; }

        /// <summary>Occurrences already materialized, nearest first.</summary>
        public List<RecurringOccurrenceDto> Occurrences { get; set; } = new();

        /// <summary>Dates inside the horizon that do not exist as orders yet. Empty right after a
        /// sweep; non-empty only between an edit and the next generation.</summary>
        public List<DateTime> PendingDates { get; set; } = new();
    }

    public class RecurringPricePreviewDto
    {
        public List<RecurringSourceDiscountDto> SourceDiscounts { get; set; } = new();
        public decimal BaseCleaning { get; set; }
        public decimal LoyaltyPercent { get; set; }
        public string LoyaltySource { get; set; } = "None";
        public decimal LoyaltyAmount { get; set; }

        /// <summary>
        /// True when the applied discount was entered as a fixed amount, so the panel can show
        /// the percentage as the DERIVED figure it is rather than as something anybody typed.
        /// </summary>
        public bool LoyaltyIsFixedAmount { get; set; }
        public decimal Tax { get; set; }
        public decimal Tips { get; set; }
        public decimal Total { get; set; }
        public bool CommercialLoyaltyExcluded { get; set; }
    }

    public record RecurringSourceDiscountDto(string Label, decimal Amount, decimal? Percent = null);

    /// <summary>What one generation pass did. Returned by the manual "generate now" action.</summary>
    public class RecurringGenerationResultDto
    {
        public int SeriesId { get; set; }
        public int CreatedCount { get; set; }
        public List<int> CreatedOrderIds { get; set; } = new();

        /// <summary>Occurrence dates that already existed. Proof the pass was idempotent rather
        /// than lucky — a second run reports every date here and creates nothing.</summary>
        public List<DateTime> SkippedExistingDates { get; set; } = new();

        public List<string> Warnings { get; set; } = new();
    }

    // ── Customer-facing: upcoming recurring cleanings and what may be paid ─────────────────────

    /// <summary>One upcoming recurring cleaning on the customer's My Orders page.</summary>
    public class UpcomingRecurringOrderDto
    {
        public bool IncludedInPayAll { get; set; }
        public string PaymentMethod { get; set; } = "Normal";
        public int OrderId { get; set; }
        public DateTime ServiceDate { get; set; }
        public TimeSpan ServiceTime { get; set; }
        public string ServiceTypeName { get; set; } = "";
        public string Status { get; set; } = "";
        public decimal Total { get; set; }
        public decimal AmountDue { get; set; }
        public bool IsPaid { get; set; }

        /// <summary>The customer may pay this one right now. Only the nearest unpaid occurrence
        /// starts out payable; paying it exposes the next.</summary>
        public bool IsPayable { get; set; }

        /// <summary>1 = nearest unpaid. 0 for paid or cancelled occurrences.</summary>
        public int QueuePosition { get; set; }

        /// <summary>Why it is not payable yet, in the customer's words.</summary>
        public string? BlockedReason { get; set; }
    }

    public class UpcomingRecurringOrdersDto
    {
        /// <summary>NEAREST FIRST — the requirement, and the only order that makes sense on a page
        /// whose whole purpose is "what is coming up and what do I owe".</summary>
        public List<UpcomingRecurringOrderDto> Orders { get; set; } = new();

        /// <summary>Sum of every unpaid occurrence, server-derived. What "Pay all upcoming"
        /// would charge — shown so the customer sees the figure before they commit to it.</summary>
        public decimal PayAllTotal { get; set; }

        public int PayAllCount { get; set; }

        /// <summary>False when there is nothing to combine (0 or 1 unpaid occurrence), so the
        /// button is not offered for a single order that is already individually payable.</summary>
        public bool CanPayAll { get; set; }
    }

    /// <summary>
    /// Start a combined payment.
    ///
    /// NOTE WHAT IS ABSENT: no amount, and no list of order ids. The server derives BOTH from the
    /// signed-in customer's own upcoming recurring orders, so a tampered request cannot change what
    /// is charged and cannot name somebody else's order. Same shape as
    /// <c>StartInvoiceCheckoutDto</c>, for the same reason.
    /// </summary>
    public class StartCombinedPaymentDto
    {
        /// <summary>Restrict the batch to one series. Optional; the customer's whole upcoming set
        /// is used when omitted. Validated to belong to the caller.</summary>
        public int? RecurringSeriesId { get; set; }
    }

    public class CombinedPaymentDto
    {
        public int BatchId { get; set; }
        public decimal Amount { get; set; }
        public List<int> OrderIds { get; set; } = new();
        public string? PaymentIntentId { get; set; }
        public string? PaymentClientSecret { get; set; }
        public bool RequiresPayment { get; set; }
    }
}
