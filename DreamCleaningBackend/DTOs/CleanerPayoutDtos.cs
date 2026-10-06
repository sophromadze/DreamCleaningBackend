using System.ComponentModel.DataAnnotations;
using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.DTOs
{
    // ── Unpaid cleaners tab ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every cleaner who is owed money right now, across all finished orders — not one month.
    /// What is owed is the payroll calculator's figure less what that line has already been
    /// handed, so a line whose hours grew after payday shows only the difference.
    /// </summary>
    public class UnpaidCleanersDto
    {
        public List<UnpaidCleanerDto> Cleaners { get; set; } = new();

        /// <summary>The grand total across every cleaner listed.</summary>
        public decimal TotalOutstanding { get; set; }

        public int LineCount { get; set; }

        /// <summary>
        /// Owed to staffing slots with nobody on file. Not a cleaner, so not payable from this tab
        /// — reported so the total here is never mistaken for everything owed.
        /// </summary>
        public decimal UnassignedOutstanding { get; set; }

        public int UnassignedSlotCount { get; set; }
    }

    public class UnpaidCleanerDto
    {
        public int CleanerId { get; set; }
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public bool IsActive { get; set; }

        /// <summary>Where this cleaner's wages are sent — the same fields the Orders panel shows.</summary>
        public CleanerPaymentMethod? PaymentMethod { get; set; }
        public string? PaymentDetails { get; set; }

        public decimal TotalOutstanding { get; set; }

        /// <summary>The part of <see cref="TotalOutstanding"/> that tops up lines paid before.</summary>
        public decimal TopUpOutstanding { get; set; }

        /// <summary>Oldest first — the order the work was done in.</summary>
        public List<UnpaidCleanerLineDto> Lines { get; set; } = new();
    }

    public class UnpaidCleanerLineDto
    {
        public int OrderCleanerId { get; set; }
        public int OrderId { get; set; }
        public DateTime ServiceDate { get; set; }
        public string ServiceTime { get; set; } = string.Empty;

        // The three raw ingredients of the shared short service-type label, as on the Orders tab.
        public string ServiceTypeName { get; set; } = string.Empty;
        public string RawServiceTypeName { get; set; } = string.Empty;
        public string? CustomServiceDisplayName { get; set; }
        public bool IsCustomServiceType { get; set; }
        public bool IsDeepCleaning { get; set; }

        public string CustomerName { get; set; } = string.Empty;
        public decimal BillableMinutes { get; set; }
        public decimal HourlyRate { get; set; }

        /// <summary>What the line is worth now: salary + tips.</summary>
        public decimal Payout { get; set; }

        /// <summary>Handed over already (non-zero only on a top-up).</summary>
        public decimal AlreadyPaid { get; set; }

        /// <summary>What paying this line now records. Echoed back in the pay request.</summary>
        public decimal Outstanding { get; set; }

        public bool IsTopUp { get; set; }
    }

    // ── Marking cleaners paid ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Pay several cleaners at once. Each selection carries the lines AND the amounts the
    /// confirmation showed: the server recomputes every one under the order lock and refuses the
    /// whole request (409) if any of them moved, rather than paying a figure nobody confirmed.
    /// </summary>
    public class PayCleanersDto
    {
        [Required, MinLength(1)]
        public List<PayCleanerSelectionDto> Cleaners { get; set; } = new();

        /// <summary>The day the money went out. Defaults to today (New York); never in the future.</summary>
        public DateTime? PaidOn { get; set; }

        /// <summary>Null = each cleaner's own saved method.</summary>
        public CleanerPaymentMethod? PaidVia { get; set; }

        [StringLength(200)]
        public string? Reference { get; set; }

        [StringLength(500)]
        public string? Note { get; set; }

        /// <summary>
        /// Minted once when the confirmation opens. A retried or double-submitted request with the
        /// same key returns the payouts already made instead of recording the money twice.
        /// </summary>
        [Required, StringLength(64, MinimumLength = 8)]
        public string IdempotencyKey { get; set; } = string.Empty;
    }

    public class PayCleanerSelectionDto
    {
        public int CleanerId { get; set; }

        [Required, MinLength(1)]
        public List<ExpectedPayoutLineDto> Lines { get; set; } = new();
    }

    public class ExpectedPayoutLineDto
    {
        public int OrderCleanerId { get; set; }

        /// <summary>The outstanding amount the confirmation showed for this line.</summary>
        public decimal Amount { get; set; }
    }

    public class PayCleanersResultDto
    {
        public List<int> PayoutIds { get; set; } = new();
        public int CleanerCount { get; set; }
        public decimal TotalPaid { get; set; }

        /// <summary>True when this key had already been used: nothing new was recorded.</summary>
        public bool AlreadyRecorded { get; set; }
    }

    // ── Paid cleaners tab ─────────────────────────────────────────────────────────────────────

    public class CleanerPayoutHistoryQuery
    {
        public DateTime? From { get; set; }
        public DateTime? To { get; set; }
        public int? CleanerId { get; set; }
        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 20;
    }

    public class CleanerPayoutHistoryDto
    {
        public List<CleanerPayoutDto> Payouts { get; set; } = new();
        public int TotalCount { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }

        /// <summary>Money still standing (not reverted) across the WHOLE filtered set, not the page.</summary>
        public decimal TotalPaid { get; set; }

        /// <summary>Everyone who has ever been paid, for the cleaner filter.</summary>
        public List<CleanerPayoutCleanerOptionDto> CleanerOptions { get; set; } = new();
    }

    public class CleanerPayoutCleanerOptionDto
    {
        public int CleanerId { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public class CleanerPayoutDto
    {
        public int Id { get; set; }
        public int CleanerId { get; set; }
        public string CleanerName { get; set; } = string.Empty;

        /// <summary>Everything the payout recorded.</summary>
        public decimal Amount { get; set; }

        /// <summary>What still stands after any reversal.</summary>
        public decimal ActiveAmount { get; set; }

        public DateTime PaidOn { get; set; }
        public CleanerPaymentMethod? PaidVia { get; set; }
        public string? Reference { get; set; }
        public string? Note { get; set; }
        public string? CreatedByName { get; set; }
        public DateTime CreatedAt { get; set; }
        public bool IsImported { get; set; }

        /// <summary>"Active", "PartiallyReverted" or "Reverted".</summary>
        public string Status { get; set; } = "Active";

        public DateTime? RevertedAt { get; set; }
        public string? RevertedByName { get; set; }
        public string? RevertReason { get; set; }

        public bool CanRevert { get; set; }

        public List<CleanerPayoutLineDto> Lines { get; set; } = new();
    }

    public class CleanerPayoutLineDto
    {
        public int Id { get; set; }
        public int OrderId { get; set; }
        public DateTime ServiceDate { get; set; }
        public string ServiceTypeName { get; set; } = string.Empty;
        public string RawServiceTypeName { get; set; } = string.Empty;
        public string? CustomServiceDisplayName { get; set; }
        public bool IsCustomServiceType { get; set; }
        public bool IsDeepCleaning { get; set; }
        public string? CustomerName { get; set; }
        public decimal Amount { get; set; }
        public bool IsTopUp { get; set; }
        public bool IsReverted { get; set; }
        public DateTime? RevertedAt { get; set; }
    }

    public class RevertCleanerPayoutDto
    {
        /// <summary>Required: a reversal moves money back to "owed", and the trail must say why.</summary>
        [Required, StringLength(500, MinimumLength = 3)]
        public string Reason { get; set; } = string.Empty;
    }
}
