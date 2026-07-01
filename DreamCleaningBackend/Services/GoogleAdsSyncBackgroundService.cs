using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Services.Interfaces;

namespace DreamCleaningBackend.Services
{
    /// <summary>
    /// Runs <see cref="IGoogleAdsCostService.SyncRecentAsync"/> once per day during the quiet
    /// New York early-morning hours. Pattern mirrors GoogleReviewSyncService / LoyaltyReengagement:
    /// an hourly tick with an in-process "already ran today" guard, so a restart mid-window still
    /// catches up within the hour. A failed sync only logs — it never crashes the host.
    /// </summary>
    public class GoogleAdsSyncBackgroundService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<GoogleAdsSyncBackgroundService> _logger;

        // Fire inside the 3:00 AM New York hour (low traffic). Hourly ticks outside it are no-ops.
        private const int SyncHourNy = 3;
        private static readonly TimeSpan TickInterval = TimeSpan.FromHours(1);

        // NY date of the last successful run, so we run at most once per calendar day.
        private DateTime? _lastRunNyDate;

        public GoogleAdsSyncBackgroundService(
            IServiceProvider serviceProvider,
            ILogger<GoogleAdsSyncBackgroundService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Let migrations/DB settle before the first tick.
            await Task.Delay(TimeSpan.FromSeconds(90), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var nowNy = NyTimeHelper.NowNy;
                    if (nowNy.Hour == SyncHourNy && _lastRunNyDate != nowNy.Date)
                    {
                        using var scope = _serviceProvider.CreateScope();
                        var service = scope.ServiceProvider.GetRequiredService<IGoogleAdsCostService>();

                        if (service.IsConfigured)
                        {
                            var result = await service.SyncRecentAsync(stoppingToken);
                            _logger.LogInformation(
                                "Google Ads daily sync completed: {Days} day(s) updated.", result.DaysSynced);
                        }

                        // Mark done for today even when unconfigured, so we don't re-check hourly.
                        _lastRunNyDate = nowNy.Date;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Never let a bad run kill the service; we retry next tick.
                    _logger.LogError(ex, "Google Ads daily sync failed.");
                }

                try
                {
                    await Task.Delay(TickInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}
