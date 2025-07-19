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
        private readonly TimeSpan _cleanupInterval = TimeSpan.FromMinutes(30);
        private int _consecutiveErrors = 0;
        private const int MAX_CONSECUTIVE_ERRORS = 5;

        public UnverifiedUserCleanupService(
            IServiceProvider serviceProvider,
            ILogger<UnverifiedUserCleanupService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Wait 60 seconds before starting to avoid startup issues
            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CleanupUnverifiedUsers();
                    _consecutiveErrors = 0; // Reset on success
                }
                catch (Exception ex)
                {
                    _consecutiveErrors++;
                    _logger.LogError(ex, $"Error occurred while cleaning up unverified users (attempt {_consecutiveErrors})");

                    if (_consecutiveErrors >= MAX_CONSECUTIVE_ERRORS)
                    {
                        _logger.LogCritical("Too many consecutive errors in UnverifiedUserCleanupService. Stopping service.");
                        break;
                    }
                }

                try
                {
                    // Use exponential backoff if errors occurred
                    var delay = _consecutiveErrors > 0
                        ? TimeSpan.FromMinutes(5 * _consecutiveErrors) // 5, 10, 15, 20, 25 minutes
                        : _cleanupInterval; // Normal 30 minutes delay

                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    // Expected when cancellation is requested
                    _logger.LogInformation("UnverifiedUserCleanupService cancellation requested");
                    break;
                }
            }

            _logger.LogInformation("UnverifiedUserCleanupService stopped");
        }

        private async Task CleanupUnverifiedUsers()
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // Find users who:
            // 1. Are not email verified
            // 2. Were created more than 1 hour ago
            // 3. Have no orders
            var cutoffTime = DateTime.UtcNow.AddHours(-24);

            var usersToDelete = await context.Users
                .Where(u => !u.IsEmailVerified &&
                           u.CreatedAt < cutoffTime &&
                           !context.Orders.Any(o => o.UserId == u.Id))
                .ToListAsync();

            if (usersToDelete.Any())
            {
                _logger.LogInformation($"Found {usersToDelete.Count} unverified users to cleanup");

                foreach (var user in usersToDelete)
                {
                    try
                    {
                        context.Users.Remove(user);
                        _logger.LogInformation($"Removing unverified user: {user.Email}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Failed to remove unverified user: {user.Email}");
                    }
                }

                await context.SaveChangesAsync();
                _logger.LogInformation($"Cleanup completed. Removed {usersToDelete.Count} unverified users");
            }
        }
    }
}