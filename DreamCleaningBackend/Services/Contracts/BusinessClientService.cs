using DreamCleaningBackend.Data;
using DreamCleaningBackend.Helpers.Contracts;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Models.Contracts;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services.Contracts
{
    /// <summary>
    /// THE ONE OWNER OF THE User ↔ ContractClient LINK (2026-09).
    ///
    /// A customer account flagged as a business IS a commercial client, and staff should not have
    /// to say so twice. Flipping <c>User.IsBusiness</c> is therefore the only gesture needed:
    /// this service makes the matching <c>ContractClient</c> exist, come back, or go quiet.
    ///
    /// <b>ContractClient is NOT replaced by User and nothing is re-architected around it.</b> It
    /// remains the commercial legal/billing entity that contracts, invoices, billing contacts and
    /// service locations hang off — most of which a User has no notion of. A standalone client
    /// with <c>SourceUserId = null</c> stays a first-class citizen, for businesses that have no
    /// website account at all.
    ///
    /// ══ THE STATE MACHINE ══
    ///
    /// Exactly one linked row can ever exist per account (unique index, see
    /// <c>ApplicationDbContext</c>), so every transition is a move on that ONE row rather than an
    /// insert-or-not decision. This is what makes the whole thing idempotent:
    ///
    /// <code>
    ///   IsBusiness false → true    no row      → create, active
    ///                              row, off    → REACTIVATE the same row (never a duplicate)
    ///                              row, on     → no-op
    ///
    ///   IsBusiness true → false    row, on     → deactivate  (contracts + invoices untouched)
    ///                              row, off    → no-op
    ///                              no row      → no-op
    ///
    ///   Delete on Commercial → Clients, LINKED client
    ///                              → IsBusiness = false AND client deactivated, one transaction
    /// </code>
    ///
    /// That last one is the important one. Soft-deleting a linked client without clearing the flag
    /// would leave the account still flagged as a business, and the next sync would put the client
    /// straight back — the admin would delete it and watch it reappear. Removing the designation
    /// is what makes the deletion STICK, which is why the UI says so before it happens.
    ///
    /// ══ WHAT IS NEVER DONE ══
    ///
    /// No hard deletes, ever — of the client, the user, a contract, an invoice or a payment.
    /// Deactivating a client hides it from the pickers and nothing else: historical contracts and
    /// invoices resolve it exactly as before, because they join on the id, not on IsActive.
    ///
    /// And no identity matching: a client is linked to an account because somebody set
    /// <c>SourceUserId</c>, never because an email address or a name happened to line up. An
    /// existing standalone client is left standalone.
    /// </summary>
    public class BusinessClientService
    {
        private readonly ApplicationDbContext _context;
        private readonly IAuditService _audit;
        private readonly ILogger<BusinessClientService> _logger;

        public BusinessClientService(
            ApplicationDbContext context,
            IAuditService audit,
            ILogger<BusinessClientService> logger)
        {
            _context = context;
            _audit = audit;
            _logger = logger;
        }

        // ── Audit actions. Read by the Audits tab; the strings are load-bearing. ──
        public const string ActionAutoCreated = "BusinessClientAutoCreated";
        public const string ActionReactivated = "CommercialClientReactivated";
        public const string ActionDeactivated = "CommercialClientDeactivated";
        public const string ActionBusinessLinkRemoved = "BusinessDesignationRemoved";

        /// <summary>What a transition actually did, so the caller can word its response honestly.</summary>
        public enum LinkOutcome { Unchanged, Created, Reactivated, Deactivated }

        /// <summary>
        /// Makes the linked client exist and be active. Idempotent: safe to call on every save of
        /// the business flag, on startup backfill, and twice in a row.
        ///
        /// Does NOT call SaveChanges — the caller owns the transaction, so the flag and the client
        /// move together or not at all.
        /// </summary>
        public async Task<(LinkOutcome Outcome, ContractClient Client)> EnsureLinkedClientAsync(User user)
        {
            var existing = await _context.ContractClients
                .FirstOrDefaultAsync(c => c.SourceUserId == user.Id);

            if (existing != null)
            {
                // A reactivated client may predate primary billing contacts, so top one up. This
                // NEVER touches an existing contact - see EnsureBillingContactAsync.
                await EnsureBillingContactAsync(user, existing);

                if (existing.IsActive) return (LinkOutcome.Unchanged, existing);

                // The SAME row comes back, keeping its id, its contracts, its invoices, its
                // service locations and any name staff had corrected. Creating a second one here
                // would orphan all of it - and the unique index would refuse anyway.
                existing.IsActive = true;
                existing.UpdatedAt = DateTime.UtcNow;
                return (LinkOutcome.Reactivated, existing);
            }

            var apartments = await _context.Apartments
                .Where(a => a.UserId == user.Id)
                .ToListAsync();

            var client = BusinessClientMapper.BuildFromAccount(
                user, BusinessClientMapper.ResolvePrimaryAddress(apartments));

            _context.ContractClients.Add(client);

            // Saved here rather than left to the caller because the contact needs the client's id,
            // and the caller's transaction still wraps both.
            await _context.SaveChangesAsync();
            await EnsureBillingContactAsync(user, client);

            return (LinkOutcome.Created, client);
        }

        /// <summary>
        /// Gives a linked client a primary billing contact built from the account holder, when it
        /// has no contact at all.
        ///
        /// <b>It never edits or replaces an existing contact.</b> The whole point of the fix this
        /// belongs to is that the account seeds the commercial record ONCE; a client whose contact
        /// an admin has since corrected - a different accounts-payable address, a proper title -
        /// must not have that overwritten because somebody re-saved the business flag. So the test
        /// is "are there any contacts", not "does this one match the account".
        ///
        /// Does not save - the caller owns the transaction.
        /// </summary>
        private async Task EnsureBillingContactAsync(User user, ContractClient client)
        {
            if (client.Id <= 0) return;

            var hasContact = await _context.ContractContacts
                .AnyAsync(c => c.ContractClientId == client.Id);

            if (hasContact) return;

            _context.ContractContacts.Add(
                BusinessClientMapper.BuildBillingContactFromAccount(user, client.Id));
        }

        /// <summary>
        /// Takes the linked client out of circulation. Never deletes it, and never touches a
        /// contract or an invoice. Does not call SaveChanges.
        /// </summary>
        public async Task<LinkOutcome> DeactivateLinkedClientAsync(int userId)
        {
            var existing = await _context.ContractClients
                .FirstOrDefaultAsync(c => c.SourceUserId == userId);

            if (existing == null || !existing.IsActive) return LinkOutcome.Unchanged;

            existing.IsActive = false;
            existing.UpdatedAt = DateTime.UtcNow;
            return LinkOutcome.Deactivated;
        }

        /// <summary>
        /// Applies a business-flag change and its client side effect together, then saves and
        /// audits. This is the whole of requirement "do it on the write path where the flag is
        /// saved" — there is no synchronisation anywhere else, and nothing writes on a read.
        /// </summary>
        public async Task<LinkOutcome> ApplyBusinessFlagAsync(User user, bool isBusiness, int actingAdminId)
        {
            user.IsBusiness = isBusiness;
            user.UpdatedAt = DateTime.UtcNow;

            LinkOutcome outcome;
            ContractClient? client = null;

            if (isBusiness)
            {
                (outcome, client) = await EnsureLinkedClientAsync(user);
            }
            else
            {
                outcome = await DeactivateLinkedClientAsync(user.Id);
                client = await _context.ContractClients
                    .FirstOrDefaultAsync(c => c.SourceUserId == user.Id);
            }

            await _context.SaveChangesAsync();

            if (client != null && outcome != LinkOutcome.Unchanged)
                await LogAsync(client, ActionFor(outcome), user, actingAdminId);

            return outcome;
        }

        /// <summary>
        /// The Delete action on Commercial → Clients, for a client that is linked to an account.
        ///
        /// Both halves in ONE transaction, because either alone is broken: clearing the flag but
        /// leaving the client visible hides nothing, and deactivating the client but leaving the
        /// flag set means the next flag save resurrects it.
        /// </summary>
        public async Task RemoveBusinessClientAsync(ContractClient client, int actingAdminId)
        {
            if (client.SourceUserId == null)
                throw new ContractWorkflowException(
                    "That client is not linked to a customer account.");

            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == client.SourceUserId.Value);

            client.IsActive = false;
            client.UpdatedAt = DateTime.UtcNow;

            // The User row itself is NEVER deleted - they keep their account, their orders and
            // their history. Only the business designation goes.
            if (user != null)
            {
                user.IsBusiness = false;
                user.UpdatedAt = DateTime.UtcNow;
            }

            await _context.SaveChangesAsync();

            await LogAsync(client, ActionBusinessLinkRemoved, user, actingAdminId);
        }

        /// <summary>
        /// Startup backfill: every active business customer that has no linked client gets one.
        ///
        /// Idempotent by construction — it asks for accounts with no linked row, so a second run
        /// finds nothing. Deliberately does NOT reactivate a deactivated link: staff who deleted
        /// that client also cleared the flag, so it will not be in this set at all, and a client
        /// deactivated some other way is a decision to respect rather than undo on next boot.
        /// </summary>
        public async Task<int> BackfillAsync(CancellationToken cancellationToken = default)
        {
            var linkedUserIds = await _context.ContractClients
                .Where(c => c.SourceUserId != null)
                .Select(c => c.SourceUserId!.Value)
                .ToListAsync(cancellationToken);

            var pending = await _context.Users
                .Where(u => u.IsBusiness
                            && u.IsActive
                            && u.Role == UserRole.Customer
                            && !linkedUserIds.Contains(u.Id))
                .ToListAsync(cancellationToken);

            if (pending.Count == 0) return 0;

            var apartments = await _context.Apartments
                .Where(a => pending.Select(u => u.Id).Contains(a.UserId))
                .ToListAsync(cancellationToken);

            var created = new List<(User User, ContractClient Client)>();

            foreach (var user in pending)
            {
                var address = BusinessClientMapper.ResolvePrimaryAddress(
                    apartments.Where(a => a.UserId == user.Id));

                var client = BusinessClientMapper.BuildFromAccount(user, address);
                _context.ContractClients.Add(client);
                created.Add((user, client));
            }

            // Two saves: the contacts need the client ids the first one allocates. A backfilled
            // client therefore comes out identical to one created by the live path, which is the
            // whole reason this runs the shared mapper rather than its own SQL.
            await _context.SaveChangesAsync(cancellationToken);

            foreach (var (user, client) in created)
                _context.ContractContacts.Add(
                    BusinessClientMapper.BuildBillingContactFromAccount(user, client.Id));

            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Backfilled {Count} commercial client(s) from business-flagged accounts.", pending.Count);

            return pending.Count;
        }

        private static string ActionFor(LinkOutcome outcome) => outcome switch
        {
            LinkOutcome.Created => ActionAutoCreated,
            LinkOutcome.Reactivated => ActionReactivated,
            LinkOutcome.Deactivated => ActionDeactivated,
            _ => "Update"
        };

        /// <summary>
        /// One audit row per transition, on the commercial-client stream keyed by CLIENT id.
        ///
        /// Names the account so "whose designation was removed" is answerable, and carries nothing
        /// sensitive: no bank details, no Stripe ids, no payment data — only who, which company,
        /// and what happened.
        /// </summary>
        private Task LogAsync(ContractClient client, string action, User? user, int actingAdminId) =>
            _audit.LogActionAsync(
                AuditEntityTypes.CommercialClient,
                client.Id,
                action,
                null,
                new
                {
                    ClientId = client.Id,
                    Company = client.LegalEntityName,
                    LinkedAccountId = client.SourceUserId,
                    LinkedAccount = user == null
                        ? null
                        : $"{user.FirstName} {user.LastName}".Trim(),
                    Status = client.IsActive ? "Active" : "Inactive"
                },
                new[] { "Status" },
                actingAdminId);
    }
}
