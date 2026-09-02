namespace DreamCleaningBackend.Helpers
{
    // Salary expenses name a PERSON, not a thing. Everything that follows from that lives here so
    // the service, the grouped view and the tests all answer the question the same way.
    //
    // The link is Expense.StaffUserId; the fallback is the Name column, which carries a SNAPSHOT of
    // the staff member's name written at save time. Both are needed, and neither is redundant:
    //
    //   • While the person is still in Users, their CURRENT name is displayed. Correcting a typo in
    //     an admin's surname fixes every salary row they ever appeared on, and rows written before
    //     and after the correction stay in ONE group instead of splitting in two.
    //   • Once the person is deleted, the snapshot is all that is left, and it is what the row keeps
    //     showing. A salary that was paid does not stop having been paid because the account went
    //     away — see StaffUserId's comment for why that column carries NO foreign key.
    public static class SalaryExpenseRules
    {
        // The seeded "Salaries" category. Its Id is the old enum value and is a fixed contract
        // (see ExpenseCategory) — the category can be renamed, so matching on the NAME would break
        // the moment the owner called it "Payroll".
        public const int SalariesCategoryId = 4;

        public static bool IsSalaryCategory(int categoryId) => categoryId == SalariesCategoryId;

        /// <summary>"First Last", collapsed to whichever half exists. Empty when neither does.</summary>
        public static string FormatStaffName(string? firstName, string? lastName)
            => $"{(firstName ?? string.Empty).Trim()} {(lastName ?? string.Empty).Trim()}".Trim();

        /// <summary>
        /// What the row is labelled with. <paramref name="liveName"/> is the linked user's current
        /// name, or null when the row has no link or that user no longer exists — in which case the
        /// stored snapshot stands.
        /// </summary>
        public static string ResolveDisplayName(string storedName, string? liveName)
            => string.IsNullOrWhiteSpace(liveName) ? (storedName ?? string.Empty).Trim() : liveName.Trim();

        /// <summary>
        /// How rows collapse into one line in the grouped view. A staff link groups by PERSON, so a
        /// deleted admin's rows stay together even if their name was spelled differently across the
        /// snapshots; everything else groups by name, exactly as it always has.
        /// </summary>
        /// <remarks>
        /// BOTH kinds are prefixed. Only the staff side needs a prefix to be unambiguous, but with
        /// the name side left bare an expense someone typed as "staff#7" landed in staff member 7's
        /// line — two unrelated things summed into one figure. The prefixes are what keep the two
        /// key spaces from ever meeting.
        /// </remarks>
        public static string GroupingKey(int? staffUserId, string name)
            => staffUserId.HasValue
                ? $"staff#{staffUserId.Value}"
                : $"name#{(name ?? string.Empty).Trim().ToLowerInvariant()}";
    }
}
