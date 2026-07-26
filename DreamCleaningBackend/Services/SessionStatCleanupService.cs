using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Data;

namespace DreamCleaningBackend.Services
{
    /// <summary>
    /// Prunes very old SessionDailyStat rows. The table is aggregated (a handful of rows per day), so
    /// this is mostly hygiene — a 2-year window keeps year-over-year comparison while bounding growth.
    /// Mirrors AuditLogCleanupService (daily sweep, batched deletes, backoff on repeated failure).
    /// </summary>
    public class SessionStatCleanupService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<SessionStatCleanupService> _logger;
        private readonly TimeSpan _cleanupInterval = TimeSpan.FromDays(1);
        private readonly TimeSpan _retentionPeriod = TimeSpan.FromDays(730); // 2 years
        private int _consecutiveErrors = 0;
        private const int MAX_CONSECUTIVE_ERRORS = 5;

        public SessionStatCleanupService(
            IServiceProvider serviceProvider,
            ILogger<SessionStatCleanupService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(90), stoppingToken); // let startup settle

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CleanupOldStats();
                    _consecutiveErrors = 0;
                }
                catch (Exception ex)
                {
                    _consecutiveErrors++;
                    _logger.LogError(ex, "Error cleaning up old session stats (attempt {Attempt})", _consecutiveErrors);
                    if (_consecutiveErrors >= MAX_CONSECUTIVE_ERRORS)
                    {
                        _logger.LogCritical("Too many consecutive errors in SessionStatCleanupService. Stopping.");
                        break;
                    }
                }

                try
                {
                    var delay = _consecutiveErrors > 0
                        ? TimeSpan.FromHours(6 * _consecutiveErrors)
                        : _cleanupInterval;
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private async Task CleanupOldStats()
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var cutoff = DateTime.UtcNow.Date.Subtract(_retentionPeriod);

            const int batchSize = 1000;
            var totalDeleted = 0;
            while (true)
            {
                var batch = await context.SessionDailyStats
                    .Where(s => s.Date < cutoff)
                    .OrderBy(s => s.Id)
                    .Take(batchSize)
                    .ToListAsync();

                if (batch.Count == 0)
                    break;

                context.SessionDailyStats.RemoveRange(batch);
                totalDeleted += await context.SaveChangesAsync();
                await Task.Delay(100);
            }

            if (totalDeleted > 0)
                _logger.LogInformation("Pruned {Count} SessionDailyStat rows older than {Cutoff:yyyy-MM-dd}.", totalDeleted, cutoff);
        }
    }
}
