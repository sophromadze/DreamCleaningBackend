using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Data;

namespace DreamCleaningBackend.Services
{
    /// <summary>
    /// Daily retention sweep for chat-agent photos (AuditLogCleanupService pattern).
    /// Deletes the image file from disk once ImageExpiresAt has passed and nulls
    /// ImagePath/ImageExpiresAt on the row — the message text/row itself is kept.
    /// </summary>
    public class ChatImageCleanupService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IConfiguration _configuration;
        private readonly ILogger<ChatImageCleanupService> _logger;
        private readonly TimeSpan _cleanupInterval = TimeSpan.FromDays(1);
        private int _consecutiveErrors = 0;
        private const int MAX_CONSECUTIVE_ERRORS = 5;

        public ChatImageCleanupService(
            IServiceProvider serviceProvider,
            IConfiguration configuration,
            ILogger<ChatImageCleanupService> logger)
        {
            _serviceProvider = serviceProvider;
            _configuration = configuration;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Wait 90 seconds before starting to avoid startup contention
            await Task.Delay(TimeSpan.FromSeconds(90), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CleanupExpiredImagesAsync(stoppingToken);
                    _consecutiveErrors = 0;
                }
                catch (Exception ex)
                {
                    _consecutiveErrors++;
                    _logger.LogError(ex, "Error cleaning up expired chat images (attempt {Attempt})", _consecutiveErrors);

                    if (_consecutiveErrors >= MAX_CONSECUTIVE_ERRORS)
                    {
                        _logger.LogCritical("Too many consecutive errors in ChatImageCleanupService. Stopping service.");
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

        private async Task CleanupExpiredImagesAsync(CancellationToken stoppingToken)
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var now = DateTime.UtcNow;
            var expired = await context.ChatAgentMessages
                .Where(m => m.ImagePath != null && m.ImageExpiresAt != null && m.ImageExpiresAt < now)
                .ToListAsync(stoppingToken);

            if (expired.Count == 0)
            {
                _logger.LogInformation("ChatImageCleanup: no expired chat images.");
                return;
            }

            var uploadRoot = _configuration["FileUpload:Path"];
            var filesDeleted = 0;

            foreach (var message in expired)
            {
                try
                {
                    if (!string.IsNullOrEmpty(uploadRoot))
                    {
                        // ImagePath is always /chat-photos/{guid}.{ext}; take only the file
                        // name so a corrupted row can never delete outside chat-photos.
                        var fileName = Path.GetFileName(message.ImagePath!);
                        var fullPath = Path.Combine(Path.GetFullPath(uploadRoot), "chat-photos", fileName);
                        if (File.Exists(fullPath))
                        {
                            File.Delete(fullPath);
                            filesDeleted++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Keep going — the row is still cleared so the URL stops being served.
                    _logger.LogWarning(ex, "Failed to delete expired chat image {Path}", message.ImagePath);
                }

                message.ImagePath = null;
                message.ImageExpiresAt = null;
            }

            await context.SaveChangesAsync(stoppingToken);
            _logger.LogInformation(
                "ChatImageCleanup: purged {Rows} expired image references ({Files} files deleted from disk).",
                expired.Count, filesDeleted);
        }
    }
}
