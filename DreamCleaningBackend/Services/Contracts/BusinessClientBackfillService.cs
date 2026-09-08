using DreamCleaningBackend.Data;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services.Contracts
{
    /// <summary>
    /// THE BACKFILL: every account already flagged as a business gets its commercial client, once,
    /// on the first startup after this ships.
    ///
    /// Why a hosted service rather than SQL inside the EF migration: the mapping reads real model
    /// fields — the account's name, its sendable email (which means going through
    /// <c>NoEmailHelper</c> so a no-email placeholder never lands in a billing field), and its
    /// oldest active apartment for the address. Hand-written SQL would have to re-implement all
    /// three and would then be the copy that drifts. This runs the same
    /// <see cref="BusinessClientMapper"/> the live write path uses, so a backfilled client and a
    /// newly flagged one are identical by construction.
    ///
    /// SAFETY PROPERTIES, all of which matter for something that runs unattended at boot:
    ///
    ///  - <b>Idempotent.</b> It asks for business accounts with NO linked client, so the second
    ///    run finds nothing. Re-running it is free.
    ///  - <b>Additive only.</b> It inserts clients. It never edits, deactivates, reactivates or
    ///    deletes one, never touches a User, and never touches a contract or an invoice.
    ///  - <b>Does not resurrect.</b> A client deactivated through Delete had its account's business
    ///    flag cleared in the same transaction, so that account is not in this set — the deletion
    ///    survives every restart. That is the whole reason Delete clears the flag.
    ///  - <b>No identity matching.</b> An existing standalone client is never adopted because its
    ///    email or name resembles an account's. A link exists only where <c>SourceUserId</c> was
    ///    set deliberately.
    ///  - <b>Never fatal.</b> A failure is logged and swallowed: the app must still start, and the
    ///    next boot tries again. Nothing downstream depends on the backfill having run — a missing
    ///    client just means that customer is not listed yet.
    ///
    /// Runs AFTER <c>Database.MigrateAsync()</c> (which Program.cs awaits before the host starts),
    /// so the unique index on SourceUserId is already in place and a concurrent double-start
    /// cannot produce two clients for one account — the second insert is refused by the database.
    /// </summary>
    public class BusinessClientBackfillService : IHostedService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<BusinessClientBackfillService> _logger;

        public BusinessClientBackfillService(
            IServiceProvider serviceProvider,
            ILogger<BusinessClientBackfillService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();

                var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                if (!await context.Database.CanConnectAsync(cancellationToken))
                {
                    _logger.LogWarning(
                        "Skipping the commercial client backfill: the database is not reachable yet.");
                    return;
                }

                var service = scope.ServiceProvider.GetRequiredService<BusinessClientService>();
                var created = await service.BackfillAsync(cancellationToken);

                if (created == 0)
                    _logger.LogInformation("Commercial client backfill: nothing to do.");
            }
            catch (Exception ex)
            {
                // Logged, not rethrown. A backfill that cannot run is a listing gap an admin can
                // fix by hand; a backfill that stops the API from starting is an outage.
                _logger.LogError(ex, "The commercial client backfill failed. It will retry on the next start.");
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
