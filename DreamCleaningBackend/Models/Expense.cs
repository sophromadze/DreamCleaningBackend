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

        // FK to ExpenseCategory. Mapped to the legacy "Category" column (which used to hold the
        // enum value) so the switch from enum to table needs no data migration — old int values
        // 0..5 line up with the seeded system-category Ids. See ExpenseCategory for the mapping.
        public int CategoryId { get; set; }

        [ForeignKey(nameof(CategoryId))]
        public virtual ExpenseCategory? Category { get; set; }

        // For one-time expenses this is the expense date.
        // For recurring expenses this is the *first* occurrence; subsequent occurrences are
        // computed as StartDate + k * FrequencyMonths.
        [Required]
        public DateTime StartDate { get; set; }

        public bool IsRecurring { get; set; } = false;

        // Required when IsRecurring is true. Number of months between occurrences.
        // Common values: 1 (monthly), 3 (quarterly), 6 (semi-annual), 12 (annual).
        public int? FrequencyMonths { get; set; }

        // Day-based proration. When true, the first and last (partial) calendar months are
        // charged only for the days the expense was actually active — e.g. Google Ads started
        // on May 15 with a $1000 monthly budget bills ~17/31 of $1000 in May. Only meaningful
        // for monthly recurring expenses (IsRecurring && FrequencyMonths == 1). When false a
        // recurring expense charges its full Amount on every occurrence (e.g. a $20 Claude
        // subscription charges the whole $20 even if it started mid-month).
        public bool ProrateByDay { get; set; } = false;

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
