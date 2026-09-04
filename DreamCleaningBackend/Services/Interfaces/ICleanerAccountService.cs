using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Services.Interfaces
{
    /// <summary>
    /// The account-to-cleaner link: discovering it on registration, resolving it for the portal,
    /// and editing it from the admin Cleaners tab. The RULES are in
    /// Helpers/CleanerAccountLink; this is the part that needs the database.
    /// </summary>
    public interface ICleanerAccountService
    {
        /// <summary>
        /// Called on every self-service registration, BEFORE the account is saved. When the address
        /// being registered matches a cleaner record, the account is created on the Cleaner role
        /// instead of Customer. Mutates <paramref name="user"/> only - the caller's SaveChanges
        /// persists it, so a failed registration cannot leave a half-promoted account behind.
        /// Returns the matched cleaner id, or null when this is an ordinary customer.
        /// </summary>
        Task<int?> ApplyCleanerRoleIfEmailMatchesAsync(User user);

        /// <summary>
        /// Completes the link for an account that was just promoted by
        /// <see cref="ApplyCleanerRoleIfEmailMatchesAsync"/>, once it has an Id. Separate call
        /// because Cleaner.UserId is a foreign key and the user has no Id until the insert lands.
        /// No-op when the cleaner record is already linked to somebody.
        /// </summary>
        Task LinkOnRegistrationAsync(int cleanerId, int userId);

        /// <summary>
        /// Which cleaner record is behind this account: the FK link first, then - only when nothing
        /// is linked - a case-insensitive email match, which is how an account created before this
        /// feature still finds its jobs. Null when the account belongs to no cleaner.
        /// </summary>
        Task<int?> ResolveCleanerIdForUserAsync(int userId);
    }
}
