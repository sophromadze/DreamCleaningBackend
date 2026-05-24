using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.DTOs
{
    // Raw expense row (the database record). Used by the management UI.
    public class ExpenseDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public ExpenseCategory Category { get; set; }
        public string CategoryName { get; set; } = string.Empty;
        public DateTime StartDate { get; set; }
        public bool IsRecurring { get; set; }
        public int? FrequencyMonths { get; set; }
        public DateTime? EndDate { get; set; }
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

        public ExpenseCategory Category { get; set; } = ExpenseCategory.Other;

        [Required]
        public DateTime StartDate { get; set; }

        public bool IsRecurring { get; set; } = false;

        // Required when IsRecurring=true. Validated at the service layer.
        public int? FrequencyMonths { get; set; }

        public DateTime? EndDate { get; set; }

        [StringLength(1000)]
        public string? Notes { get; set; }
    }

    public class UpdateExpenseDto : CreateExpenseDto
    {
    }

    // One projected occurrence of an expense within a date window. Recurring rows produce
    // many of these; one-time rows produce zero or one. Used by statistics for per-day attribution.
    public class ExpenseOccurrenceDto
    {
        public int ExpenseId { get; set; }
        public string Name { get; set; } = string.Empty;
        public ExpenseCategory Category { get; set; }
        public string CategoryName { get; set; } = string.Empty;
        public DateTime Date { get; set; }
        public decimal Amount { get; set; }
        public bool IsRecurring { get; set; }
    }

    public class ExpenseCategoryBreakdownDto
    {
        public ExpenseCategory Category { get; set; }
        public string CategoryName { get; set; } = string.Empty;
        public decimal Total { get; set; }
        public List<ExpenseOccurrenceDto> Items { get; set; } = new();
    }

    public class ExpenseBreakdownDto
    {
        public decimal Total { get; set; }
        public List<ExpenseCategoryBreakdownDto> ByCategory { get; set; } = new();
    }
}
