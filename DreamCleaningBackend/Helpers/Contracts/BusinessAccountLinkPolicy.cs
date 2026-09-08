using DreamCleaningBackend.Data;
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
    }
}
