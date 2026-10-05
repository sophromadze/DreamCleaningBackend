using System.Text.RegularExpressions;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// The rules for <c>ServiceType.ServiceKey</c>, stated once for every writer (the Booking
    /// Services create/update endpoints and the pricing-configuration import).
    ///
    /// <para>
    /// A key is how code recognises a service type without its Id or Name - both diverge between
    /// the local and production databases, and Name is editable. So a key is a lowercase word, or
    /// lowercase words joined by single hyphens ("residential", "move-in-out"), unique across all
    /// service types when set. An empty value means "no key" and is stored as NULL, which the
    /// unique index allows any number of.
    /// </para>
    ///
    /// <para>
    /// Uppercase is REJECTED, not silently lowered: the person typing it should see the exact key
    /// that code will look for, and a quiet rewrite is how "Move-In-Out" ends up saved as
    /// something nobody typed.
    /// </para>
    /// </summary>
    public static class ServiceTypeKeyPolicy
    {
        public const int MaxLength = 50;

        private static readonly Regex Shape = new(@"^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);

        /// <summary>Trims the input; blank (or missing) becomes null, meaning "no key".</summary>
        public static string? Normalize(string? raw) =>
            string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

        /// <summary>
        /// Null when <paramref name="key"/> (already normalized, non-null) is a valid key;
        /// otherwise the FIRST thing wrong with it, worded for the admin who typed it.
        /// </summary>
        public static string? DescribeProblem(string key)
        {
            if (key.Length > MaxLength)
                return $"Service key must be at most {MaxLength} characters.";

            if (key.Any(char.IsUpper))
                return "Service key must be lowercase (for example \"move-in-out\").";

            if (key.Any(char.IsWhiteSpace) || key.Contains('_'))
                return "Service key must use hyphens between words, not spaces or underscores (for example \"move-in-out\").";

            if (key.StartsWith('-') || key.EndsWith('-') || key.Contains("--"))
                return "Service key can't start or end with a hyphen, or contain two hyphens in a row.";

            if (!Shape.IsMatch(key))
                return "Service key may only contain lowercase letters, numbers and hyphens (for example \"move-in-out\").";

            return null;
        }

        /// <summary>Message for a key another service type already holds.</summary>
        public static string DescribeDuplicate(string key, string ownerName) =>
            $"Service key \"{key}\" is already used by \"{ownerName}\". Each service type needs its own key.";
    }
}
