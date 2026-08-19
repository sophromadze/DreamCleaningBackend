using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// THE single writer for <see cref="Order.PropertyType"/> and <see cref="Order.LevelsQuantity"/>,
    /// and the one place that defines what a property type is.
    ///
    /// Both columns are DISPLAY-ONLY denormalizations. The OrderServices row for the "levels"
    /// service stays the pricing source of truth: the stair charge is always computed by the
    /// shared calculator from that row, never from Order.LevelsQuantity. The columns exist
    /// because the cleaner assignment email, the admin orders list, the Excel export and the
    /// CRM customer summary all read flat Order columns instead of joining OrderServices -
    /// exactly the reason Order.BedroomsQuantity already exists alongside the bedrooms row.
    ///
    /// Every write path MUST go through <see cref="Apply"/>: booking create, confirm-payment,
    /// user order edit, admin order edit, pending-edit approval and reorder. A call site that
    /// assigned the column by hand could drift from the OrderService row, and then the cleaner
    /// email would tell the crew a different number of levels than the customer was charged for.
    ///
    /// The guarantee is structural, not conventional. LevelsQuantity is DERIVED from the same
    /// QuoteInput the calculator priced, and OrderPricingCalculator.AddOrderLinesFromQuote
    /// copies that input quantity onto the OrderService row verbatim. Column and row therefore
    /// cannot disagree by construction, not merely by discipline.
    /// </summary>
    public static class PropertyDetailsHelper
    {
        /// <summary>Apartment or condo. No levels are captured or charged.</summary>
        public const string Apartment = "Apartment";

        /// <summary>House or townhouse. Levels are captured and priced.</summary>
        public const string House = "House";

        /// <summary>
        /// ServiceKey of the levels service. Referenced everywhere the levels row has to be
        /// singled out (pricing clamps, the display-only column, and the filters that keep it
        /// out of the generic service loops on the booking and order-edit pages).
        /// </summary>
        public const string LevelsServiceKey = "levels";

        /// <summary>
        /// The first level is included at no charge, so a single-level house costs exactly the
        /// same as the equivalent apartment. This constant is the SEEDED default only: the real
        /// allowance lives in the levels service's self-referencing ServiceThreshold row and is
        /// admin-editable. Nothing in the pricing path reads this value - it exists so the
        /// migration and its tests state the intent in one place.
        /// </summary>
        public const int SeededIncludedLevels = 1;

        /// <summary>Lowest level count. There is no such thing as a house with zero levels.</summary>
        public const int MinLevels = 1;

        /// <summary>Highest level count the chips offer. Mirrors LEVEL_OPTIONS on the frontend.</summary>
        public const int MaxInformationalLevels = 4;

        /// <summary>
        /// Coerces a client-supplied property type to one of the two known values, or null.
        ///
        /// Null is a first-class result, not a failure: every order created before this feature
        /// has no property type, and those orders must keep rendering without showing an empty
        /// field. Unknown strings collapse to null rather than being stored, so a typo or a
        /// hand-rolled API call can never put a third value in the column.
        /// </summary>
        public static string? NormalizePropertyType(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            var trimmed = raw.Trim();
            if (string.Equals(trimmed, Apartment, StringComparison.OrdinalIgnoreCase)) return Apartment;
            if (string.Equals(trimmed, House, StringComparison.OrdinalIgnoreCase)) return House;
            return null;
        }

        /// <summary>True only for a normalized House. Null and Apartment are both false.</summary>
        public static bool IsHouse(string? propertyType)
            => NormalizePropertyType(propertyType) == House;

        /// <summary>
        /// The level count to display for an order, read from the priced input.
        ///
        /// Returns null for anything that is not a house, so an apartment order and a legacy
        /// order both store null and both render as "no levels field" rather than as "1 level".
        /// A house with no levels line (custom / Pre-Arranged pricing, where levels are hidden
        /// and unpriced) also returns null for the same reason.
        /// </summary>
        public static int? ResolveLevelsQuantity(
            string? propertyType, OrderPricingCalculator.QuoteInput? input, int? requestedLevels = null)
        {
            if (!IsHouse(propertyType)) return null;

            // A PRICED levels line always wins. It is the row the customer was charged from, so
            // reading the column off it is what guarantees the two can never disagree.
            var levelsLine = input?.Services
                .FirstOrDefault(s => s.ServiceKey == LevelsServiceKey);
            if (levelsLine != null) return levelsLine.Quantity;

            // INFORMATIONAL case: this service type has no levels service, so there is no line to
            // read and the column is the ONLY record of the count. Same precedent as
            // Order.BedroomsQuantity / BathroomsQuantity, which are captured this way for
            // cleaner+hours and custom modes and explicitly affect neither price nor duration.
            //
            // Clamped here because this value never passes through the calculator, so it gets none
            // of the range clamping OrderPricingInputBuilder applies to a priced levels line.
            return ClampInformationalLevels(requestedLevels);
        }

        /// <summary>
        /// Clamps an informational level count into the offered range, or null when absent.
        ///
        /// The range is the hardcoded chip range rather than a service row's MinValue/MaxValue,
        /// because in the informational case there IS no service row to read them from. On a
        /// priced service type the configured range still governs, via
        /// OrderPricingInputBuilder.ClampLevelsToConfiguredRange.
        /// </summary>
        public static int? ClampInformationalLevels(int? requestedLevels)
        {
            if (requestedLevels == null) return null;
            return Math.Clamp(requestedLevels.Value, MinLevels, MaxInformationalLevels);
        }



        /// <summary>
        /// Writes both display columns from the QuoteInput that was priced for this order.
        ///
        /// For every path that RE-PRICES: booking create, confirm-payment, reorder and the
        /// customer order edit. Pass the exact instance handed to the calculator, AFTER the
        /// server-side clamps have run - rebuilding a second input to read from would reintroduce
        /// the drift this helper exists to prevent.
        /// </summary>
        /// <param name="requestedLevels">
        /// The count the client chose, used ONLY when this service type has no priced levels
        /// service. Ignored whenever a priced line exists, so it can never override the row the
        /// customer was actually charged from.
        /// </param>
        public static void Apply(
            Order order, string? requestedPropertyType, OrderPricingCalculator.QuoteInput? input,
            int? requestedLevels = null)
        {
            var normalized = NormalizePropertyType(requestedPropertyType);
            Set(order, normalized, ResolveLevelsQuantity(normalized, input, requestedLevels));
        }

        /// <summary>
        /// Writes both display columns by reading the order's OWN levels line.
        ///
        /// For paths that mutate OrderServices in place instead of re-pricing, which is what the
        /// admin editor and the pending-edit approval do (SuperAdminFullUpdateOrder assigns
        /// os.Quantity row by row and never builds a QuoteInput). Reading back from the rows it
        /// just wrote gives the same guarantee the QuoteInput overload gives: the column is a
        /// function of the row, so the two cannot drift.
        ///
        /// Requires the OrderServices.Service navigation to be loaded. When it is not, the levels
        /// line cannot be identified and the count is left ALONE rather than silently nulled - a
        /// partially loaded order must never be able to erase a level count as a side effect.
        /// </summary>
        /// <param name="requestedPropertyType">
        /// Null means NO CHANGE here, matching SuperAdminUpdateOrderDto's patch semantics: an
        /// admin editing only the service date must not strip the property type off the order.
        /// </param>
        /// <param name="requestedLevels">
        /// Same role as on <see cref="Apply"/>: the fallback for an order whose service type has
        /// no priced levels row. Null leaves an existing informational count alone.
        /// </param>
        public static void ApplyFromOrderLines(
            Order order, string? requestedPropertyType, int? requestedLevels = null)
        {
            if (requestedPropertyType == null) return;

            var normalized = NormalizePropertyType(requestedPropertyType);

            if (normalized != House)
            {
                Set(order, normalized, null);
                return;
            }

            var levelsLine = order.OrderServices?
                .FirstOrDefault(os => os.Service != null && os.Service.ServiceKey == LevelsServiceKey);

            if (levelsLine == null)
            {
                order.PropertyType = normalized;

                // No priced row. Either this is the INFORMATIONAL case (a service type with no
                // levels service, where the column is the only record), or the OrderServices
                // navigation was not loaded. An explicit requested count means the caller knows
                // the informational value; null means "do not touch it", so a partially loaded
                // order can never erase a count as a side effect.
                var informational = ClampInformationalLevels(requestedLevels);
                if (informational != null) order.LevelsQuantity = informational;
                return;
            }

            Set(order, normalized, levelsLine.Quantity);
        }

        private static void Set(Order order, string? normalizedPropertyType, int? levelsQuantity)
        {
            order.PropertyType = normalizedPropertyType;

            // Switching a house back to an apartment must clear the level count, or the cleaner
            // email keeps announcing stairs for a flat.
            order.LevelsQuantity = normalizedPropertyType == House ? levelsQuantity : null;
        }
    }
}
