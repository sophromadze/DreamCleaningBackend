using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Models.Contracts;

namespace DreamCleaningBackend.Helpers.Contracts
{
    /// <summary>
    /// SEEDING A COMMERCIAL CLIENT FROM A BUSINESS ACCOUNT — and, just as importantly, the rule for
    /// what keeps flowing between the two afterwards.
    ///
    /// Pure and context-free so the mapping can be asserted without a database. Everything here is
    /// read off fields that actually exist on <see cref="User"/> and <see cref="Apartment"/>;
    /// nothing is invented, and nothing is guessed from an email address.
    ///
    /// ══ THE SYNCHRONISATION RULE (one rule, stated once) ══
    ///
    /// <b>The account seeds the client ONCE, at link time. After that the ContractClient is the
    /// authority for everything commercial, and the only field written back to the account is the
    /// PHONE NUMBER.</b>
    ///
    /// The reason the list is that short is that the two records describe different things, and
    /// treating them as copies of each other is the actual mistake:
    ///
    ///  - <b>Legal entity name vs. first/last name.</b> A company is not its owner. "Casey Client"
    ///    is a person; "Chick Tastic LLC" is the counterparty a contract is signed with. Writing
    ///    either into the other is wrong in both directions, so the account's name only ever SEEDS
    ///    the legal name, as a starting point staff correct.
    ///  - <b>Notice email vs. account email.</b> <c>User.Email</c> is a LOGIN IDENTITY with
    ///    verification state, a uniqueness constraint and a whole confirm-by-token change flow
    ///    behind it (<c>PendingEmail</c> / <c>EmailChangeToken</c>). A billing screen must not
    ///    silently reassign it — and a company that is invoiced at accounts@ while its owner logs
    ///    in as casey@ is normal, not drift.
    ///  - <b>Principal address vs. apartment.</b> The apartment is somewhere we CLEAN; the
    ///    principal address is where the company is REGISTERED and where legal notice is served.
    ///    The reference contract has different values for the two on purpose.
    ///  - <b>Phone.</b> The one genuinely identical fact about the same party, with no machinery
    ///    behind it. It is written through, transactionally — see <c>BusinessClientService</c>.
    ///
    /// Where the two legitimately differ, the admin UI SHOWS the account's value beside the
    /// client's rather than reconciling them, so a divergence is visible instead of silent.
    /// </summary>
    public static class BusinessClientMapper
    {
        /// <summary>
        /// The account's own address, or null. The OLDEST ACTIVE apartment: it is the one they
        /// registered with, and a customer with several has no "primary" flag to choose from —
        /// the same rule <c>GetBusinessCustomers</c> already uses for the contract form.
        /// </summary>
        public static Apartment? ResolvePrimaryAddress(IEnumerable<Apartment>? apartments) =>
            apartments?.Where(a => a.IsActive).OrderBy(a => a.Id).FirstOrDefault();

        /// <summary>
        /// The account's sendable address, or null for a no-email cash customer. The placeholder
        /// domain must never reach a billing field — an invoice addressed to it silently fails.
        /// </summary>
        public static string? ResolveNoticeEmail(User user) =>
            string.IsNullOrWhiteSpace(user.Email) || NoEmailHelper.IsPlaceholder(user.Email)
                ? null
                : user.Email.Trim();

        /// <summary>
        /// The seed for the legal entity name. The account holder's name is a STARTING POINT that
        /// staff correct to the registered company name, not a claim about the entity — which is
        /// exactly why the client is editable from Commercial → Clients.
        /// </summary>
        public static string ResolveLegalEntityName(User user) =>
            string.Join(" ", new[] { user.FirstName, user.LastName }
                    .Where(s => !string.IsNullOrWhiteSpace(s)))
                .Trim();

        /// <summary>
        /// Builds the row for a newly linked account.
        ///
        /// <b>EntityType is deliberately left EMPTY.</b> It is rendered into the contract as a
        /// legal characterisation of the counterparty ("Chick Tastic LLC, a limited liability
        /// company"), so defaulting it to the commonest value would print a statement about a
        /// company nobody has checked. Blank is a gap staff fill in; a guess is a false document.
        /// </summary>
        public static ContractClient BuildFromAccount(User user, Apartment? primaryAddress)
        {
            var now = DateTime.UtcNow;

            return new ContractClient
            {
                LegalEntityName = ResolveLegalEntityName(user),
                EntityType = string.Empty,
                PrincipalAddress = primaryAddress?.Address?.Trim() ?? string.Empty,
                City = primaryAddress?.City?.Trim() ?? string.Empty,
                State = primaryAddress?.State?.Trim() ?? string.Empty,
                Zip = primaryAddress?.PostalCode?.Trim() ?? string.Empty,
                NoticeEmail = ResolveNoticeEmail(user),
                Phone = user.Phone,
                SourceUserId = user.Id,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            };
        }

        /// <summary>
        /// True when a phone edit on the client should be written through to the account.
        ///
        /// Only when the account has nothing of its own, or still holds exactly what the client
        /// held before the edit. If the two had already diverged, somebody set them deliberately
        /// and a billing screen is not the place to overrule that — "do not overwrite unrelated
        /// profile data" cuts both ways.
        /// </summary>
        public static bool ShouldWritePhoneThrough(string? accountPhone, string? clientPhoneBefore)
        {
            if (string.IsNullOrWhiteSpace(accountPhone)) return true;

            return string.Equals(
                PhoneHelper.NormalizeToDigits(accountPhone),
                PhoneHelper.NormalizeToDigits(clientPhoneBefore),
                StringComparison.Ordinal);
        }
    }
}
