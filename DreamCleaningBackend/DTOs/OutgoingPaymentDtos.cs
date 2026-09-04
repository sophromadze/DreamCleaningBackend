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

        /// <summary>What the job was PRICED for.</summary>
        public int MaidsCount { get; set; }

        /// <summary>
        /// How many people the work was split across — <c>max(MaidsCount, assigned)</c>. This is
        /// what "· 6h each cleaner" is derived from, NOT the assignment count.
        /// </summary>
        public int SplitCount { get; set; }

        /// <summary>The order's default hourly rate — what a line with no override is paid at.</summary>
        public decimal OrderHourlyRate { get; set; }

        /// <summary>What the rate SHOULD be for this service type, per the shared calculator.</summary>
        public decimal ExpectedHourlyRate { get; set; }

        /// <summary>Sum of the cleaner lines' salaries — what is written to Order.CleanerTotalSalary.</summary>
        public decimal TotalSalary { get; set; }

        /// <summary>Salaries + tips: everything that leaves the company for this order.</summary>
        public decimal TotalPayout { get; set; }

        public List<OutgoingPaymentCleanerDto> Cleaners { get; set; } = new();

        /// <summary>
        /// Staffing slots nobody is assigned to. Somebody worked those hours — they are just not
        /// in the system — so their pay is reported here at the same hours and rate, counted in
        /// TotalSalary/TotalPayout, and CAN be marked paid like any other line (the record lives
        /// on OrderUnassignedPayout, keyed by SlotIndex, since there is no cleaner to key on).
        ///
        /// Kept OUT of <see cref="Cleaners"/> so that anything walking the assignment list —
        /// per-cleaner rate/hours edits, cleaner-name search — cannot trip over a line with no
        /// cleaner behind it. IsFullyPaid deliberately spans BOTH lists.
        /// </summary>
        public List<OutgoingPaymentCleanerDto> UnassignedCleaners { get; set; } = new();

        /// <summary>Human-readable problems a SuperAdmin should look at before paying. Never blocks paying.</summary>
        public List<string> Warnings { get; set; } = new();

        /// <summary>
        /// True when every payout line is SETTLED — paid, and still covered by what was paid.
        /// A line whose hours grew after payment un-settles it, so an order that gained an hour
        /// after everyone was paid stops reading "Paid" and goes back to "Part paid".
        /// </summary>
        public bool IsFullyPaid { get; set; }

        /// <summary>
        /// True when some money has gone out on this order but something is still owed —
        /// including the case where every line was paid and a later edit left a shortfall on it.
        /// </summary>
        public bool IsPartiallyPaid { get; set; }

        /// <summary>Everything still to hand over on this order: unpaid lines plus any shortfalls.</summary>
        public decimal OutstandingPayout { get; set; }

        /// <summary>
        /// The part of <see cref="OutstandingPayout"/> owed on lines that were ALREADY PAID —
        /// the extra money the order's edit created. Zero on an order nobody has been paid for,
        /// which is what keeps top-up wording off a plainly unpaid order.
        /// </summary>
        public decimal TopUpPayout { get; set; }

        /// <summary>Any line paid above what it is now worth. Advisory; nothing is clawed back.</summary>
        public decimal OverpaidAmount { get; set; }
    }

    /// <summary>What one cleaner is owed for one order, and whether they have had it.</summary>
    public class OutgoingPaymentCleanerDto
    {
        /// <summary>0 on an unassigned slot — there is no assignment row behind it.</summary>
        public int OrderCleanerId { get; set; }
        public int CleanerId { get; set; }
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;

        /// <summary>
        /// True for a staffing slot with nobody assigned. The figures are real — somebody worked
        /// those hours — and the slot CAN be marked paid; it just cannot have its rate or hours
        /// edited, because there is no per-cleaner record to hang an override on.
        /// </summary>
        public bool IsUnassigned { get; set; }

        /// <summary>
        /// Unassigned slots only: which slot this is, 0-based. This is what the pay/unpay
        /// endpoints address the line by, in place of an assignment id.
        /// </summary>
        public int SlotIndex { get; set; }

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

        /// <summary>
        /// What was actually handed over, frozen at pay time and never re-derived. Null until
        /// paid. A TOP-UP adds to it, so it always reads as the total this person has had for
        /// this order — see <see cref="Helpers.CleanerPayoutSettlement"/>.
        /// </summary>
        public decimal? PaidAmount { get; set; }
        public CleanerPaymentMethod? PaidVia { get; set; }
        public DateTime? PaidAt { get; set; }
        public string? PaidByName { get; set; }
        public string? PaymentNote { get; set; }

        // ===== Settlement: is the money that went out still enough? =====
        //
        // Derived from PaidAmount vs Payout by Helpers.CleanerPayoutSettlement, which is also
        // what decides the order's pill and the header total. The component renders these and
        // recomputes nothing.

        /// <summary>
        /// Still to hand over: the whole payout on an unpaid line, the SHORTFALL on a line whose
        /// hours grew after it was paid. Never negative.
        /// </summary>
        public decimal OutstandingPayout { get; set; }

        /// <summary>
        /// Paid ABOVE what the line is now worth — hours edited down after payment. Reported so
        /// somebody can sort it out; nothing nets it off another line.
        /// </summary>
        public decimal OverpaidAmount { get; set; }

        /// <summary>Nothing left to pay on this line.</summary>
        public bool IsSettled { get; set; }

        /// <summary>
        /// Already paid once and worth more now. This is what turns on the "still to pay"
        /// wording — an ordinary unpaid line must never show it.
        /// </summary>
        public bool IsTopUp { get; set; }
    }

    /// <summary>Totals across everything the current filter matched — the page header.</summary>
    public class OutgoingPaymentSummaryDto
    {
        public int OrderCount { get; set; }
        public int CleanerLineCount { get; set; }
        public decimal TotalSalary { get; set; }
        public decimal TotalTips { get; set; }
        public decimal TotalPayout { get; set; }

        /// <summary>
        /// Still owed — the number that says how much money has to leave today. Counts unpaid
        /// lines in full AND the shortfall on lines paid before their hours grew, so a top-up
        /// created by an order edit cannot hide from the header.
        /// </summary>
        public decimal UnpaidPayout { get; set; }
        public decimal PaidPayout { get; set; }

        /// <summary>The part of <see cref="UnpaidPayout"/> owed on already-paid lines.</summary>
        public decimal TopUpPayout { get; set; }

        /// <summary>How many lines still owe something — unpaid ones and shortfalls alike.</summary>
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
