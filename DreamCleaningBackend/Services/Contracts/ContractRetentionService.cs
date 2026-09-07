using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models.Contracts;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services.Contracts
{
    /// <summary>
    /// Permanently removes contracts that have been soft-deleted for longer than the retention
    /// window (default six months, <c>ContractRetention:HiddenMonths</c>).
    ///
    /// Pattern follows the other background workers here — hourly loop, one scope per cycle,
    /// backoff on repeated failures. It only acts once a day, because a retention sweep has no
    /// reason to run more often and a daily cadence keeps the log readable.
    ///
    /// The order of operations matters: the <see cref="ContractDeletionLog"/> row is written and
    /// committed BEFORE the contract is removed. The contract's own audit trail cascades away with
    /// it, so a record written afterwards could be lost to a failure mid-delete, and the one
    /// question anyone asks later — "was DC-2026-000X deleted, and when" — would have no answer.
    /// </summary>
    public class ContractRetentionService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<ContractRetentionService> _logger;
        private readonly IConfiguration _configuration;

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
        public async Task<int> SweepAsync(CancellationToken cancellationToken = default)
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var storage = scope.ServiceProvider.GetRequiredService<ContractStorage>();

            var cutoff = DateTime.UtcNow.AddMonths(-RetentionMonths);

            var expired = await context.Contracts
                .Include(c => c.ContractClient)
                .Include(c => c.HiddenByUser)
                .Where(c => c.IsHidden && c.HiddenAt != null && c.HiddenAt <= cutoff)
                .ToListAsync(cancellationToken);

            if (expired.Count == 0) return 0;

            var purged = 0;
            foreach (var contract in expired)
            {
                try
                {
                    await PurgeAsync(context, storage, contract, cancellationToken);
                    purged++;
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

            return purged;
        }

        private async Task PurgeAsync(
            ApplicationDbContext context, ContractStorage storage,
            Contract contract, CancellationToken cancellationToken)
        {
            var versionIds = await context.ContractVersions
                .Where(v => v.ContractId == contract.Id)
                .Select(v => v.Id)
                .ToListAsync(cancellationToken);

            var files = await context.ContractFiles
                .Where(f => versionIds.Contains(f.ContractVersionId))
                .ToListAsync(cancellationToken);

            var signatureCount = await context.ContractSignatures
                .CountAsync(s => s.ContractSigner != null
                                 && versionIds.Contains(s.ContractSigner.ContractVersionId),
                            cancellationToken);

            // Written and committed FIRST — see the class summary.
            context.ContractDeletionLogs.Add(new ContractDeletionLog
            {
                ContractNumber = contract.ContractNumber,
                ContractId = contract.Id,
                ClientLegalName = contract.ContractClient?.LegalEntityName,
                StatusAtDeletion = contract.Status.ToString(),
                HiddenAt = contract.HiddenAt,
                HiddenBy = contract.HiddenByUser == null
                    ? null
                    : $"{contract.HiddenByUser.FirstName} {contract.HiddenByUser.LastName}".Trim(),
                DeletedAt = DateTime.UtcNow,
                VersionCount = versionIds.Count,
                SignatureCount = signatureCount,
                FileCount = files.Count,
                DeletedBy = "Retention job"
            });
            await context.SaveChangesAsync(cancellationToken);

            // Then the documents on disk. A file that has already gone is not an error — the row
            // is what we are authoritative about.
            foreach (var file in files)
            {
                try
                {
                    var path = storage.Resolve(file.FilePath);
                    if (File.Exists(path)) File.Delete(path);
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "Could not delete contract file {Path}.", file.FilePath);
                }
            }

            // Finally the row. Versions, signers, signatures, files and the contract's own audit
            // rows all cascade from here (see the delete behaviour in ApplicationDbContext); the
            // deletion log deliberately does not, because it has no foreign key to follow.
            context.Contracts.Remove(contract);
            await context.SaveChangesAsync(cancellationToken);
        }
    }
}
