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
        /// The account holder's name, as one string. This is the PERSON, and it is used to seed
        /// the primary billing CONTACT - never the legal entity name.
        /// </summary>
        public static string ResolvePersonName(User user) =>
            string.Join(" ", new[] { user.FirstName, user.LastName }
                    .Where(s => !string.IsNullOrWhiteSpace(s)))
                .Trim();

        /// <summary>
        /// Builds the row for a newly linked account.
        ///
        /// <b>LEGAL ENTITY NAME IS DELIBERATELY LEFT EMPTY (2026-09).</b> It used to be seeded with
        /// the account holder's name, which put "nodar alania" in the Legal entity name field of
        /// every auto-created commercial client while the Primary billing contact - the field that
        /// person's name actually belongs in - sat empty. A COMPANY IS NOT ITS OWNER: "Nodar Alania"
        /// is a person and "Chick Tastic LLC" is the counterparty a contract is signed with, and
        /// the contract prints the legal name as a statement about a registered entity. A blank is
        /// a gap staff fill in from the client's own paperwork; a person's name there is a false
        /// document that reads as correct.
        ///
        /// The account holder instead seeds the primary billing CONTACT - see
        /// <see cref="BuildBillingContactFromAccount"/>.
        ///
        /// <b>EntityType is left empty for the same reason.</b> It is rendered as a legal
        /// characterisation ("a limited liability company"), so defaulting it to the commonest
        /// value would print something nobody has checked.
        /// </summary>
        public static ContractClient BuildFromAccount(User user, Apartment? primaryAddress)
        {
            var now = DateTime.UtcNow;

            return new ContractClient
            {
                LegalEntityName = string.Empty,
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
        /// The primary billing contact seeded from the account holder.
        ///
        /// This is where the website user's identity actually belongs: first name, last name, their
        /// email as both the contact address and the billing/notice address, and their phone when
        /// they have one. It is what "Primary billing contact remains mostly empty" was missing.
        ///
        /// <b>TITLE IS LEFT EMPTY ON PURPOSE.</b> Ticking a business flag says nothing about
        /// whether this person is the Owner, a manager or an office administrator, and the title is
        /// printed on a signature block underneath a name. Assuming "Owner" would put an
        /// unverified claim of authority into a legal document.
        ///
        /// The role is <c>ClientSigner</c> because that is the only client-side role the model has
        /// and it is what the existing billing-contact resolver looks for; the person who signs and
        /// the person who is billed are frequently the same on a small commercial account, and
        /// staff can add a separate contact when they are not.
        /// </summary>
        public static ContractContact BuildBillingContactFromAccount(User user, int contractClientId)
        {
            var now = DateTime.UtcNow;

            return new ContractContact
            {
                ContractClientId = contractClientId,
                FirstName = user.FirstName?.Trim() ?? string.Empty,
                LastName = user.LastName?.Trim() ?? string.Empty,
                Title = null,
                Email = ResolveNoticeEmail(user),
                Phone = user.Phone,
                Role = ContractContactRole.ClientSigner,
                IsPrimaryBillingContact = true,
                // The account this person signs in as, which is what enables them to sign a
                // contract and read their invoices from the customer portal without a token link.
                UserId = user.Id,
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
