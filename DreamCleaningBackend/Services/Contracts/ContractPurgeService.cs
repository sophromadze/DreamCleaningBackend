using DreamCleaningBackend.Data;
using DreamCleaningBackend.Helpers.Contracts;
using DreamCleaningBackend.Models.Contracts;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services.Contracts
{
    /// <summary>Thrown when a permanent delete is refused. Surfaced as a 400 with the reason.</summary>
    public class ContractPurgeRefusedException : Exception
    {
        public ContractPurgeRefusedException(string message) : base(message) { }
    }

    /// <summary>
    /// THE ONE PLACE A CONTRACT IS PERMANENTLY DESTROYED.
    ///
    /// Two callers, and they must not be able to behave differently: the admin's "Full delete"
    /// action, and <see cref="ContractRetentionService"/>'s sweep over long-archived contracts.
    /// Both go through <see cref="PurgeAsync"/>, and both are gated by
    /// <see cref="ContractHardDeletePolicy"/> - so an executed agreement is refused whether a
    /// person pressed the button or a timer came round to it. The sweep used to have no such gate.
    ///
    /// ORDER OF OPERATIONS, and each step is load-bearing:
    ///
    /// 1. The <see cref="ContractDeletionLog"/> row and the contract's removal happen in ONE
    ///    transaction. The contract's own audit trail cascades away with it, so the only record
    ///    that survives is that log row - and a log written in a separate transaction could be
    ///    committed for a delete that then failed, or lost for a delete that succeeded. Atomically
    ///    together is the only arrangement where "was DCC-2026-0001 deleted" has a truthful answer.
    ///
    /// 2. The PDFs come off disk AFTER that transaction commits. A file deleted before a rollback
    ///    is gone from a contract that still exists, which is unrecoverable; a file left behind
    ///    after a successful delete is inert. So the failure that costs nothing is the one chosen.
    ///
    /// The shared client, the service location, the contractor profile, the template and the
    /// signing contacts are all Restrict-mapped and are deliberately untouched: they exist
    /// independently of any one contract and are shared with others.
    /// </summary>
    public class ContractPurgeService
    {
        private readonly ApplicationDbContext _context;
        private readonly ContractStorage _storage;
        private readonly ILogger<ContractPurgeService> _logger;

        public ContractPurgeService(
            ApplicationDbContext context,
            ContractStorage storage,
            ILogger<ContractPurgeService> logger)
        {
            _context = context;
            _storage = storage;
            _logger = logger;
        }

        /// <summary>
        /// Everything the policy needs, counted in the database. Separate from the purge itself
        /// because the detail endpoint asks the same question to decide whether to offer the
        /// button at all.
        /// </summary>
        public async Task<ContractDeletionFacts> GatherFactsAsync(
            Contract contract, CancellationToken cancellationToken = default)
        {
            var signatureCount = await _context.ContractSignatures
                .CountAsync(s => s.ContractSigner != null
                                 && s.ContractSigner.ContractVersion != null
                                 && s.ContractSigner.ContractVersion.ContractId == contract.Id,
                            cancellationToken);

            var invoiceCount = await _context.CommercialInvoices
                .CountAsync(i => i.ContractId == contract.Id, cancellationToken);

            var templateCount = await _context.CommercialRecurringInvoiceTemplates
                .CountAsync(t => t.ContractId == contract.Id, cancellationToken);

            var derivedCount = await _context.Contracts
                .CountAsync(c => c.DuplicatedFromContractId == contract.Id, cancellationToken);

            return new ContractDeletionFacts
            {
                Status = contract.Status,
                SignatureCount = signatureCount,
                LinkedInvoiceCount = invoiceCount,
                LinkedRecurringTemplateCount = templateCount,
                DerivedContractCount = derivedCount
            };
        }

        /// <summary>The blocker sentence for this contract, or null when it may be destroyed.</summary>
        public async Task<string?> DescribeBlockerAsync(
            Contract contract, CancellationToken cancellationToken = default) =>
            ContractHardDeletePolicy.DescribeBlocker(
                await GatherFactsAsync(contract, cancellationToken));

        /// <summary>
        /// Destroys the contract and everything owned by it.
        ///
        /// <paramref name="deletedBy"/> names the actor on the surviving log row - an admin's name
        /// for the manual action, "Retention job" for the sweep. The column has always existed for
        /// exactly this distinction.
        /// </summary>
        public async Task PurgeAsync(
            Contract contract, string deletedBy, CancellationToken cancellationToken = default)
        {
            var blocker = await DescribeBlockerAsync(contract, cancellationToken);
            if (blocker != null) throw new ContractPurgeRefusedException(blocker);

            var versionIds = await _context.ContractVersions
                .Where(v => v.ContractId == contract.Id)
                .Select(v => v.Id)
                .ToListAsync(cancellationToken);

            var files = await _context.ContractFiles
                .Where(f => versionIds.Contains(f.ContractVersionId))
                .ToListAsync(cancellationToken);

            // Zero by definition once the policy has passed, but recorded rather than assumed:
            // the log row is the only thing that outlives this and it should state what it saw.
            var signatureCount = await _context.ContractSignatures
                .CountAsync(s => s.ContractSigner != null
                                 && versionIds.Contains(s.ContractSigner.ContractVersionId),
                            cancellationToken);

            var clientName = contract.ContractClient?.LegalEntityName
                ?? await _context.ContractClients
                    .Where(c => c.Id == contract.ContractClientId)
                    .Select(c => c.LegalEntityName)
                    .FirstOrDefaultAsync(cancellationToken);

            var hiddenBy = contract.HiddenByUser == null
                ? null
                : $"{contract.HiddenByUser.FirstName} {contract.HiddenByUser.LastName}".Trim();

            var log = new ContractDeletionLog
            {
                ContractNumber = contract.ContractNumber,
                ContractId = contract.Id,
                ClientLegalName = clientName,
                StatusAtDeletion = contract.Status.ToString(),
                HiddenAt = contract.HiddenAt,
                HiddenBy = hiddenBy,
                DeletedAt = DateTime.UtcNow,
                VersionCount = versionIds.Count,
                SignatureCount = signatureCount,
                FileCount = files.Count,
                DeletedBy = deletedBy
            };

            // Step 1 - the log and the removal, atomically. See the class summary.
            await using (var transaction = await _context.Database.BeginTransactionAsync(cancellationToken))
            {
                _context.ContractDeletionLogs.Add(log);

                // Versions, signers, signatures, files and the contract's own audit rows all
                // cascade from this one Remove (see ApplicationDbContext); the deletion log
                // deliberately has no foreign key to follow, so it stays.
                _context.Contracts.Remove(contract);

                await _context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }

            // Step 2 - the documents on disk, only now that the rows are certainly gone. A file
            // that has already disappeared is not an error; the row is what we are authoritative
            // about.
            foreach (var file in files)
            {
                try
                {
                    var path = _storage.Resolve(file.FilePath);
                    if (File.Exists(path)) File.Delete(path);
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "Could not delete contract file {Path}.", file.FilePath);
                }
                catch (UnauthorizedAccessException ex)
                {
                    _logger.LogWarning(ex, "Could not delete contract file {Path}.", file.FilePath);
                }
            }

            _logger.LogInformation(
                "Contract {Number} permanently deleted by {Actor} ({Versions} version(s), {Files} file(s)).",
                log.ContractNumber, deletedBy, log.VersionCount, log.FileCount);
        }
    }
}
