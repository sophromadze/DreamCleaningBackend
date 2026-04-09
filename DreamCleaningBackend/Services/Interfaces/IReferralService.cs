using DreamCleaningBackend.DTOs;

namespace DreamCleaningBackend.Services.Interfaces
{
    public interface IReferralService
    {
        Task<ReferralValidationResult> ValidateCode(string code);
        Task ProcessReferralRegistration(int newUserId, string referralCode);
        Task ProcessReferralOrderCompletion(int userId);
        Task<List<ReferralDto>> GetMyReferrals(int userId);
        Task<string> GenerateReferralCode(int userId);
    }
}
