using DreamCleaningBackend.DTOs;

namespace DreamCleaningBackend.Services.Interfaces
{
    public interface IBubblePointsService
    {
        Task<string> GetTier(decimal totalSpent);
        Task<decimal> GetMultiplier(string tier);
        Task AddPoints(int userId, int points, string type, string description, int? orderId = null);
        Task ProcessOrderCompletion(int orderId);
        Task GrantWelcomeBonus(int userId);
        Task<BubbleRewardsSummaryDto> GetSummary(int userId);
        Task<HeaderSummaryDto> GetHeaderSummary(int userId);
        Task<RedemptionResultDto> RedeemPoints(int userId, int points, int orderId);
        Task<PagedResult<BubblePointsHistoryDto>> GetHistory(int userId, int page, int pageSize);
        Task AdminAdjustPoints(int userId, int points, string description);
        Task AdminGrantCredit(int userId, decimal amount, string description);
        Task AdminGrantReviewBonus(int userId);
        Task<(decimal creditAmount, bool valid, string message)> GetPointsCreditForBooking(int points);
        Task DeductPointsForBooking(int userId, int points, int orderId);
        Task ReverseOrderCompletion(int orderId);
    }
}
