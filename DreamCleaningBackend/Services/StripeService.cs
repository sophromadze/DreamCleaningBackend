using DreamCleaningBackend.Models;
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

        public async Task<PaymentIntent> CreatePaymentIntentAsync(decimal amount, Dictionary<string, string> metadata = null,
            string receiptEmail = null, string customerId = null, bool saveCardForOffSession = false)
        {
            try
            {
                var options = new PaymentIntentCreateOptions
                {
                    Amount = (long)(amount * 100), // Convert to cents
                    Currency = "usd",
                    PaymentMethodTypes = new List<string> { "card" },
                    Metadata = metadata ?? new Dictionary<string, string>(),
                    // Stripe's receipt goes to the ORDER's email regardless of who pays —
                    // guest payment links may be settled by a relative/helper.
                    ReceiptEmail = string.IsNullOrWhiteSpace(receiptEmail) ? null : receiptEmail.Trim(),
                    Customer = string.IsNullOrWhiteSpace(customerId) ? null : customerId,
                    // Saves the card being used for THIS payment for later off-session
                    // recurring charges. Stripe requires a Customer on the intent for this.
                    SetupFutureUsage = saveCardForOffSession && !string.IsNullOrWhiteSpace(customerId)
                        ? "off_session" : null
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

        public async Task<string> CreateOrGetCustomerAsync(User user)
        {
            var customerService = new CustomerService();

            // Reuse the stored Customer unless it was deleted on Stripe's side (e.g. from the
            // dashboard) — recreating on resource_missing self-heals instead of failing forever.
            if (!string.IsNullOrEmpty(user.StripeCustomerId))
            {
                try
                {
                    var existing = await customerService.GetAsync(user.StripeCustomerId);
                    if (existing != null && existing.Deleted != true)
                        return user.StripeCustomerId;
                }
                catch (StripeException ex) when (ex.StripeError?.Code == "resource_missing")
                {
                    _logger.LogWarning("Stripe customer {CustomerId} for user {UserId} no longer exists — recreating",
                        user.StripeCustomerId, user.Id);
                }
            }

            try
            {
                var customer = await customerService.CreateAsync(new CustomerCreateOptions
                {
                    // Never leak the no-email placeholder address (…@no-email.invalid) to Stripe.
                    Email = user.IsNoEmailUser ? null : user.Email,
                    Name = $"{user.FirstName} {user.LastName}".Trim(),
                    Metadata = new Dictionary<string, string> { { "userId", user.Id.ToString() } }
                });

                // Mutates the tracked entity; the caller owns SaveChanges.
                user.StripeCustomerId = customer.Id;
                return customer.Id;
            }
            catch (StripeException ex)
            {
                _logger.LogError(ex, "Error creating Stripe customer for user {UserId}", user.Id);
                throw new ApplicationException($"Payment processing error: {ex.Message}");
            }
        }

        public async Task<SetupIntent> CreateSetupIntentAsync(string stripeCustomerId)
        {
            try
            {
                var service = new SetupIntentService();
                return await service.CreateAsync(new SetupIntentCreateOptions
                {
                    Customer = stripeCustomerId,
                    PaymentMethodTypes = new List<string> { "card" },
                    Usage = "off_session"
                });
            }
            catch (StripeException ex)
            {
                _logger.LogError(ex, "Error creating setup intent for customer {CustomerId}", stripeCustomerId);
                throw new ApplicationException($"Payment processing error: {ex.Message}");
            }
        }

        public async Task<OffSessionChargeResult> CreateOffSessionPaymentIntentAsync(decimal amount, string customerId,
            string paymentMethodId, Dictionary<string, string> metadata, string idempotencyKey,
            string receiptEmail = null)
        {
            var service = new PaymentIntentService();

            try
            {
                var paymentIntent = await service.CreateAsync(
                    new PaymentIntentCreateOptions
                    {
                        Amount = (long)(amount * 100),
                        Currency = "usd",
                        Customer = customerId,
                        PaymentMethod = paymentMethodId,
                        OffSession = true,
                        Confirm = true,
                        Metadata = metadata ?? new Dictionary<string, string>(),
                        ReceiptEmail = string.IsNullOrWhiteSpace(receiptEmail) ? null : receiptEmail.Trim()
                    },
                    new RequestOptions { IdempotencyKey = idempotencyKey });

                if (paymentIntent.Status == "succeeded")
                {
                    return new OffSessionChargeResult { Success = true, PaymentIntentId = paymentIntent.Id };
                }

                if (paymentIntent.Status == "requires_action")
                {
                    // SCA/3DS — cannot be completed with the customer absent.
                    return new OffSessionChargeResult
                    {
                        RequiresAction = true,
                        PaymentIntentId = paymentIntent.Id,
                        FailureReason = "authentication_required: the bank asked for extra verification, which needs the customer present"
                    };
                }

                return new OffSessionChargeResult
                {
                    PaymentIntentId = paymentIntent.Id,
                    FailureReason = $"unexpected_status: {paymentIntent.Status}"
                };
            }
            catch (StripeException ex)
            {
                var error = ex.StripeError;

                if (error?.Code == "authentication_required")
                {
                    return new OffSessionChargeResult
                    {
                        RequiresAction = true,
                        PaymentIntentId = error.PaymentIntent?.Id,
                        FailureReason = "authentication_required: the bank asked for extra verification, which needs the customer present"
                    };
                }

                // Hard decline / expired card / detached payment method / missing customer, etc.
                var code = error?.DeclineCode ?? error?.Code ?? "stripe_error";
                var message = error?.Message ?? ex.Message;
                _logger.LogWarning(ex, "Off-session charge declined ({Code}) for customer {CustomerId}", code, customerId);

                return new OffSessionChargeResult
                {
                    PaymentIntentId = error?.PaymentIntent?.Id,
                    FailureReason = $"{code}: {message}"
                };
            }
        }

        public async Task<Stripe.PaymentMethod> GetPaymentMethodAsync(string paymentMethodId)
        {
            try
            {
                var service = new PaymentMethodService();
                return await service.GetAsync(paymentMethodId);
            }
            catch (StripeException ex)
            {
                _logger.LogError(ex, "Error retrieving payment method {PaymentMethodId}", paymentMethodId);
                throw new ApplicationException($"Payment method retrieval error: {ex.Message}");
            }
        }

        public async Task DetachPaymentMethodAsync(string paymentMethodId)
        {
            try
            {
                var service = new PaymentMethodService();
                await service.DetachAsync(paymentMethodId);
            }
            catch (StripeException ex)
            {
                // Best-effort cleanup — an already-detached/missing card is not an error worth failing over.
                _logger.LogWarning(ex, "Could not detach payment method {PaymentMethodId}", paymentMethodId);
            }
        }

        public async Task UpdatePaymentIntentMetadataAsync(string paymentIntentId, Dictionary<string, string> metadata)
        {
            try
            {
                var service = new PaymentIntentService();
                await service.UpdateAsync(paymentIntentId, new PaymentIntentUpdateOptions { Metadata = metadata });
            }
            catch (StripeException ex)
            {
                // Traceability nicety only — never let it break the flow that called it.
                _logger.LogWarning(ex, "Could not update metadata on payment intent {PaymentIntentId}", paymentIntentId);
            }
        }
    }
}
