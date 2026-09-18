using DreamCleaningBackend.Data;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services.Contracts
{
    /// <summary>
    /// Permanently removes contracts that have been ARCHIVED for longer than the retention window
    /// (default six months, <c>ContractRetention:HiddenMonths</c>).
    ///
    /// Pattern follows the other background workers here — hourly loop, one scope per cycle,
    /// backoff on repeated failures. It only acts once a day, because a retention sweep has no
    /// reason to run more often and a daily cadence keeps the log readable.
    ///
    /// IT NO LONGER PURGES WHATEVER IT FINDS. The sweep and the admin's "Full delete" button share
    /// <see cref="ContractPurgeService"/>, which refuses anything
    /// <see cref="Helpers.Contracts.ContractHardDeletePolicy"/> protects — a signature, a linked
    /// invoice, an amendment built from it. Before that gate existed this job would silently
    /// destroy an executed agreement six months after somebody archived it, unattended and with no
    /// decision behind it. Archive now means the document is preserved, and a test draft still
    /// ages out exactly as it always did.
    ///
    /// A contract the policy protects is simply SKIPPED and stays archived indefinitely, which is
    /// the intended resting place for it. It is logged once per sweep rather than per contract, so
    /// a steady population of preserved agreements does not fill the log every day.
    /// </summary>
    public class ContractRetentionService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<ContractRetentionService> _logger;
        private readonly IConfiguration _configuration;

        /// <summary>What the surviving ContractDeletionLog row names as the actor.</summary>
        public const string DeletedByLabel = "Retention job";

        private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
        private DateTime _lastSweepUtc = DateTime.MinValue;

        public ContractRetentionService(
            IServiceProvider serviceProvider,
            ILogger<ContractRetentionService> logger,
            IConfiguration configuration)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _configuration = configuration;
        }

        private int RetentionMonths =>
            Math.Max(1, _configuration.GetValue("ContractRetention:HiddenMonths", 6));

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var consecutiveFailures = 0;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (DateTime.UtcNow - _lastSweepUtc >= TimeSpan.FromHours(24))
                    {
                        await SweepAsync(stoppingToken);
                        _lastSweepUtc = DateTime.UtcNow;
                    }
                    consecutiveFailures = 0;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    consecutiveFailures++;
                    _logger.LogError(ex, "Contract retention sweep failed (attempt {Count}).", consecutiveFailures);
                }

                // Back off after repeated failures rather than hammering a broken database.
                var delay = consecutiveFailures > 3 ? TimeSpan.FromHours(6) : Interval;
                try { await Task.Delay(delay, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        /// <summary>Exposed so the behaviour can be exercised directly rather than only on a timer.</summary>
        /// <summary>Exposed so the behaviour can be exercised directly rather than only on a timer.</summary>
        public async Task<int> SweepAsync(CancellationToken cancellationToken = default)
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var purge = scope.ServiceProvider.GetRequiredService<ContractPurgeService>();

            var cutoff = DateTime.UtcNow.AddMonths(-RetentionMonths);

            var expired = await context.Contracts
                .Include(c => c.ContractClient)
                .Include(c => c.HiddenByUser)
                .Where(c => c.IsHidden && c.HiddenAt != null && c.HiddenAt <= cutoff)
                .ToListAsync(cancellationToken);

            if (expired.Count == 0) return 0;

            var purged = 0;
            var preserved = 0;

            foreach (var contract in expired)
            {
                try
                {
                    await purge.PurgeAsync(contract, DeletedByLabel, cancellationToken);
                    purged++;
                }
                catch (ContractPurgeRefusedException)
                {
                    // Protected by the shared policy — a signature, a linked invoice, an amendment
                    // built from it. It stays archived, which is where it belongs; this is the
                    // normal resting state for an executed agreement, not a failure.
                    preserved++;
                }
                catch (Exception ex)
                {
                    // One bad contract must not stop the sweep for the rest.
                    _logger.LogError(ex, "Failed to purge contract {Number}.", contract.ContractNumber);
                }
            }

            if (purged > 0)
                _logger.LogInformation("Permanently deleted {Count} contract(s) past the {Months}-month retention window.",
                    purged, RetentionMonths);

            if (preserved > 0)
                _logger.LogInformation(
                    "Kept {Count} archived contract(s) that carry signatures, invoices or amendments.",
                    preserved);

            return purged;
        }
    }
}
