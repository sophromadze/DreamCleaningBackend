namespace DreamCleaningBackend.Services.Interfaces
{
    public interface ISubscriptionService
    {
        Task<bool> ActivateSubscription(int userId, int subscriptionId, DateTime startDate);
        Task<bool> CheckAndUpdateSubscriptionStatus(int userId);
        Task<bool> RenewSubscription(int userId, DateTime startDate);
        Task<bool> DeactivateSubscription(int userId);
        Task<decimal> GetUserDiscountPercentage(int userId);
    }
}