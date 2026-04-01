namespace DreamCleaningBackend.Services;

public class LiveChatCleanupService : BackgroundService
{
    private readonly LiveChatSessionManager _sessionManager;
    private readonly ILogger<LiveChatCleanupService> _logger;

    public LiveChatCleanupService(LiveChatSessionManager sessionManager, ILogger<LiveChatCleanupService> logger)
    {
        _sessionManager = sessionManager;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken);
            _sessionManager.CleanupInactiveSessions(TimeSpan.FromHours(2));
            _logger.LogInformation("LiveChat: Cleaned up inactive sessions");
        }
    }
}
