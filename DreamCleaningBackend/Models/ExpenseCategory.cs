namespace DreamCleaningBackend.Models
{
    // Fixed category list for company expenses. Order is the display order in the breakdown.
    // Values are stable — never renumber without a data migration.
    public enum ExpenseCategory
    {
        Subscriptions = 0,
        Supplies = 1,
        Infrastructure = 2,
        Marketing = 3,
        Salaries = 4,
        Other = 5
    }
}
