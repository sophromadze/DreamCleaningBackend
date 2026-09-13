using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services
{
    // See ILoyaltyDiscountService for the lifecycle overview.
    public class LoyaltyDiscountService : ILoyaltyDiscountService
    {
        private readonly ApplicationDbContext _context;
        private readonly IAuditService _auditService;
        private readonly ILogger<LoyaltyDiscountService> _logger;

        // Audit action names — keep in sync with the spec and the admin UI's getActionClass.
        private const string ActionAutoActivated = "LoyaltyAutoActivated";
        private const string ActionAutoUpgraded = "LoyaltyAutoUpgraded";
        private const string ActionManualSet = "LoyaltyManualSet";
        private const string ActionManualCleared = "LoyaltyManualCleared";
        private const string ActionUsed = "LoyaltyUsed";
        private const string ActionReversed = "LoyaltyReversed";

        public LoyaltyDiscountService(
            ApplicationDbContext context,
            IAuditService auditService,
            ILogger<LoyaltyDiscountService> logger)
        {
            _context = context;
            _auditService = auditService;
            _logger = logger;
        }

        public async Task<LoyaltyDiscountDto> GetForUserAsync(int userId)
        {
            var user = await _context.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == userId)
                ?? throw new InvalidOperationException($"User #{userId} not found");

            return Project(user);
        }

        public async Task<LoyaltyDiscountDto> SetManualAsync(
            int userId, decimal percentage, int adminUserId, bool isLifetime = false)
        {
            if (percentage < 0 || percentage > 100)
                throw new ArgumentOutOfRangeException(nameof(percentage), "Percentage must be between 0 and 100");

            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId)
                ?? throw new InvalidOperationException($"User #{userId} not found");

            // Setting to exactly 0 via the manual endpoint is semantically a clear — let the
            // user re-enter the auto-managed flow rather than being permanently frozen at 0.
            // That includes clearing LIFETIME: "0% forever" is not a discount, it is its absence,
            // and leaving the flag set would keep the inactivity automation suppressed for a
            // customer who now has nothing.
            if (percentage == 0)
            {
                return await ClearAsync(userId, adminUserId);
            }

            var oldPct = user.LoyaltyDiscountPercentage;
            var oldOverride = user.LoyaltyDiscountIsManualOverride;
            var oldActivatedAt = user.LoyaltyDiscountActivatedAt;
            var oldLastUsedAt = user.LoyaltyDiscountLastUsedAt;

            user.LoyaltyDiscountPercentage = percentage;
            user.LoyaltyDiscountIsManualOverride = true;
            // Set on EVERY manual write, in both directions: an admin editing a lifetime discount
            // down to a one-time one must actually demote it, or the customer would keep a
            // standing entitlement the admin believes they removed.
            user.LoyaltyDiscountIsLifetime = isLifetime;
            // Stamp ActivatedAt when going from 0 → manual value so the admin UI can show
            // a meaningful "set on" date. Don't overwrite an existing date — if the admin is
            // editing an already-active discount we preserve the original activation moment.
            if (oldPct == 0 && user.LoyaltyDiscountActivatedAt == null)
                user.LoyaltyDiscountActivatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            await _auditService.LogLoyaltyDiscountChangeAsync(
                userId, ActionManualSet,
                oldPct, oldOverride, oldActivatedAt, oldLastUsedAt,
                user.LoyaltyDiscountPercentage, user.LoyaltyDiscountIsManualOverride,
                user.LoyaltyDiscountActivatedAt, user.LoyaltyDiscountLastUsedAt,
                adminUserId);

            _logger.LogInformation("Admin {AdminId} manually set loyalty discount for user {UserId} to {Percentage}%", adminUserId, userId, percentage);

            return Project(user);
        }

        public async Task<LoyaltyDiscountDto> ClearAsync(int userId, int adminUserId)
        {
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId)
                ?? throw new InvalidOperationException($"User #{userId} not found");

            var oldPct = user.LoyaltyDiscountPercentage;
            var oldOverride = user.LoyaltyDiscountIsManualOverride;
            var oldActivatedAt = user.LoyaltyDiscountActivatedAt;
            var oldLastUsedAt = user.LoyaltyDiscountLastUsedAt;

            // Belt-and-suspenders: only emit audit + save when something actually changes.
            if (oldPct == 0 && !oldOverride && oldActivatedAt == null && !user.LoyaltyDiscountIsLifetime)
            {
                return Project(user);
            }

            user.LoyaltyDiscountPercentage = 0;
            user.LoyaltyDiscountIsManualOverride = false;
            // Clearing hands the customer back to the ordinary inactivity automation — which is
            // exactly what the lifetime flag was suppressing. Leaving it set would freeze them out
            // of the 60/90-day discounts forever with nothing to show for it.
            user.LoyaltyDiscountIsLifetime = false;
            user.LoyaltyDiscountActivatedAt = null;
            // Do NOT touch LastUsedAt — clearing should not re-open a cooldown the user already
            // passed through. The next natural inactivity cycle will re-evaluate.

            await _context.SaveChangesAsync();

            await _auditService.LogLoyaltyDiscountChangeAsync(
                userId, ActionManualCleared,
                oldPct, oldOverride, oldActivatedAt, oldLastUsedAt,
                user.LoyaltyDiscountPercentage, user.LoyaltyDiscountIsManualOverride,
                user.LoyaltyDiscountActivatedAt, user.LoyaltyDiscountLastUsedAt,
                adminUserId);

            _logger.LogInformation("Admin {AdminId} cleared loyalty discount for user {UserId}", adminUserId, userId);

            return Project(user);
        }

        public async Task<(decimal amount, decimal percentage)> CalculateForOrderAsync(int userId, decimal subTotal)
        {
            if (subTotal <= 0) return (0m, 0m);

            var pct = await _context.Users
                .AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => (decimal?)u.LoyaltyDiscountPercentage)
                .FirstOrDefaultAsync() ?? 0m;

            if (pct <= 0) return (0m, 0m);

            var amount = Math.Round(subTotal * pct / 100m, 2, MidpointRounding.AwayFromZero);
            return (amount, pct);
        }

        public async Task ApplyToOrderAsync(int orderId)
        {
            var order = await _context.Orders.FirstOrDefaultAsync(o => o.Id == orderId)
                ?? throw new InvalidOperationException($"Order #{orderId} not found");

            if (!DreamCleaningBackend.Helpers.ResidentialLoyaltyPolicy.AppliesTo(order)) return;
            // A generated occurrence carries a standing agreement, never a consumable account award.
            if (order.IsGeneratedByRecurringSeries) return;
            if (await _context.CommercialInvoiceOrders.AnyAsync(l => l.OrderId == orderId)) return;

            // No-op when the order didn't actually carry a loyalty discount. ApplyToOrderAsync
            // is called unconditionally on the booking-confirmation path so the caller doesn't
            // have to branch.
            if (order.LoyaltyDiscountAmount <= 0 || order.LoyaltyDiscountPercentage <= 0)
                return;

            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == order.UserId);
            if (user == null) return;

            var oldPct = user.LoyaltyDiscountPercentage;
            var oldOverride = user.LoyaltyDiscountIsManualOverride;
            var oldActivatedAt = user.LoyaltyDiscountActivatedAt;
            var oldLastUsedAt = user.LoyaltyDiscountLastUsedAt;

            // LIFETIME IS NOT CONSUMED. The whole meaning of the mode is that it survives being
            // used: the percentage, the manual-override flag and the activation date all stay, so
            // the next order — including every occurrence a recurring series generates — gets it
            // again. LastUsedAt is still stamped, because "when did this customer last take their
            // discount" is a real question, but it is not a cooldown here: the automation is
            // suppressed for lifetime customers anyway (see LoyaltyReengagementService), so there
            // is nothing for a cooldown to hold back.
            //
            // The reminder logs are likewise left alone. Deleting them exists to let the win-back
            // cycle re-trigger from scratch, and that cycle does not run for this customer.
            if (user.LoyaltyDiscountIsLifetime)
            {
                user.LoyaltyDiscountLastUsedAt = DateTime.UtcNow;

                await _context.SaveChangesAsync();

                await _auditService.LogLoyaltyDiscountChangeAsync(
                    user.Id, ActionUsed,
                    oldPct, oldOverride, oldActivatedAt, oldLastUsedAt,
                    user.LoyaltyDiscountPercentage, user.LoyaltyDiscountIsManualOverride,
                    user.LoyaltyDiscountActivatedAt, user.LoyaltyDiscountLastUsedAt,
                    adminUserId: null);

                _logger.LogInformation(
                    "Lifetime loyalty discount {Percentage}% applied by user {UserId} on order {OrderId} "
                    + "(${Amount}); not consumed.",
                    order.LoyaltyDiscountPercentage, user.Id, orderId, order.LoyaltyDiscountAmount);

                return;
            }

            // Consume: zero the percentage, clear manual override (a new cycle re-enters auto
            // management), stamp LastUsedAt, and delete the reminder logs for this user so the
            // next cycle can re-trigger from scratch once cooldown + inactivity windows pass.
            user.LoyaltyDiscountPercentage = 0;
            user.LoyaltyDiscountIsManualOverride = false;
            user.LoyaltyDiscountActivatedAt = null;
            user.LoyaltyDiscountLastUsedAt = DateTime.UtcNow;

            var staleLogs = await _context.NotificationLogs
                .Where(nl => nl.CustomerId == user.Id &&
                    (nl.NotificationType == NotificationTypes.LoyaltyReminder30 ||
                     nl.NotificationType == NotificationTypes.LoyaltyReminder60 ||
                     nl.NotificationType == NotificationTypes.LoyaltyReminder90))
                .ToListAsync();
            if (staleLogs.Count > 0)
                _context.NotificationLogs.RemoveRange(staleLogs);

            await _context.SaveChangesAsync();

            await _auditService.LogLoyaltyDiscountChangeAsync(
                user.Id, ActionUsed,
                oldPct, oldOverride, oldActivatedAt, oldLastUsedAt,
                user.LoyaltyDiscountPercentage, user.LoyaltyDiscountIsManualOverride,
                user.LoyaltyDiscountActivatedAt, user.LoyaltyDiscountLastUsedAt,
                adminUserId: null);

            _logger.LogInformation("Loyalty discount {Percentage}% consumed by user {UserId} on order {OrderId} (${Amount})",
                order.LoyaltyDiscountPercentage, user.Id, orderId, order.LoyaltyDiscountAmount);
        }

        public async Task ReverseFromOrderAsync(int orderId)
        {
            var order = await _context.Orders.FirstOrDefaultAsync(o => o.Id == orderId)
                ?? throw new InvalidOperationException($"Order #{orderId} not found");

            if (order.LoyaltyDiscountAmount <= 0 || order.LoyaltyDiscountPercentage <= 0)
                return;

            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == order.UserId);
            if (user == null) return;

            var oldPct = user.LoyaltyDiscountPercentage;
            var oldOverride = user.LoyaltyDiscountIsManualOverride;
            var oldActivatedAt = user.LoyaltyDiscountActivatedAt;
            var oldLastUsedAt = user.LoyaltyDiscountLastUsedAt;

            // Nothing was consumed for a lifetime customer, so there is nothing to give back —
            // and restoring would be actively wrong: it would overwrite the CURRENT percentage
            // (an admin may have changed it since) with the figure this order happened to use.
            if (user.LoyaltyDiscountIsLifetime) return;

            // Restore the percentage snapshot from the order. We do NOT recreate the reminder
            // NotificationLog rows that were deleted on consumption — the spec is explicit that
            // the next natural inactivity cycle re-triggers them if eligible.
            user.LoyaltyDiscountPercentage = order.LoyaltyDiscountPercentage;
            user.LoyaltyDiscountIsManualOverride = false;
            user.LoyaltyDiscountActivatedAt ??= DateTime.UtcNow;
            user.LoyaltyDiscountLastUsedAt = null;

            await _context.SaveChangesAsync();

            await _auditService.LogLoyaltyDiscountChangeAsync(
                user.Id, ActionReversed,
                oldPct, oldOverride, oldActivatedAt, oldLastUsedAt,
                user.LoyaltyDiscountPercentage, user.LoyaltyDiscountIsManualOverride,
                user.LoyaltyDiscountActivatedAt, user.LoyaltyDiscountLastUsedAt,
                adminUserId: null);

            _logger.LogInformation("Loyalty discount {Percentage}% restored to user {UserId} after cancelling order {OrderId}",
                order.LoyaltyDiscountPercentage, user.Id, orderId);
        }

        public (decimal loyaltyAmount, decimal loyaltyPercentage, decimal subscriptionAmount, decimal promoAmount)
            ResolveStacking(decimal loyaltyCandidateAmount, decimal loyaltyCandidatePercentage,
                            decimal subscriptionAmount, decimal promoAmount)
        {
            // Single implementation lives in OrderPricingCalculator (mirrored on the frontend).
            return OrderPricingCalculator.ResolveLoyaltyStacking(
                loyaltyCandidateAmount, loyaltyCandidatePercentage, subscriptionAmount, promoAmount);
        }

        private static LoyaltyDiscountDto Project(User user)
        {
            // Status derivation: "None" / "Auto" / "Manual" / "Used" — used by the admin UI for
            // a friendly label without exposing the underlying boolean.
            string status;
            if (user.LoyaltyDiscountPercentage > 0)
                // Lifetime outranks Manual: both are admin-set, but only one keeps applying after
                // it has been used, and that is the fact somebody reading the label needs.
                status = user.LoyaltyDiscountIsLifetime
                    ? "Lifetime"
                    : user.LoyaltyDiscountIsManualOverride ? "Manual" : "Auto";
            else if (user.LoyaltyDiscountLastUsedAt.HasValue)
                status = "Used";
            else
                status = "None";

            return new LoyaltyDiscountDto
            {
                Percentage = user.LoyaltyDiscountPercentage,
                IsManualOverride = user.LoyaltyDiscountIsManualOverride,
                IsLifetime = user.LoyaltyDiscountIsLifetime,
                ActivatedAt = user.LoyaltyDiscountActivatedAt,
                LastUsedAt = user.LoyaltyDiscountLastUsedAt,
                Status = status,
            };
        }
    }
}
