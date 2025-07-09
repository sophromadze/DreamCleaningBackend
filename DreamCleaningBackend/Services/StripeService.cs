using DreamCleaningBackend.Services.Interfaces;
using Stripe;

namespace DreamCleaningBackend.Services
{
    public class StripeService : IStripeService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<StripeService> _logger;

        public StripeService(IConfiguration configuration, ILogger<StripeService> logger)
        {
            _configuration = configuration;
            _logger = logger;
            StripeConfiguration.ApiKey = _configuration["Stripe:SecretKey"];
        }

        public async Task<PaymentIntent> CreatePaymentIntentAsync(decimal amount, Dictionary<string, string> metadata = null)
        {
            try
            {
                var options = new PaymentIntentCreateOptions
                {
                    Amount = (long)(amount * 100), // Convert to cents
                    Currency = "usd",
                    PaymentMethodTypes = new List<string> { "card" },
                    Metadata = metadata ?? new Dictionary<string, string>()
                };

                var service = new PaymentIntentService();
                return await service.CreateAsync(options);
            }
            catch (StripeException ex)
            {
                _logger.LogError(ex, "Error creating payment intent");
                throw new ApplicationException($"Payment processing error: {ex.Message}");
            }
        }

        public async Task<PaymentIntent> ConfirmPaymentIntentAsync(string paymentIntentId)
        {
            try
            {
                var service = new PaymentIntentService();
                return await service.ConfirmAsync(paymentIntentId);
            }
            catch (StripeException ex)
            {
                _logger.LogError(ex, "Error confirming payment intent");
                throw new ApplicationException($"Payment confirmation error: {ex.Message}");
            }
        }

        public async Task<PaymentIntent> GetPaymentIntentAsync(string paymentIntentId)
        {
            try
            {
                var service = new PaymentIntentService();
                return await service.GetAsync(paymentIntentId);
            }
            catch (StripeException ex)
            {
                _logger.LogError(ex, "Error retrieving payment intent");
                throw new ApplicationException($"Payment retrieval error: {ex.Message}");
            }
        }

        public async Task<Refund> CreateRefundAsync(string paymentIntentId, decimal? amount = null)
        {
            try
            {
                var options = new RefundCreateOptions
                {
                    PaymentIntent = paymentIntentId
                };

                if (amount.HasValue)
                {
                    options.Amount = (long)(amount.Value * 100); // Partial refund
                }

                var service = new RefundService();
                return await service.CreateAsync(options);
            }
            catch (StripeException ex)
            {
                _logger.LogError(ex, "Error creating refund");
                throw new ApplicationException($"Refund processing error: {ex.Message}");
            }
        }
    }
}
