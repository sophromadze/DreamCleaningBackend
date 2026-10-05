using System.Collections.Concurrent;
using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// Single source of truth for "which SpecialOffer row is THE first-time customer offer"
    /// and how its discount is written for customers. Mirrored by
    /// <c>shared/booking/special-offer-keys.ts</c> on the frontend.
    ///
    /// <para>
    /// THE KEY DECIDES: the first-time offer is the one whose <see cref="SpecialOffer.OfferKey"/> is
    /// <see cref="Key"/> ("first-time"). Neither the Name (editable) nor the Type is reliable -
    /// production's first-time offer is a <see cref="OfferType.Custom"/> row, and the admin panel
    /// refuses to change an existing offer's Type - and RequiresFirstTimeCustomer is an eligibility
    /// rule any offer can carry, not an identity.
    /// </para>
    ///
    /// <para>
    /// Two callers, two rules (same split as the extra-service keys):
    /// <list type="bullet">
    /// <item><see cref="IsFirstTimeOffer"/> - an operational per-row question. A keyed row is judged
    /// by its key only; an UNKEYED row falls back to the old broad predicate (flag, Type or
    /// "first time" in the name) and the first such fallback per name logs one warning.</item>
    /// <item><see cref="Find"/> - the marketing question "which discount do we ADVERTISE" (pricing
    /// page copy, emails, the chat agent's live figures). Key ONLY: with no keyed active offer it
    /// returns null and the caller drops the discount sentence rather than guess.</item>
    /// </list>
    /// </para>
    ///
    /// There is NO default percentage anywhere: when no offer matches, callers must omit the
    /// discount line rather than invent a number.
    /// </summary>
    public static class FirstTimeOfferHelper
    {
        /// <summary>The OfferKey of the first-time customer offer.</summary>
        public const string Key = "first-time";

        /// <summary>Set once at startup (Program.cs); null in unit tests, which then just skip the log.</summary>
        public static ILogger? Logger { get; set; }

        private static readonly ConcurrentDictionary<string, byte> Warned = new();

        /// <summary>True when the offer carries the first-time key (whitespace-trimmed, exact).</summary>
        public static bool HasKey(SpecialOffer? offer) =>
            offer != null && string.Equals(offer.OfferKey?.Trim(), Key, StringComparison.Ordinal);

        /// <summary>
        /// Key first: a keyed offer is the first-time offer exactly when its key is "first-time". An
        /// unkeyed one is judged by <see cref="LegacyIsFirstTimeOffer"/>, and a match there is logged once.
        /// </summary>
        public static bool IsFirstTimeOffer(SpecialOffer offer)
        {
            if (!string.IsNullOrWhiteSpace(offer.OfferKey))
                return HasKey(offer);

            if (!LegacyIsFirstTimeOffer(offer)) return false;

            var name = offer.Name?.Trim() ?? "";
            if (Warned.TryAdd(name, 0))
            {
                Logger?.LogWarning(
                    "[special offer keys] Special offer \"{Name}\" (Id {Id}) has no OfferKey; treated it as the first-time offer " +
                    "by its flag/type/name. Set its key to \"{Key}\" in Admin > Special Offers.", name, offer.Id, Key);
            }
            return true;
        }

        /// <summary>
        /// The rule before keys existed, kept verbatim for unkeyed rows: the RequiresFirstTimeCustomer
        /// flag, the FirstTime type, or "first time" / "first-time" in the name.
        /// </summary>
        public static bool LegacyIsFirstTimeOffer(SpecialOffer offer) =>
            offer.RequiresFirstTimeCustomer ||
            offer.Type == OfferType.FirstTime ||
            (offer.Name != null &&
                (offer.Name.Contains("first time", StringComparison.OrdinalIgnoreCase) ||
                 offer.Name.Contains("first-time", StringComparison.OrdinalIgnoreCase)));

        /// <summary>
        /// The ADVERTISED first-time offer out of an already-materialized list (callers pass the
        /// active, in-date offers): the first one keyed "first-time", or null. Key only - see the
        /// class summary.
        /// </summary>
        public static SpecialOffer? Find(IEnumerable<SpecialOffer> offers) =>
            offers.FirstOrDefault(HasKey);

        /// <summary>
        /// Protected from deletion: the keyed first-time offer, and (as before keys) any row of
        /// Type FirstTime.
        /// </summary>
        public static bool IsProtectedFromDeletion(SpecialOffer offer) =>
            HasKey(offer) || offer.Type == OfferType.FirstTime;

        /// <summary>Customer-facing discount label — "10%" or "$25". Null when there is nothing
        /// to advertise, so the caller drops the sentence entirely.</summary>
        public static string? FormatDiscountLabel(SpecialOffer? offer)
        {
            if (offer == null || offer.DiscountValue <= 0)
                return null;

            return OfferLabel(offer);
        }

        /// <summary>Same formatting for any offer (seasonal ones included).</summary>
        public static string OfferLabel(SpecialOffer offer) =>
            offer.IsPercentage ? $"{offer.DiscountValue:0.##}%" : $"${offer.DiscountValue:0.##}";

        /// <summary>For tests: forget which fallbacks were already logged.</summary>
        public static void ResetWarningsForTests() => Warned.Clear();
    }
}
