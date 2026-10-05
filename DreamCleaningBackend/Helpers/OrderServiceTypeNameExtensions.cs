using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// SINGLE SOURCE OF TRUTH for the customer/cleaner-facing service-type name of an order.
    /// For the custom ("Pre-Arranged") service type, the admin picks a per-order label at booking
    /// time (Order.CustomServiceDisplayName, e.g. "Deep"); everywhere a human sees the service type
    /// — notifications, emails, SMS, order details — it must read "<label> Cleaning" instead of the
    /// generic custom service-type name. For every other service type, the normal ServiceType.Name
    /// is returned unchanged.
    ///
    /// Requires Order.ServiceType to be loaded (Include). Only valid for in-memory Order instances —
    /// EF IQueryable projections can't call this, so they inline the equivalent ternary.
    /// </summary>
    public static class OrderServiceTypeNameExtensions
    {
        public static string GetDisplayServiceTypeName(this Order order, string fallback = "")
        {
            if (order?.ServiceType == null)
                return fallback;

            if (order.ServiceType.IsCustom && !string.IsNullOrWhiteSpace(order.CustomServiceDisplayName))
                return $"{order.CustomServiceDisplayName.Trim()} Cleaning";

            return string.IsNullOrWhiteSpace(order.ServiceType.Name) ? fallback : order.ServiceType.Name;
        }

        /// <summary>
        /// The order's ServiceType.ServiceKey for code that recognises a type - null for a custom
        /// ("Pre-Arranged") order, whose per-order label is what identifies it, and for an unkeyed
        /// type. Pair it with <see cref="GetDisplayServiceTypeName"/>, which stays the fallback.
        /// </summary>
        public static string? GetRecognisableServiceTypeKey(this Order order) =>
            order?.ServiceType is { IsCustom: false } type && !string.IsNullOrWhiteSpace(type.ServiceKey)
                ? type.ServiceKey.Trim()
                : null;
    }
}
