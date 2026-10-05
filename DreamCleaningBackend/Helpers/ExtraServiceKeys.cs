using System.Collections.Concurrent;
using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// The ExtraService.ExtraServiceKey values code looks for, and the ONE way to ask "is this extra
    /// that one?". Mirrored by <c>shared/booking/extra-service-keys.ts</c> on the frontend.
    ///
    /// <para>
    /// THE KEY DECIDES WHEN IT IS SET. Only an extra with NO key (a row nobody has keyed yet - a
    /// database the AddExtraServiceKey fill did not recognise, or an extra created since) falls back
    /// to the name match each call site has always used, and the first such fallback per
    /// (key, name) logs one warning so the gap is visible. A keyed extra is never matched by name,
    /// which is the point: renaming it in admin no longer changes what it does.
    /// </para>
    ///
    /// <para>
    /// Deep / Super Deep and Same Day are NOT recognised through keys: their own flags
    /// (IsDeepCleaning, IsSuperDeepCleaning, IsSameDayService) already are the stable identity.
    /// </para>
    ///
    /// <para>
    /// Marketing (the FAQ prices) does not use the fallback - it reads the key only and drops the
    /// price when it is missing; that lives in the frontend's marketing-prices.ts.
    /// </para>
    /// </summary>
    public static class ExtraServiceKeys
    {
        public const string DeepCleaning = "deep-cleaning";
        public const string SameDay = "same-day";
        public const string ExtraCleaners = "extra-cleaners";
        public const string ExtraMinutes = "extra-minutes";
        public const string CleaningSupplies = "cleaning-supplies";
        public const string CleaningEssentials = "cleaning-essentials";
        public const string VacuumCleaner = "vacuum-cleaner";
        public const string Oven = "oven";

        /// <summary>Set once at startup (Program.cs); null in unit tests, which then just skip the log.</summary>
        public static ILogger? Logger { get; set; }

        private static readonly ConcurrentDictionary<string, byte> Warned = new();

        /// <summary>
        /// Key first: a keyed extra is <paramref name="key"/> exactly when its key is. An unkeyed one
        /// is judged by <paramref name="legacyNameMatch"/> (given the trimmed name), and a match there
        /// is logged once.
        /// </summary>
        public static bool Is(string? extraServiceKey, string? name, string key, Func<string, bool> legacyNameMatch)
        {
            if (!string.IsNullOrWhiteSpace(extraServiceKey))
                return string.Equals(extraServiceKey.Trim(), key, StringComparison.Ordinal);

            var trimmed = name?.Trim();
            if (string.IsNullOrEmpty(trimmed) || !legacyNameMatch(trimmed)) return false;

            if (Warned.TryAdd($"{key}\u0000{trimmed}", 0))
            {
                Logger?.LogWarning(
                    "[extra service keys] Extra service \"{Name}\" has no ExtraServiceKey; matched it as \"{Key}\" by name. " +
                    "Set its key in Admin > Services > Extra Services.", trimmed, key);
            }
            return true;
        }

        public static bool Is(ExtraService? extra, string key, Func<string, bool> legacyNameMatch) =>
            extra != null && Is(extra.ExtraServiceKey, extra.Name, key, legacyNameMatch);

        // The legacy name rules, exactly as each call site used them before keys existed. Kept in
        // one place so every fallback for a key agrees.

        /// <summary>Exact name "Extra Cleaners" (OrderPricingCalculator's long-standing rule).</summary>
        public static bool LegacyExtraCleaners(string name) =>
            string.Equals(name, "Extra Cleaners", StringComparison.OrdinalIgnoreCase);

        public static bool LegacyCleaningSupplies(string name) =>
            name.Contains("cleaning supplies", StringComparison.OrdinalIgnoreCase);

        public static bool LegacyCleaningEssentials(string name) =>
            name.Contains("cleaning essentials", StringComparison.OrdinalIgnoreCase);

        public static bool LegacyVacuum(string name) =>
            name.Contains("vacuum", StringComparison.OrdinalIgnoreCase);

        public static bool LegacyOven(string name) =>
            name.Contains("oven", StringComparison.OrdinalIgnoreCase);

        /// <summary>"deep cleaning" in the name - also matches "Super Deep Cleaning", as it always did.</summary>
        public static bool LegacyDeepCleaning(string name) =>
            name.Contains("deep cleaning", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Deep or Super Deep: the flags decide, and an UNKEYED row whose flags are both off still
        /// counts when its name says "deep cleaning" (historical rows predating the flags).
        /// </summary>
        public static bool IsDeepOrSuperDeep(ExtraService? extra) =>
            extra != null
            && (extra.IsDeepCleaning || extra.IsSuperDeepCleaning
                || Is(extra.ExtraServiceKey, extra.Name, DeepCleaning, LegacyDeepCleaning));

        /// <summary>Super Deep: the flag, or an unkeyed row named "super deep ...".</summary>
        public static bool IsSuperDeep(ExtraService? extra) =>
            extra != null
            && (extra.IsSuperDeepCleaning
                || (string.IsNullOrWhiteSpace(extra.ExtraServiceKey) && !extra.IsDeepCleaning
                    && (extra.Name ?? "").Contains("super deep", StringComparison.OrdinalIgnoreCase)));
    }
}
