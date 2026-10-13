using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// "Deep" or "Regular" for a residential order in the admin Orders list (2026-10).
    ///
    /// The list used to have no way to tell: it carried no extras, so the Orders tab fetched EVERY
    /// residential order's full details in the background (one request per order, 261 on a dev
    /// /admin load and growing with every booking) just to draw this one label - and showed
    /// "Regular" until each order's batch arrived. The list now carries <c>IsDeepCleaning</c>,
    /// decided here from one grouped query for the whole list.
    ///
    /// The rule is the frontend's <c>resolveIsDeepResidential</c> (orders.component.ts), unchanged:
    /// <list type="bullet">
    /// <item>an extra counts when it is Deep and not Super Deep - its flags decide, then its
    ///   ExtraServiceKey; only an UNKEYED, un-flagged extra is matched by name
    ///   (<see cref="ExtraServiceKeys.IsDeepOrSuperDeep"/> / <see cref="ExtraServiceKeys.IsSuperDeep"/>);</item>
    /// <item>a service line never counts once its service is keyed; an unkeyed (legacy) line
    ///   counts when its name, normalised, contains "deep-cleaning" and not "super-deep".</item>
    /// </list>
    /// </summary>
    public static class ResidentialCleaningVariant
    {
        /// <summary>One candidate row: an order's extra, or one of its service lines.</summary>
        public sealed class Line
        {
            public int OrderId { get; set; }
            public bool IsExtra { get; set; }
            public string? Key { get; set; }
            public string? Name { get; set; }
            public bool IsDeepCleaning { get; set; }
            public bool IsSuperDeepCleaning { get; set; }
        }

        /// <summary>Does this extra make the order Deep?</summary>
        public static bool ExtraMakesDeep(string? extraServiceKey, string? name, bool isDeepCleaning, bool isSuperDeepCleaning)
        {
            var extra = new ExtraService
            {
                ExtraServiceKey = extraServiceKey,
                Name = name ?? "",
                IsDeepCleaning = isDeepCleaning,
                IsSuperDeepCleaning = isSuperDeepCleaning
            };
            return ExtraServiceKeys.IsDeepOrSuperDeep(extra) && !ExtraServiceKeys.IsSuperDeep(extra);
        }

        /// <summary>Does this service line make the order Deep? Keyed lines never do.</summary>
        public static bool ServiceLineMakesDeep(string? serviceKey, string? name)
        {
            if (!string.IsNullOrWhiteSpace(serviceKey)) return false;
            var normalized = System.Text.RegularExpressions.Regex.Replace((name ?? "").Trim().ToLowerInvariant(), @"[_\s]+", "-");
            return normalized.Contains("deep-cleaning") && !normalized.Contains("super-deep");
        }

        public static bool LineMakesDeep(Line line) => line.IsExtra
            ? ExtraMakesDeep(line.Key, line.Name, line.IsDeepCleaning, line.IsSuperDeepCleaning)
            : ServiceLineMakesDeep(line.Key, line.Name);

        /// <summary>
        /// The ids of every order that is Deep, in ONE database round-trip (a UNION of the two
        /// candidate sets) - never a query per order. The SQL filter is a deliberate superset (any
        /// flagged, deep-keyed or "deep"-named row); the exact rule is applied above, in memory.
        /// </summary>
        public static async Task<HashSet<int>> LoadDeepOrderIdsAsync(ApplicationDbContext context)
        {
            var extras = context.OrderExtraServices
                .Where(oes => oes.ExtraService.IsDeepCleaning
                    || oes.ExtraService.IsSuperDeepCleaning
                    || oes.ExtraService.ExtraServiceKey == ExtraServiceKeys.DeepCleaning
                    || oes.ExtraService.Name.ToLower().Contains("deep"))
                .Select(oes => new Line
                {
                    OrderId = oes.OrderId,
                    IsExtra = true,
                    Key = oes.ExtraService.ExtraServiceKey,
                    Name = oes.ExtraService.Name,
                    IsDeepCleaning = oes.ExtraService.IsDeepCleaning,
                    IsSuperDeepCleaning = oes.ExtraService.IsSuperDeepCleaning
                });
            var serviceLines = context.OrderServices
                .Where(os => (os.Service.ServiceKey == null || os.Service.ServiceKey.Trim() == "")
                    && os.Service.Name.ToLower().Contains("deep"))
                .Select(os => new Line
                {
                    OrderId = os.OrderId,
                    IsExtra = false,
                    Key = os.Service.ServiceKey,
                    Name = os.Service.Name,
                    IsDeepCleaning = false,
                    IsSuperDeepCleaning = false
                });

            var candidates = await extras.Concat(serviceLines).ToListAsync();
            return candidates.Where(LineMakesDeep).Select(l => l.OrderId).ToHashSet();
        }
    }
}
