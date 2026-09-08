using System.Security.Cryptography;

namespace DreamCleaningBackend.Helpers.Commercial
{
    /// <summary>
    /// Public reference numbers for the two commercial documents: <c>DCC-YYYY-XXXXXXXX</c> for a
    /// contract and <c>DCI-YYYY-XXXXXXXX</c> for an invoice.
    ///
    /// The random half is drawn from <see cref="RandomNumberGenerator"/>, never from
    /// <c>Random</c>. A sequential public id (the old <c>DC-2026-0001</c>) leaks the business's
    /// volume to anyone holding one document, and lets a client guess their neighbour's reference
    /// by subtracting one. Eight cryptographic digits is 90 million values, so a guess is not
    /// worth attempting even before the token on the public page is considered.
    ///
    /// The two prefixes are separate on purpose. A contract and an invoice used to be able to
    /// collide conceptually at the same number, and "DC-2026-0007" was ambiguous the moment both
    /// existed. DCC and DCI never are.
    ///
    /// Uniqueness is enforced by the unique index on each column - this class only picks a
    /// candidate. The caller retries on a collision; see <c>InvoiceNumberService</c>.
    /// </summary>
    public static class ReferenceNumberGenerator
    {
        public const string ContractPrefix = "DCC";
        public const string InvoicePrefix = "DCI";

        /// <summary>The lowest 8-digit value, so the number never renders with a leading zero.</summary>
        private const int Min = 10_000_000;
        private const int Max = 99_999_999;

        public static string NewContractNumber(int year) => Build(ContractPrefix, year);

        public static string NewInvoiceNumber(int year) => Build(InvoicePrefix, year);

        /// <summary><c>PREFIX-YYYY-XXXXXXXX</c> with a cryptographically random 8-digit tail.</summary>
        public static string Build(string prefix, int year) =>
            $"{prefix}-{year}-{SecureEightDigits()}";

        /// <summary>
        /// A uniformly distributed integer in [10000000, 99999999].
        /// <see cref="RandomNumberGenerator.GetInt32(int,int)"/> is rejection-sampled internally,
        /// so there is no modulo bias to correct for here.
        /// </summary>
        public static int SecureEightDigits() => RandomNumberGenerator.GetInt32(Min, Max + 1);

        /// <summary>
        /// True for a reference this generator would produce. Used to tell a new-format number
        /// from a legacy <c>DC-YYYY-NNNN</c> one without re-parsing the year everywhere.
        /// </summary>
        public static bool IsNewFormat(string? number, string prefix)
        {
            if (string.IsNullOrWhiteSpace(number)) return false;
            var parts = number.Split('-');
            return parts.Length == 3
                && parts[0] == prefix
                && parts[1].Length == 4 && parts[1].All(char.IsDigit)
                && parts[2].Length == 8 && parts[2].All(char.IsDigit);
        }
    }
}
