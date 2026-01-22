using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Data;

namespace DreamCleaningBackend.Services
{
    public class AuditLogCleanupService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<AuditLogCleanupService> _logger;
        private readonly TimeSpan _cleanupInterval = TimeSpan.FromDays(1); // Run once per day
        private readonly TimeSpan _retentionPeriod = TimeSpan.FromDays(180); // 6 months retention
        private int _consecutiveErrors = 0;
        private const int MAX_CONSECUTIVE_ERRORS = 5;

        public AuditLogCleanupService(
            IServiceProvider serviceProvider,
            ILogger<AuditLogCleanupService> logger)
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
                    await CleanupOldAuditLogs();
                    _consecutiveErrors = 0; // Reset on success
                }
                catch (Exception ex)
                {
                    _consecutiveErrors++;
                    _logger.LogError(ex, $"Error occurred while cleaning up old audit logs (attempt {_consecutiveErrors})");

                    if (_consecutiveErrors >= MAX_CONSECUTIVE_ERRORS)
                    {
                        _logger.LogCritical("Too many consecutive errors in AuditLogCleanupService. Stopping service.");
                        break;
                    }
                }

                try
                {
                    // Use exponential backoff if errors occurred
                    var delay = _consecutiveErrors > 0
                        ? TimeSpan.FromHours(6 * _consecutiveErrors) // 6, 12, 18, 24, 30 hours
                        : _cleanupInterval; // Normal 1 day delay

                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    // Expected when cancellation is requested
                    _logger.LogInformation("AuditLogCleanupService cancellation requested");
                    break;
                }
            }

            _logger.LogInformation("AuditLogCleanupService stopped");
        }

        private async Task CleanupOldAuditLogs()
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // Calculate cutoff date: 6 months ago
            var cutoffDate = DateTime.UtcNow.Subtract(_retentionPeriod);

            // First, count how many logs will be deleted (for logging purposes)
            var countToDelete = await context.AuditLogs
                .Where(a => a.CreatedAt < cutoffDate)
                .CountAsync();

            if (countToDelete > 0)
            {
                _logger.LogInformation($"Found {countToDelete} audit logs older than 6 months to cleanup (cutoff date: {cutoffDate:yyyy-MM-dd HH:mm:ss} UTC)");

                // Delete in batches to avoid memory issues and database locks
                const int batchSize = 1000;
                int totalDeleted = 0;
                int batchNumber = 0;

                while (true)
                {
                    // Get a batch of logs to delete (only load what we need)
                    var batch = await context.AuditLogs
                        .Where(a => a.CreatedAt < cutoffDate)
                        .OrderBy(a => a.Id)
                        .Take(batchSize)
                        .ToListAsync();

                    if (!batch.Any())
                        break;

                    // Delete this batch
                    context.AuditLogs.RemoveRange(batch);
                    var deleted = await context.SaveChangesAsync();

                    totalDeleted += deleted;
                    batchNumber++;
                    _logger.LogInformation($"Deleted batch {batchNumber} ({deleted} audit logs). Total deleted so far: {totalDeleted}");

                    // Small delay between batches to avoid overwhelming the database
                    await Task.Delay(100);
                }

                _logger.LogInformation($"Cleanup completed. Removed {totalDeleted} audit logs older than 6 months");
            }
            else
            {
                _logger.LogInformation($"No audit logs older than 6 months found (cutoff date: {cutoffDate:yyyy-MM-dd HH:mm:ss} UTC)");
            }
        }
    }
}
