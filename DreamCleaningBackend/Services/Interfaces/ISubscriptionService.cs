namespace DreamCleaningBackend.Services.Interfaces
{
    public interface ISubscriptionService
    {
        Task<bool> ActivateSubscription(int userId, int subscriptionId);
        Task<bool> CheckAndUpdateSubscriptionStatus(int userId);
        Task<bool> RenewSubscription(int userId);
        Task<bool> DeactivateSubscription(int userId);
        Task<decimal> GetUserDiscountPercentage(int userId);
    }
}