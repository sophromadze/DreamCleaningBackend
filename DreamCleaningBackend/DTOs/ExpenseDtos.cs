using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.DTOs
{
    // Raw expense row (the database record). Used by the management UI.
    public class ExpenseDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;

        /// <summary>The amount as ENTERED, in <see cref="Currency"/>. Never converted.</summary>
        public decimal Amount { get; set; }

        /// <summary>"USD" or "GEL". Only a salary can be anything but USD.</summary>
        public string Currency { get; set; } = "USD";

        public int CategoryId { get; set; }
        public string CategoryName { get; set; } = string.Empty;

        // Salary rows only. StaffUserId is the link; Name already carries the display name
        // (the linked user's current one, or the stored snapshot once they are gone).
        // StaffUserRemoved says which of those two the caller is looking at, so the UI can mark a
        // former staff member instead of silently presenting them as current.
        public int? StaffUserId { get; set; }
        public bool StaffUserRemoved { get; set; }

        public DateTime StartDate { get; set; }
        public bool IsRecurring { get; set; }
        public int? FrequencyMonths { get; set; }
        public DateTime? EndDate { get; set; }
        public bool ProrateByDay { get; set; }
        public string? Notes { get; set; }
        public int CreatedByUserId { get; set; }
        public string? CreatedByUserName { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public class CreateExpenseDto
    {
        // On a salary row with a StaffUserId this is IGNORED — the server writes the staff member's
        // own name so the snapshot can never disagree with who was picked. It is still what names
        // every other expense, and what names a salary paid to somebody who has no account.
        [StringLength(200)]
        public string Name { get; set; } = string.Empty;

        [Range(0, double.MaxValue)]
        public decimal Amount { get; set; }

        // "USD" or "GEL". Accepted only on the Salaries category — everything else is forced to
        // USD server-side, so a mis-tagged supplier invoice can't report at ~2.7x its real cost.
        [StringLength(3)]
        public string? Currency { get; set; }

        public int CategoryId { get; set; }

        // Who this salary is for. Only accepted on the Salaries category; ignored elsewhere.
        // Null on a salary row means "not a staff member on file" and falls back to Name.
        public int? StaffUserId { get; set; }

        [Required]
        public DateTime StartDate { get; set; }

        public bool IsRecurring { get; set; } = false;

        // Required when IsRecurring=true. Validated at the service layer.
        public int? FrequencyMonths { get; set; }

        public DateTime? EndDate { get; set; }

        // Day-based proration of partial first/last months. Only valid for monthly recurring.
        public bool ProrateByDay { get; set; } = false;

        [StringLength(1000)]
        public string? Notes { get; set; }
    }

    public class UpdateExpenseDto : CreateExpenseDto
    {
    }

    // One projected occurrence of an expense within a date window. Recurring rows produce
    // many of these; one-time rows produce zero or one. Amount is already prorated when the
    // expense opts into day-based proration. Used by statistics for per-day attribution.
    public class ExpenseOccurrenceDto
    {
        public int ExpenseId { get; set; }
        // Already resolved for salary rows — the linked staff member's current name, or the stored
        // snapshot once that account is gone.
        public string Name { get; set; } = string.Empty;
        public int? StaffUserId { get; set; }
        public int CategoryId { get; set; }
        public string CategoryName { get; set; } = string.Empty;
        public DateTime Date { get; set; }

        /// <summary>
        /// USD — already converted from <see cref="Currency"/> at the occurrence month's locked FX
        /// rate. This is the figure Statistics and Finances add up, which is why the conversion
        /// happens here rather than at each of those call sites.
        /// </summary>
        public decimal Amount { get; set; }

        /// <summary>The same occurrence in the currency it was entered in. Display only.</summary>
        public decimal AmountInCurrency { get; set; }
        public string Currency { get; set; } = "USD";

        /// <summary>The rate used, when one was. Null for a USD row — nothing was converted.</summary>
        public decimal? UsdPerGel { get; set; }

        public bool IsRecurring { get; set; }
    }

    public class ExpenseCategoryBreakdownDto
    {
        public int CategoryId { get; set; }
        public string CategoryName { get; set; } = string.Empty;
        public decimal Total { get; set; }
        public List<ExpenseOccurrenceDto> Items { get; set; } = new();
    }

    public class ExpenseBreakdownDto
    {
        public decimal Total { get; set; }
        public List<ExpenseCategoryBreakdownDto> ByCategory { get; set; } = new();
    }

    // ── Category management ────────────────────────────────────────────────────

    public class ExpenseCategoryDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int DisplayOrder { get; set; }
        public bool IsSystem { get; set; }
        // How many expense rows currently reference this category (for the manage-UI delete guard).
        public int ExpenseCount { get; set; }
    }

    public class SaveExpenseCategoryDto
    {
        [Required]
        [StringLength(100)]
        public string Name { get; set; } = string.Empty;
    }

    // ── Grouped view (Category → Name → individual entries) ─────────────────────

    // The whole grouped view for one calendar month. MonthTotal and every category/name total
    // are scoped to that month (prorated). Each name additionally carries an all-time total and
    // the underlying expense rows that feed it.
    public class GroupedExpensesDto
    {
        public int Year { get; set; }
        public int Month { get; set; }
        public string MonthLabel { get; set; } = string.Empty; // e.g. "June 2026"
        public decimal MonthTotal { get; set; }
        public List<GroupedCategoryDto> Categories { get; set; } = new();
    }

    public class GroupedCategoryDto
    {
        public int CategoryId { get; set; }
        public string CategoryName { get; set; } = string.Empty;
        public int DisplayOrder { get; set; }
        public decimal MonthTotal { get; set; }
        public List<GroupedNameDto> Names { get; set; } = new();
    }

    // One distinct expense name within a category. MonthTotal is what hits the selected month;
    // AllTimeTotal is everything charged to date across every entry sharing this name; Entries
    // are the raw rows (e.g. the "$100 for 2 months" Claude row and the ongoing "$20" Claude row).
    public class GroupedNameDto
    {
        public string Name { get; set; } = string.Empty;
        // Set when this line is a staff member's salary. Grouping is then by PERSON rather than by
        // name, so their rows stay on one line across a rename or after their account is deleted.
        public int? StaffUserId { get; set; }
        // True when StaffUserId no longer resolves to a user — a former staff member, shown under
        // the name snapshotted on their rows.
        public bool StaffUserRemoved { get; set; }

        /// <summary>USD, like every other total on this page — a line can mix currencies.</summary>
        public decimal MonthTotal { get; set; }
        public decimal AllTimeTotal { get; set; }

        /// <summary>
        /// The month's total in the currency it was entered in, when every entry on this line
        /// shares ONE non-USD currency (set in <see cref="Currency"/>). Null otherwise — including
        /// for a line that mixes currencies, where there is no single figure to show and only the
        /// USD total is meaningful.
        /// </summary>
        public decimal? MonthTotalInCurrency { get; set; }
        public decimal? AllTimeTotalInCurrency { get; set; }
        public string? Currency { get; set; }

        public List<ExpenseDto> Entries { get; set; } = new();
    }

    // ── Salary staff picker ─────────────────────────────────────────────────────

    // A person a salary can be recorded against. The list is current staff PLUS anyone who already
    // has salary rows, so a leaver's final payment can still be entered against them.
    public class ExpenseStaffMemberDto
    {
        public int Id { get; set; }
        public string FullName { get; set; } = string.Empty;
        public string? Email { get; set; }
        // "SuperAdmin" / "Admin" / "Moderator", or null once they are no longer staff.
        public string? Role { get; set; }
        // False for a blocked account. Still selectable — being blocked from signing in says
        // nothing about whether they are owed a salary.
        public bool IsActive { get; set; }
        // They no longer hold a staff role (or the account is gone) but have salary rows on file.
        public bool IsFormer { get; set; }
        // How many salary rows already name them, so the picker can order familiar names first.
        public int SalaryEntryCount { get; set; }
    }
}
