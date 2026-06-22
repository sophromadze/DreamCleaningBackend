using DreamCleaningBackend.Services.Interfaces;

namespace DreamCleaningBackend.Services
{
    /// <summary>
    /// Periodically evaluates enabled CRM automation rules and creates admin review alerts.
    /// Non-sending by design — see <see cref="AutomationEvaluationService"/>. Mirrors the
    /// scope-per-run + exponential-backoff pattern used by the other background workers.
    /// </summary>
    public class AutomationBackgroundService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<AutomationBackgroundService> _logger;
        private int _consecutiveErrors = 0;
        private const int MAX_CONSECUTIVE_ERRORS = 5;

        public AutomationBackgroundService(IServiceProvider serviceProvider, ILogger<AutomationBackgroundService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Let the app finish starting (and migrations apply) before the first evaluation.
            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);

            // Ensure the default (disabled) rules exist so the Automation tab has something to show.
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<IAutomationEvaluationService>();
                await svc.EnsureDefaultRulesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AutomationBackgroundService: failed to seed default rules");
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var svc = scope.ServiceProvider.GetRequiredService<IAutomationEvaluationService>();
                    await svc.EvaluateAllEnabledAsync();
                    _consecutiveErrors = 0;
                }
                catch (Exception ex)
                {
                    _consecutiveErrors++;
                    _logger.LogError(ex, "Error in automation evaluation (attempt {Attempt})", _consecutiveErrors);
                    if (_consecutiveErrors >= MAX_CONSECUTIVE_ERRORS)
                    {
                        _logger.LogCritical("Too many consecutive errors in AutomationBackgroundService. Stopping.");
                        break;
                    }
                }

                try
                {
                    // Lapsed-customer state changes slowly — every 6 hours is plenty.
                    var delay = _consecutiveErrors > 0
                        ? TimeSpan.FromMinutes(15 * _consecutiveErrors)
                        : TimeSpan.FromHours(6);
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}
