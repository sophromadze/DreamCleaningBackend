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

        public async Task<bool> ActivateSubscription(int userId, int subscriptionId)
        {
            var user = await _context.Users.FindAsync(userId);
            if (user == null) return false;

            var subscription = await _context.Subscriptions.FindAsync(subscriptionId);
            if (subscription == null || !subscription.IsActive) return false;

            user.SubscriptionId = subscriptionId;
            user.SubscriptionStartDate = DateTime.UtcNow;
            user.SubscriptionExpiryDate = DateTime.UtcNow.AddDays(subscription.SubscriptionDays);
            user.LastOrderDate = DateTime.UtcNow;
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

        public async Task<bool> RenewSubscription(int userId)
        {
            var user = await _context.Users
                .Include(u => u.Subscription)
                .FirstOrDefaultAsync(u => u.Id == userId);

            if (user == null || user.Subscription == null) return false;

            user.SubscriptionExpiryDate = DateTime.UtcNow.AddDays(user.Subscription.SubscriptionDays);
            user.LastOrderDate = DateTime.UtcNow;
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
