using Stripe;

namespace DreamCleaningBackend.Services.Interfaces
{
    public interface IStripeService
    {
        Task<PaymentIntent> CreatePaymentIntentAsync(decimal amount, Dictionary<string, string> metadata = null);
        Task<PaymentIntent> ConfirmPaymentIntentAsync(string paymentIntentId);
        Task<PaymentIntent> GetPaymentIntentAsync(string paymentIntentId);
        Task<Refund> CreateRefundAsync(string paymentIntentId, decimal? amount = null);
    }
}
