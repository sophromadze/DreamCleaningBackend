using System.Text.Json;

namespace DreamCleaningBackend.Services
{
    /// <summary>
    /// Single source of truth for the restricted admin pages that a SuperAdmin can grant a
    /// regular Admin read-only ("view") access to. The string keys are mirrored on the frontend
    /// in <c>src/app/shared/admin-viewable-pages.ts</c> — keep both lists in sync.
    ///
    /// Adding a future page = add a const here + mirror it on the frontend + apply
    /// <c>[RequirePageView(...)]</c> to that page's GET endpoints + wire its read-only mode.
    /// </summary>
    public static class AdminViewablePages
    {
        public const string Statistics = "statistics";
        public const string Expenses = "expenses";
        public const string BubbleRewards = "bubble-rewards";

        public static readonly IReadOnlyList<string> All = new[]
        {
            Statistics,
            Expenses,
            BubbleRewards
        };

        public static bool IsValid(string? key) => key != null && All.Contains(key);

        /// <summary>Parses the raw JSON column value into a normalized list of valid page keys.</summary>
        public static List<string> Parse(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return new List<string>();

            List<string>? keys;
            try
            {
                keys = JsonSerializer.Deserialize<List<string>>(raw);
            }
            catch (JsonException)
            {
                return new List<string>();
            }

            return keys == null
                ? new List<string>()
                : keys.Where(IsValid).Distinct().ToList();
        }
    }
}
