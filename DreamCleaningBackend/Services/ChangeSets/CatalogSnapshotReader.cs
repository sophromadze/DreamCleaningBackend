using DreamCleaningBackend.Data;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services.ChangeSets
{
    /// <summary>
    /// The whole Services configuration in one read (2026-10) — every service type with its
    /// services (and their included amounts and rate tiers), poll questions and configured extras,
    /// the extras catalogue, and the subscription plans — with the ids the write tool takes, so
    /// Claude can calculate and plan a change from one call.
    ///
    /// Every column is included as stored (snake_case), so nothing here needs updating when a
    /// column is added. Each row also says how much depends on it, because a booking edit re-prices
    /// at TODAY's catalogue: <c>orders</c> (every order ever), <c>future_orders</c> (service date
    /// today or later in New York, status not Done / Cancelled / Refunded) and
    /// <c>active_recurring_plans</c> (active, not stopped or ended, template order uses it). The
    /// re-pricing itself is unchanged; this only shows the reach of a price change.
    /// </summary>
    public sealed class CatalogSnapshotReader
    {
        private static readonly string[] ClosedStatuses = { "Done", "Cancelled", "Refunded" };

        public const string UsageDefinitions =
            "orders = every order that uses the row; future_orders = orders with a service date today or later (New York) " +
            "whose status is not Done, Cancelled or Refunded (unpaid Pending bookings included); active_recurring_plans = " +
            "recurring plans that are active, not stopped and not ended, whose template order uses the row. Editing an existing " +
            "order re-prices it at the current catalogue, and recurring plans create their next visits at current prices.";

        private readonly ApplicationDbContext _db;

        public CatalogSnapshotReader(ApplicationDbContext db) => _db = db;

        public sealed record Usage(int Orders, int FutureOrders, int ActiveRecurringPlans);

        public async Task<object> ReadAsync(bool includeInactive, CancellationToken ct = default)
        {
            var today = NyTimeHelper.NowNy.Date;

            var types = await _db.ServiceTypes.AsNoTracking().OrderBy(t => t.DisplayOrder).ThenBy(t => t.Id).ToListAsync(ct);
            var services = await _db.Services.AsNoTracking().OrderBy(s => s.DisplayOrder).ThenBy(s => s.Id).ToListAsync(ct);
            var thresholds = await _db.ServiceThresholds.AsNoTracking().OrderBy(t => t.SourceQuantity).ToListAsync(ct);
            var tiers = await _db.ServiceRateTiers.AsNoTracking().OrderBy(t => t.DisplayOrder).ThenBy(t => t.FromQuantity).ToListAsync(ct);
            var extras = await _db.ExtraServices.AsNoTracking().OrderBy(e => e.DisplayOrder).ThenBy(e => e.Id).ToListAsync(ct);
            var polls = await _db.PollQuestions.AsNoTracking().OrderBy(p => p.DisplayOrder).ThenBy(p => p.Id).ToListAsync(ct);
            var plans = await _db.Subscriptions.AsNoTracking().OrderBy(s => s.DisplayOrder).ThenBy(s => s.Id).ToListAsync(ct);

            if (!includeInactive)
            {
                types = types.Where(t => t.IsActive).ToList();
                services = services.Where(s => s.IsActive).ToList();
                extras = extras.Where(e => e.IsActive).ToList();
                polls = polls.Where(p => p.IsActive).ToList();
                plans = plans.Where(p => p.IsActive).ToList();
            }

            // ── Usage ───────────────────────────────────────────────────────────────────────
            var orders = _db.Orders.AsNoTracking();
            var future = orders.Where(o => o.ServiceDate >= today && !ClosedStatuses.Contains(o.Status));
            var activeTemplates = _db.RecurringOrderSeries.AsNoTracking()
                .Where(s => s.IsActive && s.StoppedAt == null && (s.EndDate == null || s.EndDate >= today))
                .Select(s => s.TemplateOrderId);

            var typeAll = await orders.GroupBy(o => o.ServiceTypeId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
            var typeFuture = await future.GroupBy(o => o.ServiceTypeId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
            var typePlans = await orders.Where(o => activeTemplates.Contains(o.Id)).GroupBy(o => o.ServiceTypeId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);

            var lineAll = await _db.OrderServices.AsNoTracking().GroupBy(l => l.ServiceId).Select(g => new { g.Key, N = g.Select(l => l.OrderId).Distinct().Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
            var lineFuture = await _db.OrderServices.AsNoTracking().Where(l => future.Select(o => o.Id).Contains(l.OrderId))
                .GroupBy(l => l.ServiceId).Select(g => new { g.Key, N = g.Select(l => l.OrderId).Distinct().Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
            var linePlans = await _db.OrderServices.AsNoTracking().Where(l => activeTemplates.Contains(l.OrderId))
                .GroupBy(l => l.ServiceId).Select(g => new { g.Key, N = g.Select(l => l.OrderId).Distinct().Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);

            var extraAll = await _db.OrderExtraServices.AsNoTracking().GroupBy(l => l.ExtraServiceId).Select(g => new { g.Key, N = g.Select(l => l.OrderId).Distinct().Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
            var extraFuture = await _db.OrderExtraServices.AsNoTracking().Where(l => future.Select(o => o.Id).Contains(l.OrderId))
                .GroupBy(l => l.ExtraServiceId).Select(g => new { g.Key, N = g.Select(l => l.OrderId).Distinct().Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
            var extraPlans = await _db.OrderExtraServices.AsNoTracking().Where(l => activeTemplates.Contains(l.OrderId))
                .GroupBy(l => l.ExtraServiceId).Select(g => new { g.Key, N = g.Select(l => l.OrderId).Distinct().Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);

            var planAll = await orders.Where(o => o.SubscriptionId != null).GroupBy(o => o.SubscriptionId!.Value).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
            var planFuture = await future.Where(o => o.SubscriptionId != null).GroupBy(o => o.SubscriptionId!.Value).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
            var planPlans = await orders.Where(o => o.SubscriptionId != null && activeTemplates.Contains(o.Id)).GroupBy(o => o.SubscriptionId!.Value).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);

            static Usage U(int id, Dictionary<int, int> all, Dictionary<int, int> fut, Dictionary<int, int> plan) =>
                new(all.GetValueOrDefault(id), fut.GetValueOrDefault(id), plan.GetValueOrDefault(id));

            // ── Shape ───────────────────────────────────────────────────────────────────────
            object Row(object entity, Usage? usage = null, params (string Key, object? Value)[] extra)
            {
                var row = new Dictionary<string, object?>();
                foreach (var (name, value) in AuditCaptureScope.Snapshot(entity))
                {
                    if (name is "CreatedAt" or "UpdatedAt") continue;
                    row[CatalogChangeRunner.Snake(name)] = value;
                }
                if (usage != null)
                    row["usage"] = new { orders = usage.Orders, future_orders = usage.FutureOrders, active_recurring_plans = usage.ActiveRecurringPlans };
                foreach (var (key, value) in extra) row[key] = value;
                return row;
            }

            var result = new
            {
                today = today.ToString("yyyy-MM-dd"),
                usage_definitions = UsageDefinitions,
                inactive_included = includeInactive,
                service_types = types.Select(t => Row(t, U(t.Id, typeAll, typeFuture, typePlans),
                    ("services", services.Where(s => s.ServiceTypeId == t.Id).Select(s => Row(s, U(s.Id, lineAll, lineFuture, linePlans),
                        ("included_amounts", thresholds.Where(x => x.ServiceId == s.Id).Select(x => Row(x)).ToList()),
                        ("rate_tiers", tiers.Where(x => x.ServiceId == s.Id).Select(x => Row(x)).ToList()))).ToList()),
                    // The type's real configuration, exactly as the Services tab lists it.
                    ("extra_service_ids", CatalogDtoMapper.ResolveConfiguredExtraServices(t, extras).Select(e => e.Id).ToList()),
                    ("poll_questions", polls.Where(p => p.ServiceTypeId == t.Id).Select(p => Row(p)).ToList()))).ToList(),
                extra_services = extras.Select(e => Row(e, U(e.Id, extraAll, extraFuture, extraPlans))).ToList(),
                subscriptions = plans.Select(p => Row(p, U(p.Id, planAll, planFuture, planPlans))).ToList()
            };
            return result;
        }
    }
}
