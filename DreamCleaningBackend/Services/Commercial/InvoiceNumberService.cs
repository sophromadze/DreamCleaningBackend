using System.Security.Cryptography;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Helpers.Commercial;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services.Commercial
{
    /// <summary>
    /// Allocates the public reference numbers for both commercial documents, and the opaque token
    /// behind the public invoice page.
    ///
    /// GENERATED SERVER-SIDE ONLY. No endpoint accepts an invoice or contract number from a
    /// caller, and neither DTO has a field for one - the browser displays what it is given.
    ///
    /// The unique index on each column is the real guard against a collision; this class picks a
    /// candidate and pre-checks it, and the SAVE is retried by the caller if two requests happen
    /// to land on the same number between the check and the insert. With 90 million values per
    /// prefix per year that retry is expected to be dead code, which is exactly why it must exist:
    /// nothing exercising it means nothing would notice it missing.
    /// </summary>
    public class InvoiceNumberService
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<InvoiceNumberService> _logger;

        /// <summary>Generous: each attempt is one indexed existence check on a random value.</summary>
        private const int MaxAttempts = 10;

        public InvoiceNumberService(ApplicationDbContext context, ILogger<InvoiceNumberService> logger)
        {
            _context = context;
            _logger = logger;
        }

        /// <summary>A free DCI-YYYY-XXXXXXXX.</summary>
        public Task<string> NextInvoiceNumberAsync(CancellationToken ct = default) =>
            NextAsync(
                ReferenceNumberGenerator.InvoicePrefix,
                candidate => _context.CommercialInvoices.AnyAsync(i => i.InvoiceNumber == candidate, ct),
                ct);

        /// <summary>A free DCC-YYYY-XXXXXXXX.</summary>
        public Task<string> NextContractNumberAsync(CancellationToken ct = default) =>
            NextAsync(
                ReferenceNumberGenerator.ContractPrefix,
                candidate => _context.Contracts.AnyAsync(c => c.ContractNumber == candidate, ct),
                ct);

        private async Task<string> NextAsync(
            string prefix, Func<string, Task<bool>> exists, CancellationToken ct)
        {
            var year = DateTime.UtcNow.Year;

            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                var candidate = ReferenceNumberGenerator.Build(prefix, year);

                if (!await exists(candidate))
                    return candidate;

                _logger.LogWarning(
                    "Reference number collision on {Candidate} (attempt {Attempt}); regenerating.",
                    candidate, attempt + 1);
            }

            // Ten consecutive collisions against 90 million values is not chance - it means the
            // RNG or the uniqueness check is broken. Failing loudly is correct: silently issuing a
            // duplicate reference would corrupt payment reconciliation, where the number IS the
            // matching key.
            throw new InvalidOperationException(
                $"Could not allocate a unique {prefix} reference after {MaxAttempts} attempts.");
        }

        /// <summary>
        /// The public-page token: 24 random bytes as 48 hex chars, the same shape and entropy as
        /// the contract review token and the customer payment link token.
        /// </summary>
        public static string NewPublicToken() =>
            Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

        /// <summary>
        /// Constant-time token comparison, so a caller cannot narrow the token down by timing a
        /// series of guesses. Same helper shape as <c>PaymentLinkHelper.TokenMatches</c>.
        /// </summary>
        public static bool TokenMatches(string? stored, string? supplied)
        {
            if (string.IsNullOrEmpty(stored) || string.IsNullOrEmpty(supplied)) return false;
            var a = System.Text.Encoding.UTF8.GetBytes(stored);
            var b = System.Text.Encoding.UTF8.GetBytes(supplied);
            return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
        }
    }
}
