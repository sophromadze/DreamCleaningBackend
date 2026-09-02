using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.DTOs
{
    // Staff salaries on the Outgoing Payments page. Deliberately its own shape rather than another
    // OutgoingPaymentOrderDto: cleaner wages are per ORDER and derived from hours worked, while a
    // salary is per PERSON per MONTH and paid in two instalments. Forcing one into the other's
    // shape would mean a row with no order, no hours and no rate.

    /// <summary>One staff member's salary for one month, and what has been paid of it.</summary>
    public class AdminSalaryPayoutDto
    {
        /// <summary>Stable identity for the person — see SalaryExpenseRules.GroupingKey.</summary>
        public string PayeeKey { get; set; } = string.Empty;

        /// <summary>Null for a salary recorded against a typed name rather than an account.</summary>
        public int? StaffUserId { get; set; }

        public string Name { get; set; } = string.Empty;

        /// <summary>"SuperAdmin" / "Admin" / "Moderator", or null once they are no longer staff.</summary>
        public string? Role { get; set; }

        /// <summary>They no longer hold a staff role, but the month still owes them a salary.</summary>
        public bool IsFormerStaff { get; set; }

        /// <summary>
        /// Where the salary is actually sent — an IBAN, a card or an ID number. Free text, copied
        /// verbatim and never parsed. Null until somebody fills it in.
        /// </summary>
        public string? PaymentDetails { get; set; }

        /// <summary>The salary alone, in the currency it was entered in — bonuses excluded.</summary>
        public decimal SalaryTotal { get; set; }

        /// <summary>
        /// Staff bonuses earned in this month, in the salary's currency (converted when the two
        /// differ — bonus rates are always set in GEL). Paid with the SECOND instalment, at the
        /// end of the month, because that is when the month's work is known.
        /// </summary>
        public decimal BonusTotal { get; set; }

        /// <summary>The bonus as it is actually set and reported: GEL.</summary>
        public decimal BonusTotalGel { get; set; }
        public decimal BonusTotalUsd { get; set; }

        /// <summary>Salary + bonuses — what the month owes this person in total.</summary>
        public decimal MonthTotal { get; set; }
        public string Currency { get; set; } = "USD";

        /// <summary>The same figure in USD.</summary>
        public decimal MonthTotalUsd { get; set; }

        /// <summary>The rate the month converted at. Null for a salary already in USD.</summary>
        public decimal? UsdPerGel { get; set; }

        /// <summary>The two instalments, always exactly two, always in order.</summary>
        public List<AdminSalaryInstalmentDto> Instalments { get; set; } = new();

        public bool IsFullyPaid { get; set; }
        public bool IsPartiallyPaid { get; set; }

        /// <summary>Still owed this month, in <see cref="Currency"/>.</summary>
        public decimal UnpaidAmount { get; set; }

        /// <summary>
        /// Things worth knowing before sending money — e.g. the salary is entered in GEL but the
        /// month has no exchange rate yet. Never blocks anything, same as the staffing warnings.
        /// </summary>
        public List<string> Warnings { get; set; } = new();
    }

    /// <summary>One of the two monthly instalments.</summary>
    public class AdminSalaryInstalmentDto
    {
        /// <summary>1 = first payment of the month, 2 = second.</summary>
        public int Half { get; set; }

        /// <summary>"First payment" / "Second payment".</summary>
        public string Label { get; set; } = string.Empty;

        /// <summary>
        /// What is owed for this instalment, in <see cref="Currency"/> — <see cref="SalaryAmount"/>
        /// plus <see cref="BonusAmount"/>. Once paid this is the FROZEN figure that was actually
        /// handed over, which can differ from today's arithmetic if the salary or the month's
        /// bonuses moved afterwards.
        /// </summary>
        public decimal Amount { get; set; }
        public string Currency { get; set; } = "USD";
        public decimal AmountUsd { get; set; }

        /// <summary>This instalment's half of the salary, before bonuses.</summary>
        public decimal SalaryAmount { get; set; }

        /// <summary>
        /// Bonuses riding on this instalment. Always 0 on the first — the month's bonuses are only
        /// known once the month is done, so they are paid with the second.
        /// </summary>
        public decimal BonusAmount { get; set; }

        public bool IsPaid { get; set; }
        public DateTime? PaidAt { get; set; }
        public string? PaidByName { get; set; }
        public string? PaymentNote { get; set; }
    }

    /// <summary>The whole salaries view for one month.</summary>
    public class AdminSalaryPayoutListDto
    {
        public int Year { get; set; }
        public int Month { get; set; }
        public string MonthLabel { get; set; } = string.Empty;

        public List<AdminSalaryPayoutDto> Payees { get; set; } = new();

        /// <summary>USD totals — the one currency every line is comparable in.</summary>
        public decimal TotalUsd { get; set; }
        public decimal PaidUsd { get; set; }
        public decimal UnpaidUsd { get; set; }
        public int UnpaidInstalmentCount { get; set; }
    }

    /// <summary>Records one instalment as paid.</summary>
    public class MarkSalaryPaidDto
    {
        [StringLength(500)]
        public string? PaymentNote { get; set; }
    }

    /// <summary>
    /// Sets where an employee's salary is sent. Blank clears it — a destination that turns out to
    /// be wrong has to be removable, not just replaceable.
    /// </summary>
    public class UpdateSalaryPayeeDetailsDto
    {
        [StringLength(200)]
        public string? PaymentDetails { get; set; }
    }
}
