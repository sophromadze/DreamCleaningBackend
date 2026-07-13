using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;

namespace DreamCleaningBackend.Services
{
    /// <summary>
    /// Scheduled blog generation. Checks hourly; when the master switch (BlogSettings row,
    /// admin-toggleable) is on, at least Blog:GenerationIntervalDays have passed since the
    /// last attempt, and the current UTC hour is >= Blog:GenerationHourUtc, it takes the
    /// highest-priority Queued topic and produces a PendingReview draft. The outcome —
    /// success or failure — is written to BlogSettings.LastRunAt/LastRunResult so silent
    /// failures show up in the admin Settings tab without reading server logs.
    /// </summary>
    public class BlogGenerationBackgroundService : BackgroundService
    {
        private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IConfiguration _configuration;
        private readonly ILogger<BlogGenerationBackgroundService> _logger;

        public BlogGenerationBackgroundService(
            IServiceScopeFactory scopeFactory,
            IConfiguration configuration,
            ILogger<BlogGenerationBackgroundService> logger)
        {
            _scopeFactory = scopeFactory;
            _configuration = configuration;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Let the app finish booting (migrations run at startup) before the first check.
            try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); }
            catch (OperationCanceledException) { return; }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunOnceIfDueAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // Never let one bad cycle kill the worker.
                    _logger.LogError(ex, "Blog generation cycle failed unexpectedly");
                }

                try { await Task.Delay(CheckInterval, stoppingToken); }
                catch (OperationCanceledException) { return; }
            }
        }

        private async Task RunOnceIfDueAsync(CancellationToken cancellationToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var settings = await context.BlogSettings.OrderBy(s => s.Id).FirstOrDefaultAsync(cancellationToken);
            if (settings == null || !settings.AutoGenerateEnabled)
                return;

            var intervalDays = Math.Max(1, _configuration.GetValue<int>("Blog:GenerationIntervalDays", 7));
            var generationHourUtc = Math.Clamp(_configuration.GetValue<int>("Blog:GenerationHourUtc", 9), 0, 23);
            var now = DateTime.UtcNow;

            if (settings.LastRunAt.HasValue && now - settings.LastRunAt.Value < TimeSpan.FromDays(intervalDays))
                return;

            if (now.Hour < generationHourUtc)
                return;

            // Take the next queued topic (lowest priority number first).
            var topic = await context.BlogTopics
                .Where(t => t.Status == BlogTopicStatus.Queued)
                .OrderBy(t => t.Priority)
                .ThenBy(t => t.Id)
                .FirstOrDefaultAsync(cancellationToken);

            settings.LastRunAt = now;

            if (topic == null)
            {
                settings.LastRunResult = "Skipped — topic queue is empty. Add topics in the Blog admin.";
                await context.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Blog auto-generation skipped: queue empty");
                return;
            }

            try
            {
                var generation = scope.ServiceProvider.GetRequiredService<IBlogGenerationService>();
                var post = await generation.GenerateFromTopicAsync(topic, cancellationToken);
                settings.LastRunResult = $"OK — created draft \"{post.Title}\" (pending review).";
                _logger.LogInformation("Blog auto-generation created draft #{Id}", post.Id);
            }
            catch (Exception ex)
            {
                settings.LastRunResult = $"Failed: {Truncate(ex.Message, 800)}";
                _logger.LogError(ex, "Blog auto-generation failed for topic #{TopicId}", topic.Id);
            }

            await context.SaveChangesAsync(cancellationToken);
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
    }
}
