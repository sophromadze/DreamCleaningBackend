using System.Net;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services
{
    /// <summary>
    /// Periodically syncs Google reviews into the local GoogleReviews table via
    /// <see cref="IGoogleBusinessProfileService"/>: once shortly after startup (so a replaced
    /// refresh token can be verified with a restart), then every 12h.
    ///
    /// Failures are logged at Error and recorded in <see cref="GoogleReviewSyncState"/>. One alert
    /// email goes to GoogleBusinessProfile:AlertEmail per failure streak — immediately on
    /// invalid_grant (refresh token expired/revoked), otherwise after
    /// <see cref="PersistentFailureThreshold"/> failed runs in a row — and nothing more until a
    /// successful sync re-arms it. Missing/empty config never crashes or alerts: it logs one Warning.
    /// </summary>
    public class GoogleReviewSyncService : BackgroundService
    {
        private const string BusinessName = "Dream Cleaning";
        private const int PersistentFailureThreshold = 3;
        private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(90);
        private static readonly TimeSpan Interval = TimeSpan.FromHours(12);

        private readonly IServiceProvider _serviceProvider;
        private readonly IConfiguration _configuration;
        private readonly ILogger<GoogleReviewSyncService> _logger;

        // One Warning per process for each missing-config case, not one per run.
        private bool _warnedNotConfigured;
        private bool _warnedNoAlertEmail;

        public GoogleReviewSyncService(
            IServiceProvider serviceProvider,
            IConfiguration configuration,
            ILogger<GoogleReviewSyncService> logger)
        {
            _serviceProvider = serviceProvider;
            _configuration = configuration;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                // First run shortly after startup (once migrations/DB are ready), not 12h later.
                await Task.Delay(StartupDelay, stoppingToken);

                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        await RunOnceAsync(stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // An escaping exception would stop the host (.NET 8 default); keep the loop alive.
                        _logger.LogError(ex, "Unexpected error in Google review sync loop.");
                    }

                    await Task.Delay(Interval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Shutting down.
            }
        }

        private async Task RunOnceAsync(CancellationToken stoppingToken)
        {
            using var scope = _serviceProvider.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IGoogleBusinessProfileService>();

            if (!service.IsConfigured)
            {
                if (!_warnedNotConfigured)
                {
                    _logger.LogWarning("GoogleBusinessProfile is not configured (ClientId, ClientSecret, RefreshToken, AccountId, LocationId); Google review sync is disabled.");
                    _warnedNotConfigured = true;
                }
                return;
            }

            try
            {
                var result = await service.SyncReviewsAsync(stoppingToken);
                _logger.LogInformation(
                    "Google review sync completed. Stored reviews: {Stored}, Google total: {Total}, average rating: {Average}",
                    result.StoredCount, result.TotalReviewCount, result.AverageRating);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var isInvalidGrant = ex is GoogleReviewSyncException { IsInvalidGrant: true };
                if (isInvalidGrant)
                    _logger.LogError(ex, "Google review sync failed: refresh token expired or revoked (invalid_grant). Generate a new GoogleBusinessProfile:RefreshToken.");
                else
                    _logger.LogError(ex, "Google review sync failed.");

                await RecordFailureAsync(scope.ServiceProvider, ex, isInvalidGrant, stoppingToken);
            }
        }

        /// <summary>Persists the failure and sends the streak's alert email when due. Never throws.</summary>
        private async Task RecordFailureAsync(IServiceProvider services, Exception error, bool isInvalidGrant, CancellationToken stoppingToken)
        {
            try
            {
                var context = services.GetRequiredService<ApplicationDbContext>();
                var state = await context.GoogleReviewSyncStates
                    .FirstOrDefaultAsync(s => s.Id == GoogleReviewSyncState.SingletonId, stoppingToken);
                if (state == null)
                {
                    state = new GoogleReviewSyncState();
                    context.GoogleReviewSyncStates.Add(state);
                }

                var now = DateTime.UtcNow;
                var message = error.Message;
                state.LastAttemptAt = now;
                state.LastError = message.Length > 2000 ? message[..2000] : message;
                state.LastErrorAt = now;
                state.ConsecutiveFailures++;

                var alertDue = state.AlertSentAt == null &&
                               (isInvalidGrant || state.ConsecutiveFailures >= PersistentFailureThreshold);
                if (alertDue && await TrySendAlertAsync(services, state, isInvalidGrant))
                    state.AlertSentAt = now;

                await context.SaveChangesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not record Google review sync failure state.");
            }
        }

        /// <summary>Returns true only when the alert email was actually handed to SMTP.</summary>
        private async Task<bool> TrySendAlertAsync(IServiceProvider services, GoogleReviewSyncState state, bool isInvalidGrant)
        {
            var to = _configuration["GoogleBusinessProfile:AlertEmail"];
            if (string.IsNullOrWhiteSpace(to))
            {
                if (!_warnedNoAlertEmail)
                {
                    _logger.LogWarning("GoogleBusinessProfile:AlertEmail is not configured; Google review sync failure alert not sent.");
                    _warnedNoAlertEmail = true;
                }
                return false;
            }

            // EmailService returns silently when sending is disabled, which would look like success.
            if (!_configuration.GetValue("Email:EnableEmailSending", true))
            {
                _logger.LogError("Email sending is disabled (Email:EnableEmailSending=false); Google review sync failure alert to {To} not sent.", to);
                return false;
            }

            try
            {
                var emailService = services.GetRequiredService<IEmailService>();
                var (subject, html) = BuildAlertEmail(state, isInvalidGrant);
                await emailService.SendEmailAsync(to, subject, html);
                _logger.LogInformation("Google review sync failure alert sent to {To}.", to);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send Google review sync failure alert to {To}.", to);
                return false;
            }
        }

        private static (string Subject, string Html) BuildAlertEmail(GoogleReviewSyncState state, bool isInvalidGrant)
        {
            var lastSuccess = state.LastSuccessAt.HasValue
                ? state.LastSuccessAt.Value.ToString("yyyy-MM-dd HH:mm 'UTC'")
                : "never";

            var subject = isInvalidGrant
                ? $"[{BusinessName}] Google reviews sync stopped: refresh token expired or revoked"
                : $"[{BusinessName}] Google reviews sync is failing";

            var action = isInvalidGrant
                ? "The Google OAuth refresh token is expired or revoked (invalid_grant). Generate a new refresh token for the Business Profile OAuth client, set it as GoogleBusinessProfile:RefreshToken on the server, and restart the API — a sync runs about 90 seconds after startup."
                : $"The sync has failed {state.ConsecutiveFailures} times in a row. Check the API logs for GoogleReviewSyncService / GoogleBusinessProfileService errors.";

            var html = $@"
<p>The Google reviews sync for <strong>{BusinessName}</strong> is failing. New Google reviews and the review count on the website will not update until it is fixed.</p>
<p><strong>What to do:</strong> {WebUtility.HtmlEncode(action)}</p>
<table cellpadding=""4"">
  <tr><td><strong>Last successful sync</strong></td><td>{lastSuccess}</td></tr>
  <tr><td><strong>Failed runs in a row</strong></td><td>{state.ConsecutiveFailures}</td></tr>
  <tr><td><strong>Error</strong></td><td><code>{WebUtility.HtmlEncode(state.LastError ?? "")}</code></td></tr>
</table>
<p>This is the only alert for this outage; the next one is sent only after a successful sync and a new failure.</p>";

            return (subject, html);
        }
    }
}
