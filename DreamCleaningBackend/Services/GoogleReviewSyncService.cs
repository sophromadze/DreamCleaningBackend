namespace DreamCleaningBackend.Services
{
    /// <summary>
    /// Periodically syncs Google reviews into the local GoogleReviews table via
    /// <see cref="IGoogleBusinessProfileService"/>. No-op until GoogleBusinessProfile is configured.
    /// </summary>
    public class GoogleReviewSyncService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<GoogleReviewSyncService> _logger;
        private readonly TimeSpan _interval = TimeSpan.FromHours(12);

        public GoogleReviewSyncService(
            IServiceProvider serviceProvider,
            ILogger<GoogleReviewSyncService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Delay startup so migrations/DB are ready first.
            await Task.Delay(TimeSpan.FromSeconds(90), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var service = scope.ServiceProvider.GetRequiredService<IGoogleBusinessProfileService>();

                    if (service.IsConfigured)
                    {
                        var count = await service.SyncReviewsAsync(stoppingToken);
                        _logger.LogInformation("Google review sync completed. Stored reviews: {Count}", count);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during Google review sync.");
                }

                try
                {
                    await Task.Delay(_interval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}
