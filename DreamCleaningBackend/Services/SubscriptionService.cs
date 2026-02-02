using DreamCleaningBackend.Data;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services
{
    public class SubscriptionService : ISubscriptionService
    {
        private readonly ApplicationDbContext _context;

        public SubscriptionService(ApplicationDbContext context)
        {
            _context = context;
        }

        private static DateTime NormalizeStartDate(DateTime serviceDate)
        {
            // Subscription countdown should start from the service date (date-only).
            return serviceDate.Date;
        }

        private static int GetAdjustedDurationDays(int subscriptionDays)
        {
            // Do NOT hardcode weekly/bi-weekly/monthly here.
            // Whatever value you configure in the admin panel (`SubscriptionDays`) gets a +1 day grace period.
            // Examples:
            // - Weekly configured as 7 => expires after 8 days
            // - Bi-weekly configured as 14 => expires after 15 days
            // - Monthly configured as 31 => expires after 32 days
            if (subscriptionDays <= 0) return 0;
            return subscriptionDays + 1;
        }

        private static DateTime? CalculateExpiryDate(DateTime startDate, int subscriptionDays)
        {
            var adjustedDays = GetAdjustedDurationDays(subscriptionDays);
            if (adjustedDays <= 0) return null;

            // Store expiry as end-of-day for a more intuitive "expires on" behavior.
            return startDate.AddDays(adjustedDays + 1).AddTicks(-1);
        }

        public async Task<bool> ActivateSubscription(int userId, int subscriptionId, DateTime startDate)
        {
            var user = await _context.Users.FindAsync(userId);
            if (user == null) return false;

            var subscription = await _context.Subscriptions.FindAsync(subscriptionId);
            if (subscription == null || !subscription.IsActive) return false;

            var normalizedStartDate = NormalizeStartDate(startDate);
            var expiryDate = CalculateExpiryDate(normalizedStartDate, subscription.SubscriptionDays);

            user.SubscriptionId = subscriptionId;
            user.SubscriptionStartDate = normalizedStartDate;
            user.SubscriptionExpiryDate = expiryDate;
            // Keep last order date aligned with the service date (not order placement time).
            user.LastOrderDate = normalizedStartDate;
            user.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            return true;
        }

        public async Task<bool> CheckAndUpdateSubscriptionStatus(int userId)
        {
            var user = await _context.Users
                .Include(u => u.Subscription)
                .FirstOrDefaultAsync(u => u.Id == userId);

            if (user == null || user.SubscriptionId == null) return false;

            // Check if subscription has expired
            if (user.SubscriptionExpiryDate.HasValue &&
                user.SubscriptionExpiryDate.Value < DateTime.UtcNow)
            {
                await DeactivateSubscription(userId);
                return false;
            }

            return true;
        }

        public async Task<bool> RenewSubscription(int userId, DateTime startDate)
        {
            var user = await _context.Users
                .Include(u => u.Subscription)
                .FirstOrDefaultAsync(u => u.Id == userId);

            if (user == null || user.Subscription == null) return false;

            var normalizedStartDate = NormalizeStartDate(startDate);
            user.SubscriptionStartDate = normalizedStartDate;
            user.SubscriptionExpiryDate = CalculateExpiryDate(normalizedStartDate, user.Subscription.SubscriptionDays);
            user.LastOrderDate = normalizedStartDate;
            user.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            return true;
        }

        public async Task<bool> DeactivateSubscription(int userId)
        {
            var user = await _context.Users.FindAsync(userId);
            if (user == null) return false;

            user.SubscriptionId = null;
            user.SubscriptionStartDate = null;
            user.SubscriptionExpiryDate = null;
            user.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            return true;
        }

        public async Task<decimal> GetUserDiscountPercentage(int userId)
        {
            var user = await _context.Users
                .Include(u => u.Subscription)
                .FirstOrDefaultAsync(u => u.Id == userId);

            if (user?.Subscription == null) return 0;

            // Check if subscription is still valid
            if (await CheckAndUpdateSubscriptionStatus(userId))
            {
                return user.Subscription.DiscountPercentage;
            }

            return 0;
        }
    }
}
