namespace DreamCleaningBackend.DTOs
{
    /// <summary>
    /// The cleaner-wage breakdown behind an order's "Cleaners Total Salary", for the admin Orders
    /// panel.
    ///
    /// It exists so the Orders panel and the Outgoing Payments page can never quote different
    /// numbers for the same job again. The panel used to RECOMPUTE the total client-side from
    /// TotalDuration x MaidsCount x the order's single rate, which cannot see a per-cleaner rate
    /// or hours override — order #315 read $200 there against a $175 payout sheet (2026-08-31).
    /// Both screens now derive from CleanerPayrollCalculator, and the panel renders the lines
    /// underneath the total so the arithmetic is checkable on the spot.
    ///
    /// Deliberately NOT part of OrderDto: OrderDtoMapper is shared with the CUSTOMER-facing order
    /// details endpoint, and cleaner wages are none of a customer's business.
    ///
    /// Deliberately NOT the same shape as OutgoingPaymentCleanerDto either. That one carries
    /// payment state and the cleaner's PaymentDetails — their bank or Zelle destination — because
    /// that page exists to send money. This one is read-only context on an order, so it carries
    /// no payment destination and no paid/unpaid state; there is nothing here to act on.
    /// </summary>
    public class OrderCleanerPayrollDto
    {
        public int OrderId { get; set; }

        /// <summary>Sum of every line below, assigned and unassigned. Equals Order.CleanerTotalSalary.</summary>
        public decimal TotalSalary { get; set; }

        /// <summary>What Order.CleanerTotalSalary actually holds right now.</summary>
        /// <remarks>
        /// Shipped alongside <see cref="TotalSalary"/> rather than assumed equal, so the panel can
        /// say so when they disagree. They should never disagree — every write path routes through
        /// CleanerPayrollCalculator.ApplyOrderTotalSalary — and a mismatch means a stale row from
        /// before that was true, or a write path that escaped it. Better surfaced than silent.
        /// </remarks>
        public decimal StoredTotalSalary { get; set; }

        /// <summary>How many people the work was split across — max(MaidsCount, assigned).</summary>
        public int SplitCount { get; set; }

        /// <summary>How many assignment rows the order has. Zero means nobody is on it yet.</summary>
        public int AssignedCount { get; set; }

        /// <summary>The even per-cleaner split every line falls back to, before any override.</summary>
        public decimal AutomaticMinutesPerCleaner { get; set; }

        /// <summary>The order's default hourly rate — what a line with no override is paid at.</summary>
        public decimal OrderHourlyRate { get; set; }

        /// <summary>One line per assigned cleaner, in assignment order.</summary>
        public List<OrderCleanerPayrollLineDto> Lines { get; set; } = new();

        /// <summary>
        /// Staffing slots nobody is assigned to, at the automatic hours and the order rate. Kept
        /// separate from <see cref="Lines"/> for the same reason the payouts page does it — there
        /// is no cleaner behind them — but they ARE counted in <see cref="TotalSalary"/>, which is
        /// exactly why the panel has to show them: otherwise the lines would not add up.
        /// </summary>
        public List<OrderCleanerPayrollLineDto> UnassignedLines { get; set; } = new();
    }

    /// <summary>What one cleaner (or one unstaffed slot) is owed in WAGES on this order.</summary>
    public class OrderCleanerPayrollLineDto
    {
        public int CleanerId { get; set; }
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;

        /// <summary>True for a staffing slot with nobody assigned; the name fields are then empty.</summary>
        public bool IsUnassignedSlot { get; set; }

        /// <summary>Minutes this line is paid for — the override, or the automatic split.</summary>
        public decimal BillableMinutes { get; set; }

        /// <summary>An admin set these hours by hand on the Outgoing Payments page ("EDITED").</summary>
        public bool HoursOverridden { get; set; }

        public decimal HourlyRate { get; set; }

        /// <summary>An admin set this rate by hand on the Outgoing Payments page ("OWN RATE").</summary>
        public bool RateOverridden { get; set; }

        /// <summary>BillableMinutes / 60 x HourlyRate. Wages only — tips are not included here,
        /// because tips are the customer's money passing through and are never part of the
        /// order's reported labour cost.</summary>
        public decimal Salary { get; set; }
    }
}
