using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// Single source of truth for "which SpecialOffer row is THE first-time customer offer"
    /// and how its discount is written for customers.
    ///
    /// The predicate is deliberately broad — the same one the pricing page, the homepage hero,
    /// the sticky CTA, the first-time popup and the chat agent's live-figures block use. Matching
    /// on <see cref="OfferType.FirstTime"/> alone is NOT enough: the admin panel refuses to change
    /// an existing offer's Type, so a first-time offer created as Custom/Seasonal (or one whose
    /// only marker is RequiresFirstTimeCustomer) stays that way forever. A narrow check silently
    /// misses it and any hardcoded fallback then advertises a discount we don't actually give.
    ///
    /// There is NO default percentage anywhere: when no offer matches, callers must omit the
    /// discount line rather than invent a number.
    /// </summary>
    public static class FirstTimeOfferHelper
    {
        public static bool IsFirstTimeOffer(SpecialOffer offer) =>
            offer.RequiresFirstTimeCustomer ||
            offer.Type == OfferType.FirstTime ||
            (offer.Name != null &&
                (offer.Name.Contains("first time", StringComparison.OrdinalIgnoreCase) ||
                 offer.Name.Contains("first-time", StringComparison.OrdinalIgnoreCase)));

        /// <summary>Picks the first-time offer out of an already-materialized list.
        /// (The name check can't be translated to SQL — query the offers first, then filter.)</summary>
        public static SpecialOffer? Find(IEnumerable<SpecialOffer> offers) =>
            offers.FirstOrDefault(IsFirstTimeOffer);

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
    }
}
