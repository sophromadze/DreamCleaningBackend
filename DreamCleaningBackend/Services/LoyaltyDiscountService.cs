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

        public async Task<LoyaltyDiscountDto> SetManualAsync(int userId, decimal percentage, int adminUserId)
        {
            if (percentage < 0 || percentage > 100)
                throw new ArgumentOutOfRangeException(nameof(percentage), "Percentage must be between 0 and 100");

            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId)
                ?? throw new InvalidOperationException($"User #{userId} not found");

            // Setting to exactly 0 via the manual endpoint is semantically a clear — let the
            // user re-enter the auto-managed flow rather than being permanently frozen at 0.
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
            if (oldPct == 0 && !oldOverride && oldActivatedAt == null)
            {
                return Project(user);
            }

            user.LoyaltyDiscountPercentage = 0;
            user.LoyaltyDiscountIsManualOverride = false;
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
            // Fast path: nothing to gate.
            if (loyaltyCandidateAmount <= 0m || loyaltyCandidatePercentage <= 0m)
                return (0m, 0m, subscriptionAmount, promoAmount);

            decimal loyalty = loyaltyCandidateAmount;
            decimal loyaltyPct = loyaltyCandidatePercentage;

            // Round 1: loyalty vs subscription. Both are subTotal-percentage-based so dollar
            // comparison is equivalent to percentage comparison. Tie → subscription wins
            // (preserves existing user expectation that an actively-paid subscription discount
            // shows up). Ties are extremely unlikely in practice.
            if (loyalty > subscriptionAmount)
            {
                subscriptionAmount = 0m;
            }
            else
            {
                loyalty = 0m;
                loyaltyPct = 0m;
            }

            // Round 2: round-1 winner (loyalty, if it survived) vs promo/special/first-time.
            // If subscription won round 1, loyalty is already zero — promo continues to stack
            // with subscription as before (existing behavior, untouched).
            if (loyalty > 0m)
            {
                if (loyalty > promoAmount)
                {
                    promoAmount = 0m;
                }
                else
                {
                    loyalty = 0m;
                    loyaltyPct = 0m;
                }
            }

            return (loyalty, loyaltyPct, subscriptionAmount, promoAmount);
        }

        private static LoyaltyDiscountDto Project(User user)
        {
            // Status derivation: "None" / "Auto" / "Manual" / "Used" — used by the admin UI for
            // a friendly label without exposing the underlying boolean.
            string status;
            if (user.LoyaltyDiscountPercentage > 0)
                status = user.LoyaltyDiscountIsManualOverride ? "Manual" : "Auto";
            else if (user.LoyaltyDiscountLastUsedAt.HasValue)
                status = "Used";
            else
                status = "None";

            return new LoyaltyDiscountDto
            {
                Percentage = user.LoyaltyDiscountPercentage,
                IsManualOverride = user.LoyaltyDiscountIsManualOverride,
                ActivatedAt = user.LoyaltyDiscountActivatedAt,
                LastUsedAt = user.LoyaltyDiscountLastUsedAt,
                Status = status,
            };
        }
    }
}
