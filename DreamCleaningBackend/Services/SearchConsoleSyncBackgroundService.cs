using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Services.Interfaces;

namespace DreamCleaningBackend.Services
{
    /// <summary>
    /// Runs <see cref="ISearchConsoleSyncService.SyncRecentAsync"/> once per day during the quiet
    /// New York early-morning hours. Same pattern as <see cref="GoogleAdsSyncBackgroundService"/>:
    /// an hourly tick with an in-process "already ran today" guard, so a restart mid-window still
    /// catches up within the hour. A failed sync only logs — it never crashes the host.
    /// </summary>
    public class SearchConsoleSyncBackgroundService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<SearchConsoleSyncBackgroundService> _logger;

        // Fire inside the 3:00 AM New York hour (low traffic). Hourly ticks outside it are no-ops.
        private const int SyncHourNy = 3;
        private static readonly TimeSpan TickInterval = TimeSpan.FromHours(1);

        private DateTime? _lastRunNyDate;

        public SearchConsoleSyncBackgroundService(
            IServiceProvider serviceProvider,
            ILogger<SearchConsoleSyncBackgroundService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(120), stoppingToken); // let migrations/DB settle

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var nowNy = NyTimeHelper.NowNy;
                    if (nowNy.Hour == SyncHourNy && _lastRunNyDate != nowNy.Date)
                    {
                        using var scope = _serviceProvider.CreateScope();
                        var service = scope.ServiceProvider.GetRequiredService<ISearchConsoleSyncService>();

                        if (service.IsConfigured)
                        {
                            var result = await service.SyncRecentAsync(stoppingToken);
                            _logger.LogInformation(
                                "Search Console daily sync completed: {Rows} row(s) across {Days} day(s).",
                                result.RowsUpserted, result.DaysCovered);
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
                    _logger.LogError(ex, "Search Console daily sync failed.");
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
