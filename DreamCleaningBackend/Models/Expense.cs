using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using DreamCleaningBackend.Helpers;

namespace DreamCleaningBackend.Models
{
    // Company expense entry. One row represents either a single (one-time) expense or
    // a recurring subscription. Recurring rows are *virtually* expanded into per-occurrence
    // amounts when statistics are computed — we never materialize child rows. That way:
    //   - editing the amount on a subscription updates every past and future occurrence
    //   - cancelling means setting EndDate (history is preserved)
    //   - the DB stays tiny even for years of monthly subscriptions
    //
    // Amount is in USD to match the company-revenue currency, EXCEPT on a salary, which may be
    // entered in GEL and is converted for reporting at read time — see the Currency column. (The
    // admin per-order bonus is a separate GEL cost entirely and is not an Expense row at all.)
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

        // The staff member this salary is paid to. Only meaningful on the Salaries category (see
        // SalaryExpenseRules); forced to null on every other category, so a link can never be left
        // behind by moving a row to Supplies.
        //
        // Deliberately carries NO foreign key. Every other User reference in this model is Restrict,
        // which BLOCKS deleting the person — right for an order's bonus attribution, wrong here: the
        // owner wants to remove someone from Users and still see what the company paid them, and a
        // Restrict would answer that with "use Block instead". SetNull is no better, because it
        // erases the one thing that keeps a deleted person's rows grouped together. So the id stays
        // as a plain historical reference that outlives the row it names, and Name carries the
        // snapshot displayed once the account is gone. Auto-increment ids are never reused, so a
        // stale id can only fail to resolve — it can never resolve to somebody else.
        public int? StaffUserId { get; set; }

        // The currency Amount was ENTERED in — "USD" or "GEL" (see ExpenseCurrency). Only a salary
        // may be anything but USD; every other category is forced to USD on write.
        //
        // Amount is NEVER stored converted. The owner pays their admins a round number of lari, and
        // that number has to stay legible on the page they typed it into; the USD figure that
        // reaches Statistics and Finances is derived at read time from the month's locked FX
        // snapshot. Storing dollars instead would freeze one month's rate into the row and make a
        // corrected rate unable to restate the months it applied to.
        [Required]
        [StringLength(3)]
        public string Currency { get; set; } = ExpenseCurrency.Usd;

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

        // Idempotency marker for externally-synced expenses. NULL for manually-entered rows;
        // "googleads:yyyy-MM-dd" for a Google Ads daily-spend row. A UNIQUE index on this column
        // (see ApplicationDbContext) guarantees exactly one row per ad-spend day and makes
        // re-syncs an upsert-by-key. On MySQL/MariaDB a UNIQUE index allows many NULLs, so manual
        // expenses are unaffected. Kept short/bounded so the column maps to varchar (indexable),
        // not TEXT (which can't be uniquely indexed without a prefix length).
        [StringLength(100)]
        public string? SourceKey { get; set; }

        public int CreatedByUserId { get; set; }

        [ForeignKey("CreatedByUserId")]
        public virtual User CreatedByUser { get; set; } = null!;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
