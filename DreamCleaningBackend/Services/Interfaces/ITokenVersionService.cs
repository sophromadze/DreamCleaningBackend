using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Services.Interfaces
{
    /// <summary>
    /// Server-side session revocation. A JWT is self-contained and lives 30 days, so until this
    /// existed the ONLY way an admin could end somebody's session was the SignalR "RoleChanged"
    /// notice - which reaches a browser that is open and nothing else. An offline user kept the
    /// permissions of their old role until the token expired on its own.
    /// </summary>
    public interface ITokenVersionService
    {
        /// <summary>Does this token's "tv" claim still match the account's current version?</summary>
        Task<bool> IsTokenCurrentAsync(int userId, int tokenVersion);

        /// <summary>
        /// Ends every session the account holds: bumps the version (so issued access tokens stop
        /// validating) and drops the refresh token (so the browser cannot silently mint a new one
        /// and stay signed in). Mutates the entity only - the CALLER saves, then calls
        /// <see cref="SyncCache"/> so the new value is what the next request reads.
        /// </summary>
        void RevokeSessions(User user);

        /// <summary>Publish a freshly saved version to the cache. Call AFTER SaveChangesAsync.</summary>
        void SyncCache(int userId, int tokenVersion);
    }
}
