using System.ComponentModel.DataAnnotations;
using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.DTOs
{
    /// <summary>
    /// One finished order on the Outgoing Payments page, with a line per cleaner who worked it.
    /// The shape deliberately mirrors what the manager used to type by hand into WhatsApp —
    /// service type, cleaners, service date, duration total and per cleaner, subtotal, tax, total
    /// without tips, tips, and the "rate × hours = pay" working — so nothing has to be looked up
    /// in a second place before the money goes out.
    /// </summary>
    public class OutgoingPaymentOrderDto
    {
        public int OrderId { get; set; }

        /// <summary>The effective, human-facing service-type name (the custom label when there is one).</summary>
        public string ServiceTypeName { get; set; } = string.Empty;
        public bool IsCustomServiceType { get; set; }

        // The three raw ingredients the admin tables need to render their SHORT service-type
        // label ("Regular" / "Deep" / "Move In/Out" / "Construction" …). The label rules live in
        // exactly one place, shared/admin/service-type-short-label.ts on the frontend, so the
        // Orders tab and this page can never label the same order differently — which means the
        // backend ships the ingredients rather than a second copy of the formatting.

        /// <summary>The RAW ServiceType.Name, before the custom label is folded in.</summary>
        public string RawServiceTypeName { get; set; } = string.Empty;

        /// <summary>The per-order label an admin chose for a custom ("Pre-Arranged") order.</summary>
        public string? CustomServiceDisplayName { get; set; }

        /// <summary>Residential only: whether the deep-cleaning extra is on this order.</summary>
        public bool IsDeepCleaning { get; set; }

        public DateTime ServiceDate { get; set; }
        public string ServiceTime { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;

        /// <summary>How the CUSTOMER paid (Normal/Cash/Zelle/Check/Other) — the "Paid by cash" note.</summary>
        public string PaymentMethod { get; set; } = string.Empty;
        public bool IsPaidByCustomer { get; set; }

        public string CustomerName { get; set; } = string.Empty;
        public string? ServiceAddress { get; set; }
        public string? City { get; set; }

        // ===== The money on the order =====
        public decimal SubTotal { get; set; }
        public decimal Tax { get; set; }

        /// <summary>Order total WITHOUT tips — the "Current total (no tips)" line.</summary>
        public decimal TotalWithoutTips { get; set; }
        public decimal Tips { get; set; }
        public decimal Total { get; set; }

        // ===== Duration =====
        /// <summary>Total cleaner-minutes for the job, as stored on the order.</summary>
        public decimal TotalDuration { get; set; }

        /// <summary>The even per-cleaner split every line falls back to, before any override.</summary>
        public decimal AutomaticMinutesPerCleaner { get; set; }

        /// <summary>What the job was PRICED for. Compared against the assignment count for the mismatch warning.</summary>
        public int MaidsCount { get; set; }

        /// <summary>The order's default hourly rate — what a line with no override is paid at.</summary>
        public decimal OrderHourlyRate { get; set; }

        /// <summary>What the rate SHOULD be for this service type, per the shared calculator.</summary>
        public decimal ExpectedHourlyRate { get; set; }

        /// <summary>Sum of the cleaner lines' salaries — what is written to Order.CleanerTotalSalary.</summary>
        public decimal TotalSalary { get; set; }

        /// <summary>Salaries + tips: everything that leaves the company for this order.</summary>
        public decimal TotalPayout { get; set; }

        public List<OutgoingPaymentCleanerDto> Cleaners { get; set; } = new();

        /// <summary>Human-readable problems a SuperAdmin should look at before paying. Never blocks paying.</summary>
        public List<string> Warnings { get; set; } = new();

        /// <summary>True when every assigned cleaner has been paid (and there is at least one).</summary>
        public bool IsFullyPaid { get; set; }

        /// <summary>True when at least one, but not every, cleaner has been paid.</summary>
        public bool IsPartiallyPaid { get; set; }
    }

    /// <summary>What one cleaner is owed for one order, and whether they have had it.</summary>
    public class OutgoingPaymentCleanerDto
    {
        public int OrderCleanerId { get; set; }
        public int CleanerId { get; set; }
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;

        /// <summary>The cleaner's saved payout method — a hint for whoever sends the money.</summary>
        public CleanerPaymentMethod? PaymentMethod { get; set; }
        public string? PaymentDetails { get; set; }

        public decimal BillableMinutes { get; set; }
        public bool HoursOverridden { get; set; }

        public decimal HourlyRate { get; set; }
        public bool RateOverridden { get; set; }

        /// <summary>Rate is off the service type's default — surfaced per cleaner, not just per order.</summary>
        public bool RateDiffersFromDefault { get; set; }

        public decimal Salary { get; set; }
        public decimal Tips { get; set; }

        /// <summary>Salary + tips: what actually gets handed to this person.</summary>
        public decimal Payout { get; set; }

        public bool IsPaid { get; set; }

        /// <summary>What was actually handed over, frozen at pay time. Null until paid.</summary>
        public decimal? PaidAmount { get; set; }
        public CleanerPaymentMethod? PaidVia { get; set; }
        public DateTime? PaidAt { get; set; }
        public string? PaidByName { get; set; }
        public string? PaymentNote { get; set; }
    }

    /// <summary>Totals across everything the current filter matched — the page header.</summary>
    public class OutgoingPaymentSummaryDto
    {
        public int OrderCount { get; set; }
        public int CleanerLineCount { get; set; }
        public decimal TotalSalary { get; set; }
        public decimal TotalTips { get; set; }
        public decimal TotalPayout { get; set; }

        /// <summary>Still owed — the number that says how much money has to leave today.</summary>
        public decimal UnpaidPayout { get; set; }
        public decimal PaidPayout { get; set; }
        public int UnpaidCleanerCount { get; set; }

        /// <summary>How many orders in range carry at least one warning.</summary>
        public int OrdersWithWarnings { get; set; }
    }

    public class OutgoingPaymentListDto
    {
        public List<OutgoingPaymentOrderDto> Orders { get; set; } = new();
        public OutgoingPaymentSummaryDto Summary { get; set; } = new();
        public int TotalCount { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
    }

    /// <summary>
    /// Edits one cleaner's pay on one order. Both fields are nullable and null means "clear the
    /// override and go back to tracking the order" — which is why they are separate booleans
    /// away from a partial-update convention: sending nothing must not silently clear anything.
    /// </summary>
    public class UpdateCleanerPayrollDto
    {
        /// <summary>New hourly rate for this cleaner on this order. Null clears the override.</summary>
        [Range(0, 1000)]
        public decimal? HourlyRate { get; set; }

        /// <summary>New paid minutes for this cleaner on this order. Null clears the override.</summary>
        [Range(0, 10080)]
        public decimal? BillableMinutes { get; set; }

        /// <summary>Set true to APPLY the HourlyRate field (including clearing it with null).</summary>
        public bool UpdateHourlyRate { get; set; }

        /// <summary>Set true to APPLY the BillableMinutes field (including clearing it with null).</summary>
        public bool UpdateBillableMinutes { get; set; }
    }

    /// <summary>
    /// Changes the ORDER's hourly rate — the default every assigned cleaner without a
    /// per-cleaner override is paid at. Writes through to <c>Order.CleanerHourlyRate</c>, so the
    /// order itself carries the new rate and the reported labour cost follows.
    /// </summary>
    public class UpdateOrderHourlyRateDto
    {
        [Range(0, 1000)]
        public decimal HourlyRate { get; set; }
    }

    /// <summary>Marks one cleaner paid for one order.</summary>
    public class MarkCleanerPaidDto
    {
        /// <summary>How the money was sent. Defaults to the cleaner's saved method when omitted.</summary>
        public CleanerPaymentMethod? PaidVia { get; set; }

        [StringLength(500)]
        public string? PaymentNote { get; set; }
    }

    /// <summary>Marks every not-yet-paid cleaner on one order paid, each via their own saved method.</summary>
    public class MarkOrderPaidDto
    {
        [StringLength(500)]
        public string? PaymentNote { get; set; }
    }
}
