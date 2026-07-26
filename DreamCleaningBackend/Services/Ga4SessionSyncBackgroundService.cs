using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Services.Interfaces;

namespace DreamCleaningBackend.Services
{
    /// <summary>
    /// Nightly GA4 session reconciliation: re-pulls the trailing window of finalized days and
    /// overwrites our raw first-party counts with GA4's bot-filtered numbers. Same 3am-NY hourly-tick
    /// guard as <see cref="GoogleAdsSyncBackgroundService"/> / <see cref="SearchConsoleSyncBackgroundService"/>,
    /// so a restart mid-window still catches up within the hour. A failed run only logs.
    /// </summary>
    public class Ga4SessionSyncBackgroundService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<Ga4SessionSyncBackgroundService> _logger;

        private const int SyncHourNy = 3;
        private static readonly TimeSpan TickInterval = TimeSpan.FromHours(1);

        private DateTime? _lastRunNyDate;

        public Ga4SessionSyncBackgroundService(
            IServiceProvider serviceProvider,
            ILogger<Ga4SessionSyncBackgroundService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(150), stoppingToken); // let migrations/DB settle

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var nowNy = NyTimeHelper.NowNy;
                    if (nowNy.Hour == SyncHourNy && _lastRunNyDate != nowNy.Date)
                    {
                        using var scope = _serviceProvider.CreateScope();
                        var service = scope.ServiceProvider.GetRequiredService<IGa4SessionSyncService>();

                        if (service.IsConfigured)
                        {
                            var result = await service.ReconcileRecentAsync(stoppingToken);
                            _logger.LogInformation(
                                "GA4 session reconcile completed: {Days} day(s) overwritten ({Sessions} sessions).",
                                result.DaysOverwritten, result.TotalSessions);
                        }

                        _lastRunNyDate = nowNy.Date; // mark done even when unconfigured
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "GA4 session reconcile failed.");
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
