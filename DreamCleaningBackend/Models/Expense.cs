using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    // Company expense entry. One row represents either a single (one-time) expense or
    // a recurring subscription. Recurring rows are *virtually* expanded into per-occurrence
    // amounts when statistics are computed — we never materialize child rows. That way:
    //   - editing the amount on a subscription updates every past and future occurrence
    //   - cancelling means setting EndDate (history is preserved)
    //   - the DB stays tiny even for years of monthly subscriptions
    //
    // Amount is in USD to match the company-revenue currency (admin per-order bonus stays
    // in GEL and is unrelated to this).
    public class Expense
    {
        [Key]
        public int Id { get; set; }

        [Required]
        [StringLength(200)]
        public string Name { get; set; } = string.Empty;

        [Column(TypeName = "decimal(18,2)")]
        public decimal Amount { get; set; }

        public ExpenseCategory Category { get; set; } = ExpenseCategory.Other;

        // For one-time expenses this is the expense date.
        // For recurring expenses this is the *first* occurrence; subsequent occurrences are
        // computed as StartDate + k * FrequencyMonths.
        [Required]
        public DateTime StartDate { get; set; }

        public bool IsRecurring { get; set; } = false;

        // Required when IsRecurring is true. Number of months between occurrences.
        // Common values: 1 (monthly), 3 (quarterly), 6 (semi-annual), 12 (annual).
        public int? FrequencyMonths { get; set; }

        // Optional cancellation point for recurring expenses. When set, occurrences strictly
        // after this date are excluded from statistics. Past occurrences are still counted.
        public DateTime? EndDate { get; set; }

        [StringLength(1000)]
        public string? Notes { get; set; }

        public int CreatedByUserId { get; set; }

        [ForeignKey("CreatedByUserId")]
        public virtual User CreatedByUser { get; set; } = null!;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
