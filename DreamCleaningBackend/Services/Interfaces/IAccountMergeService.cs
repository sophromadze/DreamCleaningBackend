using DreamCleaningBackend.DTOs;

namespace DreamCleaningBackend.Services.Interfaces
{
    public interface IAccountMergeService
    {
        /// <summary>Verifies ownership of the old account via 6-digit email code and performs the merge. Returns merge result with new token.</summary>
        Task<MergeResultDto> ConfirmAndMergeAsync(int newAccountId, string verificationMethod, string verificationToken);
        /// <summary>Sends a new merge confirmation code to the verified email for the pending merge request.</summary>
        Task ResendMergeCodeAsync(int newAccountId);
    }
}
