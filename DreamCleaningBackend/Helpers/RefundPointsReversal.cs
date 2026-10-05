using System.Security.Cryptography;
using System.Text;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// Bubble points taken back when an order is refunded or cancelled (owner's rules, 2026-10).
    /// Before this a refund never touched points: a fully refunded order kept everything it earned.
    ///
    /// NEW REFUNDS (<see cref="ApplyForRefundAsync"/>, called by OrderRefundService):
    ///   - a full refund reverses every point the order earned (its positive history rows -
    ///     OrderEarned, StreakBonus and any other bonus booked against the order);
    ///   - a partial refund reverses round(earned x refunded / charged);
    ///   - CUMULATIVE against the order's absolute refunded total, so a second partial refund takes
    ///     only its own share and re-running the Stripe sync takes nothing.
    ///
    /// PAST REFUNDS AND CANCELLATIONS (<see cref="BuildCorrectionPlanAsync"/> /
    /// <see cref="ApplyCorrectionPlanAsync"/>, the one-time admin correction): dry run first, then
    /// apply exactly that plan. Every corrected order gets a marker row (even a 0-point one when the
    /// customer had nothing left), so a second run finds nothing to do.
    ///
    /// Both: the balance never goes below zero (what is left is taken, the shortfall is logged and
    /// written on the row), and every reversal is a history row whose Description carries the full
    /// admin detail. The CUSTOMER never sees that detail - BubblePointsService.GetHistory shows these
    /// rows as a neutral "Balance adjustment" with no order and no reason.
    /// </summary>
    public static class RefundPointsReversal
    {
        /// <summary>A reversal written when a refund is issued or synced (from deploy on).</summary>
        public const string RefundReversalType = "RefundReversal";
        /// <summary>One-time correction of a refund recorded before deploy.</summary>
        public const string RefundCorrectionType = "RefundCorrection";
        /// <summary>One-time correction of a cancelled order that still held its points.</summary>
        public const string CancellationCorrectionType = "CancellationCorrection";

        public static readonly string[] ReversalTypes = { RefundReversalType, RefundCorrectionType, CancellationCorrectionType };

        /// <summary>What the customer sees for any reversal row.</summary>
        public const string CustomerFacingType = "BalanceAdjustment";

        public static bool IsReversal(string? type) => type != null && ReversalTypes.Contains(type);

        /// <summary>Points to have taken back in TOTAL for this much refunded. Pure, for the tests.</summary>
        public static int TargetReversal(int earned, decimal totalCharged, decimal refundedTotal)
        {
            if (earned <= 0 || refundedTotal <= 0m) return 0;
            if (totalCharged <= 0m || refundedTotal >= totalCharged) return earned;
            var share = (int)Math.Round(earned * refundedTotal / totalCharged, MidpointRounding.AwayFromZero);
            return Math.Clamp(share, 0, earned);
        }

        private sealed record OrderPoints(int Earned, int AlreadyReversed, bool HasAutomaticReversal, bool HasCorrection);

        private static async Task<Dictionary<int, OrderPoints>> LoadOrderPointsAsync(ApplicationDbContext context, IEnumerable<int> orderIds)
        {
            var ids = orderIds.Distinct().ToList();
            var rows = await context.BubblePointsHistories
                .Where(h => h.OrderId != null && ids.Contains(h.OrderId.Value))
                .Select(h => new { OrderId = h.OrderId!.Value, h.Type, h.Points })
                .ToListAsync();
            return rows.GroupBy(r => r.OrderId).ToDictionary(g => g.Key, g => new OrderPoints(
                g.Where(r => r.Points > 0).Sum(r => r.Points),
                -g.Where(r => IsReversal(r.Type)).Sum(r => r.Points),
                g.Any(r => r.Type == RefundReversalType),
                g.Any(r => r.Type == RefundCorrectionType || r.Type == CancellationCorrectionType)));
        }

        // ── New refunds ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Takes back what is still owed for <paramref name="order"/> after a refund. Adds to the
        /// context; the caller saves inside the same SaveChanges as the refund itself.
        /// <paramref name="appliedBy"/> names who or what triggered it, for the admin record.
        /// </summary>
        public static async Task ApplyForRefundAsync(ApplicationDbContext context, ILogger logger,
            Order order, decimal totalCharged, decimal refundedTotal, string appliedBy)
        {
            var points = (await LoadOrderPointsAsync(context, new[] { order.Id }))
                .GetValueOrDefault(order.Id) ?? new OrderPoints(0, 0, false, false);
            var owed = TargetReversal(points.Earned, totalCharged, refundedTotal) - points.AlreadyReversed;
            if (owed <= 0) return;

            var user = await context.Users.FirstOrDefaultAsync(u => u.Id == order.UserId);
            if (user == null) return;

            var taken = Math.Min(owed, Math.Max(0, user.BubblePoints));
            var kind = refundedTotal >= totalCharged ? "full refund" : "partial refund";
            if (taken < owed)
                logger.LogWarning(
                    "Refund of order {OrderId}: {Owed} Bubble points were due back from user {UserId} but the balance was {Balance}; took {Taken}, {Shortfall} had already been spent.",
                    order.Id, owed, user.Id, user.BubblePoints, taken, owed - taken);
            if (taken <= 0) return;

            user.BubblePoints -= taken;
            context.BubblePointsHistories.Add(new BubblePointsHistory
            {
                UserId = user.Id,
                OrderId = order.Id,
                Points = -taken,
                Type = RefundReversalType,
                Description = Describe(order.Id, kind, taken, owed, points.Earned,
                    $"${refundedTotal:0.00} of ${totalCharged:0.00} refunded", appliedBy),
                CreatedAt = DateTime.UtcNow
            });
            logger.LogInformation("Refund of order {OrderId}: took back {Taken} Bubble points from user {UserId}.", order.Id, taken, user.Id);
        }

        /// <summary>The admin-facing record. The customer never sees it (GetHistory masks it).</summary>
        private static string Describe(int orderId, string kind, int taken, int owed, int earned, string money, string appliedBy)
        {
            var shortfall = taken < owed ? $"; {owed - taken} more were due but the customer had already spent them" : "";
            var text = $"Refund/cancellation of order #{orderId} ({kind}, {money}): {taken} of {earned} earned points reversed{shortfall}. Applied by {appliedBy}.";
            return text.Length <= 500 ? text : text[..500];
        }

        // ── One-time correction of past refunds and cancellations ───────────────────────────

        public sealed class CorrectionRow
        {
            public int UserId { get; set; }
            public string Customer { get; set; } = "";
            public string? Email { get; set; }
            public int OrderId { get; set; }
            /// <summary>cancelled / full refund / partial refund</summary>
            public string Kind { get; set; } = "";
            public decimal OrderTotal { get; set; }
            public decimal RefundedAmount { get; set; }
            public int PointsEarned { get; set; }
            public int AlreadyReversed { get; set; }
            /// <summary>What the rule says should come back now.</summary>
            public int PointsToReverse { get; set; }
            /// <summary>What can actually be taken (the balance never goes below zero).</summary>
            public int PointsTaken { get; set; }
            public int BalanceBefore { get; set; }
            public int BalanceAfter { get; set; }
        }

        public sealed class CorrectionPlan
        {
            public List<CorrectionRow> Rows { get; set; } = new();
            public int Customers => Rows.Select(r => r.UserId).Distinct().Count();
            public int Orders => Rows.Count;
            public int TotalPointsToReverse => Rows.Sum(r => r.PointsToReverse);
            public int TotalPointsTaken => Rows.Sum(r => r.PointsTaken);
            /// <summary>Fingerprint of exactly this plan; apply refuses any other.</summary>
            public string PlanId { get; set; } = "";
        }

        /// <summary>
        /// Every cancelled or (fully/partly) refunded order that still holds points it should not.
        /// Skips orders already handled - by a correction, or by the automatic refund reversal.
        /// Read-only.
        /// </summary>
        public static async Task<CorrectionPlan> BuildCorrectionPlanAsync(ApplicationDbContext context)
        {
            var candidates = await context.Orders
                .Where(o => o.Status == OrderStatuses.Cancelled || o.Status == OrderStatuses.Refunded || o.TotalRefundedAmount > 0)
                .Where(o => context.BubblePointsHistories.Any(h => h.OrderId == o.Id && h.Points > 0))
                .Select(o => new { o.Id, o.UserId, o.Status, o.Total, o.TotalRefundedAmount })
                .ToListAsync();

            var points = await LoadOrderPointsAsync(context, candidates.Select(c => c.Id));
            var userIds = candidates.Select(c => c.UserId).Distinct().ToList();
            var users = await context.Users.Where(u => userIds.Contains(u.Id))
                .Select(u => new { u.Id, u.FirstName, u.LastName, u.Email, u.BubblePoints })
                .ToDictionaryAsync(u => u.Id);

            var rows = new List<CorrectionRow>();
            foreach (var userGroup in candidates.GroupBy(c => c.UserId).OrderBy(g => g.Key))
            {
                if (!users.TryGetValue(userGroup.Key, out var user)) continue;
                var balance = Math.Max(0, user.BubblePoints);
                foreach (var o in userGroup.OrderBy(c => c.Id))
                {
                    var p = points.GetValueOrDefault(o.Id);
                    if (p == null || p.HasCorrection || p.HasAutomaticReversal) continue;

                    var kind = OrderStatuses.IsCancelled(o.Status) ? "cancelled"
                        : OrderStatuses.IsRefunded(o.Status) ? "full refund" : "partial refund";
                    var target = kind == "partial refund" ? TargetReversal(p.Earned, o.Total, o.TotalRefundedAmount) : p.Earned;
                    var owed = target - p.AlreadyReversed;
                    if (owed <= 0) continue;

                    var taken = Math.Min(owed, balance);
                    rows.Add(new CorrectionRow
                    {
                        UserId = user.Id, Customer = $"{user.FirstName} {user.LastName}".Trim(), Email = user.Email,
                        OrderId = o.Id, Kind = kind, OrderTotal = o.Total, RefundedAmount = o.TotalRefundedAmount,
                        PointsEarned = p.Earned, AlreadyReversed = p.AlreadyReversed,
                        PointsToReverse = owed, PointsTaken = taken,
                        BalanceBefore = balance, BalanceAfter = balance - taken
                    });
                    balance -= taken;
                }
            }

            var plan = new CorrectionPlan { Rows = rows };
            plan.PlanId = Fingerprint(rows);
            return plan;
        }

        private static string Fingerprint(IEnumerable<CorrectionRow> rows)
        {
            var text = string.Join("\n", rows.Select(r =>
                $"{r.UserId}|{r.OrderId}|{r.Kind}|{r.PointsEarned}|{r.AlreadyReversed}|{r.PointsToReverse}|{r.PointsTaken}|{r.BalanceBefore}"));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
        }

        /// <summary>
        /// Applies the plan whose <see cref="CorrectionPlan.PlanId"/> the admin reviewed. Rebuilds it
        /// first and refuses if anything changed since the dry run (returns null). Saves.
        /// </summary>
        public static async Task<CorrectionPlan?> ApplyCorrectionPlanAsync(ApplicationDbContext context, ILogger logger,
            string reviewedPlanId, string appliedBy)
        {
            var plan = await BuildCorrectionPlanAsync(context);
            if (!string.Equals(plan.PlanId, reviewedPlanId, StringComparison.OrdinalIgnoreCase)) return null;
            if (plan.Rows.Count == 0) return plan;

            var userIds = plan.Rows.Select(r => r.UserId).Distinct().ToList();
            var users = await context.Users.Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id);
            var now = DateTime.UtcNow;

            foreach (var row in plan.Rows)
            {
                var user = users[row.UserId];
                user.BubblePoints = Math.Max(0, user.BubblePoints - row.PointsTaken);
                // Written even when nothing could be taken: the row is what marks the order as
                // corrected, so a second run never revisits it. 0-point rows are hidden from the customer.
                context.BubblePointsHistories.Add(new BubblePointsHistory
                {
                    UserId = row.UserId,
                    OrderId = row.OrderId,
                    Points = -row.PointsTaken,
                    Type = row.Kind == "cancelled" ? CancellationCorrectionType : RefundCorrectionType,
                    Description = Describe(row.OrderId, row.Kind, row.PointsTaken, row.PointsToReverse, row.PointsEarned,
                        row.Kind == "cancelled" ? $"order total ${row.OrderTotal:0.00}" : $"${row.RefundedAmount:0.00} of ${row.OrderTotal:0.00} refunded",
                        appliedBy),
                    CreatedAt = now
                });
                if (row.PointsTaken < row.PointsToReverse)
                    logger.LogWarning("Points correction for order {OrderId}: {Owed} due from user {UserId}, {Taken} taken (balance exhausted).",
                        row.OrderId, row.PointsToReverse, row.UserId, row.PointsTaken);
            }

            await context.SaveChangesAsync();
            logger.LogInformation("Points correction {PlanId} applied by {AppliedBy}: {Orders} orders, {Customers} customers, {Points} points taken back.",
                plan.PlanId, appliedBy, plan.Orders, plan.Customers, plan.TotalPointsTaken);
            return plan;
        }
    }
}
