using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models.Contracts;
using DreamCleaningBackend.Services.Contracts;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Helpers.Contracts
{
    /// <summary>
    /// WHICH CUSTOMER ACCOUNT A COMMERCIAL CLIENT MAY BE LINKED TO.
    ///
    /// <c>ContractClient.SourceUserId</c> is the ownership key for the self-service My Contracts
    /// area: a customer sees exactly the contracts whose client carries their id. So the link is
    /// an access grant, and pointing it at an ordinary residential customer would hand them a
    /// contracts area belonging to somebody else. Only an account flagged as a BUSINESS may be
    /// named, and it is named deliberately by staff - never inferred from a matching email
    /// address, a matching name, or anything else.
    ///
    /// This lived as a private method on <see cref="ContractService"/> until standalone client
    /// creation shipped (2026-09) and needed the identical rule. It moved here rather than being
    /// copied: two call sites deciding independently who may be linked is how one of them ends up
    /// admitting an account the other would refuse.
    ///
    /// Null is fully supported and is the common case - a client typed up for a company with no
    /// account at all. Those stay reachable only through the emailed token link.
    /// </summary>
    public static class BusinessAccountLinkPolicy
    {
        public const string MissingAccountMessage =
            "The linked customer account no longer exists.";

        public const string NotBusinessMessage =
            "That customer is not flagged as a business. Turn on the business flag on their account first.";

        /// <summary>
        /// Returns the id to store, or null when nothing was chosen.
        /// Throws <see cref="ContractWorkflowException"/> (a 400 through the shared filter) when
        /// the account is gone or is not a business.
        /// </summary>
        public static async Task<int?> ResolveAsync(ApplicationDbContext context, int? sourceUserId)
        {
            if (!sourceUserId.HasValue) return null;

            var account = await context.Users
                .Where(u => u.Id == sourceUserId.Value)
                .Select(u => new { u.Id, u.IsBusiness })
                .FirstOrDefaultAsync();

            if (account == null)
                throw new ContractWorkflowException(MissingAccountMessage);

            if (!account.IsBusiness)
                throw new ContractWorkflowException(NotBusinessMessage);

            return account.Id;
        }

        /// <summary>
        /// The commercial client an account is ALREADY linked to, or null.
        ///
        /// AT MOST ONE CAN EXIST — <c>IX_ContractClients_SourceUserId</c> is UNIQUE — so this is a
        /// lookup, not a search, and a caller that finds a row must ADOPT it rather than insert
        /// another.
        ///
        /// It matters because ticking the business flag on a customer auto-creates their client, so
        /// by the time an admin can pick that account on the contract form, its client exists.
        /// Inserting a second one violates the index and 500s the save; before the index existed it
        /// produced two commercial records for one business, splitting their contracts and invoices
        /// across rows that no report would ever reunite.
        ///
        /// Deactivated clients are INCLUDED deliberately: a retired client being contracted with
        /// again is exactly the row to bring back, and skipping it here would send the caller
        /// straight into the duplicate insert this exists to prevent.
        /// </summary>
        public static Task<ContractClient?> FindLinkedClientAsync(
            ApplicationDbContext context, int? sourceUserId)
        {
            if (!sourceUserId.HasValue) return Task.FromResult<ContractClient?>(null);

            return context.ContractClients
                .FirstOrDefaultAsync(c => c.SourceUserId == sourceUserId.Value);
        }

        /// <summary>
        /// The accounts that are ON the Business Clients tab right now: those whose linked client
        /// is ACTIVE.
        ///
        /// <b>"Active" is the whole point, and leaving it out was a bug (2026-09).</b> Admin ->
        /// Users partitions every account across its sub-tabs, and the Customers tab hides the
        /// ones Business Clients is already showing. Nothing is ever hard-deleted here, so "Move
        /// to Customers" clears <c>User.IsBusiness</c> and DEACTIVATES the linked client, leaving
        /// the row in place with its contracts and its invoices. A membership test that only
        /// asked whether a row EXISTS therefore stayed true forever - and the account vanished
        /// from the panel altogether: gone from Business Clients (that list is active-only) and
        /// still hidden from Customers by a link nobody could see.
        ///
        /// Loaded as one set rather than a correlated <c>Any()</c> per row: the users list is a
        /// full-table read, and that subquery ran once per account.
        /// </summary>
        public static async Task<HashSet<int>> LoadBusinessClientAccountIdsAsync(
            ApplicationDbContext context)
        {
            var ids = await context.ContractClients
                .Where(c => c.SourceUserId != null && c.IsActive)
                .Select(c => c.SourceUserId!.Value)
                .ToListAsync();

            return ids.ToHashSet();
        }
    }
}
