using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Data;

namespace DreamCleaningBackend.Services
{
    public class UnverifiedUserCleanupService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<UnverifiedUserCleanupService> _logger;
        private readonly TimeSpan _cleanupInterval = TimeSpan.FromMinutes(30); // Run every 30 minutes

        public UnverifiedUserCleanupService(
            IServiceProvider serviceProvider,
            ILogger<UnverifiedUserCleanupService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CleanupUnverifiedUsers();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error occurred while cleaning up unverified users");
                }

                await Task.Delay(_cleanupInterval, stoppingToken);
            }
        }

        private async Task CleanupUnverifiedUsers()
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // Find users who:
            // 1. Are not email verified
            // 2. Were created more than 1 hour ago
            // 3. Are using local auth (not social login)
            var cutoffTime = DateTime.Now.AddHours(-1);

            var unverifiedUsers = await context.Users
                .Where(u => !u.IsEmailVerified
                    && u.CreatedAt < cutoffTime
                    && u.AuthProvider == "Local")
                .ToListAsync();

            if (unverifiedUsers.Any())
            {
                _logger.LogInformation($"Found {unverifiedUsers.Count} unverified users to delete");

                // Delete associated data first (if any)
                foreach (var user in unverifiedUsers)
                {
                    // Delete any apartments
                    var apartments = await context.Apartments
                        .Where(a => a.UserId == user.Id)
                        .ToListAsync();
                    context.Apartments.RemoveRange(apartments);

                    // Delete any orders (shouldn't be any, but just in case)
                    var orders = await context.Orders
                        .Where(o => o.UserId == user.Id)
                        .ToListAsync();
                    context.Orders.RemoveRange(orders);
                }

                // Delete the users
                context.Users.RemoveRange(unverifiedUsers);
                await context.SaveChangesAsync();

                _logger.LogInformation($"Successfully deleted {unverifiedUsers.Count} unverified users");
            }
        }
    }
}