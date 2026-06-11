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
        public decimal Amount { get; set; }
        public int CategoryId { get; set; }
        public string CategoryName { get; set; } = string.Empty;
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
        [Required]
        [StringLength(200)]
        public string Name { get; set; } = string.Empty;

        [Range(0, double.MaxValue)]
        public decimal Amount { get; set; }

        public int CategoryId { get; set; }

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
        public string Name { get; set; } = string.Empty;
        public int CategoryId { get; set; }
        public string CategoryName { get; set; } = string.Empty;
        public DateTime Date { get; set; }
        public decimal Amount { get; set; }
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
        public decimal MonthTotal { get; set; }
        public decimal AllTimeTotal { get; set; }
        public List<ExpenseDto> Entries { get; set; } = new();
    }
}
