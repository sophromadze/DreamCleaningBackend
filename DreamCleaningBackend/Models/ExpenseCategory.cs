using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.Models
{
    // Company-expense category. Previously a fixed enum; now a database table so the owner
    // can add/rename/delete their own categories from the UI.
    //
    // The first seven rows are seeded as "system" categories. Their Ids intentionally match
    // the OLD enum values so the existing Expenses.Category column (now the CategoryId FK)
    // keeps pointing at the right category with no data migration:
    //   0 Subscriptions, 1 Supplies, 2 Infrastructure, 3 Marketing, 4 Salaries, 5 Other,
    //   6 Sales (new).
    // System rows can be renamed but not deleted. Custom rows get Ids 7+ (assigned in the
    // service as max(Id)+1 — the PK is ValueGeneratedNever so we never collide with the
    // hand-picked system Ids above).
    public class ExpenseCategory
    {
        [Key]
        public int Id { get; set; }

        [Required]
        [StringLength(100)]
        public string Name { get; set; } = string.Empty;

        // Display order in the breakdown / dropdown. Lower sorts first.
        public int DisplayOrder { get; set; }

        // Seeded categories are protected from deletion.
        public bool IsSystem { get; set; } = false;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public virtual ICollection<Expense> Expenses { get; set; } = new List<Expense>();
    }
}
