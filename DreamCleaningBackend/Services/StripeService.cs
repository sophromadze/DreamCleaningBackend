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
            string receiptEmail = null, string customerId = null, bool saveCardForOffSession = false,
            string idempotencyKey = null)
        {
            try
            {
                var options = new PaymentIntentCreateOptions
                {
                    // Rounded, never truncated — identical for every 2dp amount, and a decimal that
                    // lands a hair under a cent boundary can no longer undercharge by a penny.
                    Amount = ToCents(amount),
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

                // Same role the refund and off-session paths already give it: a repeat of the
                // same logical attempt returns the intent Stripe already created rather than a
                // second chargeable one. Without a key, two prepare-payment calls produced two
                // intents and the customer was charged twice for one booking (2026-08-30).
                var requestOptions = string.IsNullOrWhiteSpace(idempotencyKey)
                    ? null
                    : new RequestOptions { IdempotencyKey = idempotencyKey };

                return await service.CreateAsync(options, requestOptions);
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

        public Task<PaymentIntent> CancelPaymentIntentAsync(string paymentIntentId) =>
            new PaymentIntentService().CancelAsync(paymentIntentId,
                new PaymentIntentCancelOptions { CancellationReason = "abandoned" });

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

        public async Task<Refund> CreateRefundAsync(string paymentIntentId, decimal? amount = null,
            string idempotencyKey = null, Dictionary<string, string> metadata = null)
        {
            try
            {
                var options = new RefundCreateOptions
                {
                    PaymentIntent = paymentIntentId,
                    Metadata = metadata
                };

                if (amount.HasValue)
                {
                    // Round before scaling: (long)(x * 100) truncates, so a decimal that lands a
                    // hair under a cent boundary would silently refund a penny short.
                    options.Amount = (long)Math.Round(amount.Value * 100, MidpointRounding.AwayFromZero);
                }

                var service = new Stripe.RefundService();
                var requestOptions = string.IsNullOrWhiteSpace(idempotencyKey)
                    ? null
                    : new RequestOptions { IdempotencyKey = idempotencyKey };

                return await service.CreateAsync(options, requestOptions);
            }
            catch (StripeException ex)
            {
                _logger.LogError(ex, "Error creating refund");
                throw new ApplicationException($"Refund processing error: {ex.Message}");
            }
        }

        public async Task<ChargeRefundState> GetChargeRefundStateAsync(string paymentIntentId)
        {
            // Synthetic references (gift-card-covered orders) never touched Stripe.
            if (string.IsNullOrWhiteSpace(paymentIntentId) || !paymentIntentId.StartsWith("pi_"))
            {
                return new ChargeRefundState
                {
                    IsRefundable = false,
                    UnavailableReason = "This order was not paid by card."
                };
            }

            try
            {
                var service = new PaymentIntentService();
                // latest_charge carries amount_refunded — the only place Dashboard-issued refunds
                // show up. Without the expand it comes back as a bare id and reads as 0 refunded.
                var intent = await service.GetAsync(paymentIntentId, new PaymentIntentGetOptions
                {
                    Expand = new List<string> { "latest_charge" }
                });

                if (intent.Status != "succeeded" || intent.AmountReceived <= 0)
                {
                    return new ChargeRefundState
                    {
                        IsRefundable = false,
                        UnavailableReason = "This payment never completed, so there is nothing to refund."
                    };
                }

                var alreadyRefunded = intent.LatestCharge?.AmountRefunded ?? 0L;

                return new ChargeRefundState
                {
                    IsRefundable = true,
                    AmountReceived = intent.AmountReceived / 100m,
                    AmountRefunded = alreadyRefunded / 100m,
                    // Deliberately separate from AmountRefunded — Stripe never folds disputed
                    // amounts into it. See ChargeRefundState.HasDispute.
                    HasDispute = intent.LatestCharge?.Disputed ?? false
                };
            }
            catch (StripeException ex)
            {
                // Deliberately swallowed: the order panel must still render if Stripe is down or
                // the intent belongs to a different (e.g. rotated) account. Refunding is disabled
                // rather than guessed at — guessing here means double-refunding real money.
                _logger.LogError(ex, "Could not read refund state for payment intent {PaymentIntentId}", paymentIntentId);
                return new ChargeRefundState
                {
                    IsRefundable = false,
                    UnavailableReason = "Could not reach the payment provider to check this charge."
                };
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

        public async Task<SetupIntent> CreateSetupIntentAsync(string stripeCustomerId, Dictionary<string, string>? metadata = null)
        {
            try
            {
                var service = new SetupIntentService();
                return await service.CreateAsync(new SetupIntentCreateOptions
                {
                    Customer = stripeCustomerId,
                    PaymentMethodTypes = new List<string> { "card" },
                    Usage = "off_session",
                    Metadata = metadata
                });
            }
            catch (StripeException ex)
            {
                _logger.LogError(ex, "Error creating setup intent for customer {CustomerId}", stripeCustomerId);
                throw new ApplicationException($"Payment processing error: {ex.Message}");
            }
        }

        public async Task<SetupIntent> GetSetupIntentAsync(string setupIntentId)
        {
            try
            {
                return await new SetupIntentService().GetAsync(setupIntentId);
            }
            catch (StripeException ex)
            {
                _logger.LogError(ex, "Error retrieving setup intent {SetupIntentId}", setupIntentId);
                throw new ApplicationException($"Payment method retrieval error: {ex.Message}");
            }
        }

        public async Task<SavedCardChargeResult> ChargeSavedCardAsync(SavedCardChargeRequest request)
        {
            var service = new PaymentIntentService();

            try
            {
                var paymentIntent = await service.CreateAsync(
                    new PaymentIntentCreateOptions
                    {
                        Amount = ToCents(request.Amount),
                        Currency = "usd",
                        Customer = request.CustomerId,
                        PaymentMethod = request.PaymentMethodId,
                        // Card-only. Without it the pinned API version (2025-06-30.basil) turns on
                        // automatic payment methods, and confirming server-side then demands a
                        // return_url for redirect-based methods — every off-session charge would
                        // have failed with an invalid_request_error (audit finding, 2026-09).
                        PaymentMethodTypes = new List<string> { "card" },
                        OffSession = request.OffSession,
                        Confirm = true,
                        Metadata = request.Metadata ?? new Dictionary<string, string>(),
                        Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description,
                        ReceiptEmail = string.IsNullOrWhiteSpace(request.ReceiptEmail) ? null : request.ReceiptEmail.Trim()
                    },
                    new RequestOptions { IdempotencyKey = request.IdempotencyKey });

                return MapIntent(paymentIntent);
            }
            catch (StripeException ex)
            {
                return MapChargeException(ex, request);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException)
            {
                // The request may or may not have reached Stripe. NEVER a decline.
                _logger.LogWarning(ex, "Saved-card charge {IdempotencyKey}: no answer from Stripe; outcome unknown.",
                    request.IdempotencyKey);
                return new SavedCardChargeResult
                {
                    Outcome = SavedCardChargeOutcome.Unknown,
                    FailureCode = "network_error",
                    Message = "No response from the payment provider."
                };
            }
        }

        /// <summary>Maps a PaymentIntent Stripe returned (no exception) to an outcome.</summary>
        public static SavedCardChargeResult MapIntent(PaymentIntent intent)
        {
            var result = new SavedCardChargeResult { PaymentIntentId = intent.Id };

            switch (intent.Status)
            {
                case "succeeded":
                    result.Outcome = SavedCardChargeOutcome.Succeeded;
                    break;
                case "processing":
                    result.Outcome = SavedCardChargeOutcome.Processing;
                    break;
                case "requires_action":
                    result.Outcome = SavedCardChargeOutcome.RequiresAction;
                    result.ClientSecret = intent.ClientSecret;
                    result.FailureCode = "authentication_required";
                    break;
                default:
                    // requires_payment_method / canceled: the attempt definitively did not charge.
                    result.Outcome = SavedCardChargeOutcome.Declined;
                    result.FailureCode = intent.LastPaymentError?.Code ?? intent.Status;
                    result.DeclineCode = intent.LastPaymentError?.DeclineCode;
                    result.Message = intent.LastPaymentError?.Message;
                    break;
            }

            return result;
        }

        /// <summary>
        /// Classifies a Stripe exception. Only an answer that PROVES no money moved is a decline:
        /// a card_error, or an invalid_request_error (detached card, missing customer). Anything
        /// that leaves the outcome open — no StripeError at all (transport), api_error / 5xx,
        /// 429, an idempotency conflict — is Unknown, and the caller must reconcile it.
        /// </summary>
        public SavedCardChargeResult MapChargeException(StripeException ex, SavedCardChargeRequest request)
        {
            var error = ex.StripeError;
            var status = (int)ex.HttpStatusCode;

            if (error == null || status == 0 || status >= 500 || status == 429
                || error.Type == "api_error" || error.Type == "idempotency_error")
            {
                _logger.LogWarning(ex, "Saved-card charge {IdempotencyKey}: outcome unknown (HTTP {Status}, {Type}).",
                    request.IdempotencyKey, status, error?.Type);
                return new SavedCardChargeResult
                {
                    Outcome = SavedCardChargeOutcome.Unknown,
                    PaymentIntentId = error?.PaymentIntent?.Id,
                    FailureCode = error?.Code ?? error?.Type ?? "unknown",
                    Message = Truncate(error?.Message ?? ex.Message, 300)
                };
            }

            if (error.Code == "authentication_required")
            {
                return new SavedCardChargeResult
                {
                    Outcome = SavedCardChargeOutcome.RequiresAction,
                    PaymentIntentId = error.PaymentIntent?.Id,
                    ClientSecret = error.PaymentIntent?.ClientSecret,
                    FailureCode = "authentication_required",
                    DeclineCode = error.DeclineCode,
                    Message = Truncate(error.Message, 300)
                };
            }

            _logger.LogInformation("Saved-card charge {IdempotencyKey} declined: {Code}/{DeclineCode}",
                request.IdempotencyKey, error.Code, error.DeclineCode);

            return new SavedCardChargeResult
            {
                Outcome = SavedCardChargeOutcome.Declined,
                PaymentIntentId = error.PaymentIntent?.Id,
                FailureCode = error.Code ?? error.Type,
                DeclineCode = error.DeclineCode,
                Message = Truncate(error.Message, 300)
            };
        }

        public async Task<PaymentIntent?> FindPaymentIntentByMetadataAsync(string key, string value)
        {
            try
            {
                var result = await new PaymentIntentService().SearchAsync(new PaymentIntentSearchOptions
                {
                    Query = $"metadata['{key}']:'{value.Replace("'", "")}'",
                    Limit = 5
                });
                return result.Data.FirstOrDefault();
            }
            catch (StripeException ex)
            {
                _logger.LogWarning(ex, "PaymentIntent search for {Key}={Value} failed.", key, value);
                return null;
            }
        }

        public async Task<CheckoutSessionState?> GetCheckoutSessionStateAsync(string sessionId)
        {
            try
            {
                var session = await new Stripe.Checkout.SessionService().GetAsync(sessionId);
                return new CheckoutSessionState
                {
                    Status = session.Status,
                    PaymentStatus = session.PaymentStatus,
                    PaymentIntentId = session.PaymentIntentId
                };
            }
            catch (StripeException ex)
            {
                _logger.LogWarning(ex, "Could not read Checkout Session {SessionId}.", sessionId);
                return null;
            }
        }

        public async Task<bool> ExpireCheckoutSessionAsync(string sessionId)
        {
            try
            {
                await new Stripe.Checkout.SessionService().ExpireAsync(sessionId);
                return true;
            }
            catch (StripeException ex)
            {
                _logger.LogInformation(ex, "Stripe refused to expire Checkout Session {SessionId}.", sessionId);
                return false;
            }
        }

        /// <summary>Dollars to integer cents, rounded — never truncated.</summary>
        public static long ToCents(decimal amount) =>
            (long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero);

        private static string? Truncate(string? value, int max) =>
            string.IsNullOrEmpty(value) ? value : value.Length <= max ? value : value[..max];

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

        public async Task<List<Stripe.PaymentMethod>> ListCustomerCardsAsync(string stripeCustomerId)
        {
            if (string.IsNullOrWhiteSpace(stripeCustomerId)) return new List<Stripe.PaymentMethod>();
            try
            {
                var service = new PaymentMethodService();
                var page = await service.ListAsync(new PaymentMethodListOptions
                {
                    Customer = stripeCustomerId,
                    Type = "card",
                    Limit = 100
                });
                return page.Data.ToList();
            }
            catch (StripeException ex)
            {
                _logger.LogWarning(ex, "Could not list cards for Stripe customer {CustomerId}", stripeCustomerId);
                throw new ApplicationException($"Payment processing error: {ex.Message}");
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// Both lookups are scoped to the CUSTOMER, so a payment method belonging to somebody else
        /// can never produce evidence here. A SetupIntent is only ever created by this application
        /// for a signed-in customer adding a card; setup_future_usage is only ever set by the
        /// browser confirming a payment after the customer chose "Save Card &amp; Pay".
        /// </remarks>
        public async Task<string?> FindCardSaveConsentAsync(string stripeCustomerId, string paymentMethodId)
        {
            if (string.IsNullOrWhiteSpace(stripeCustomerId) || string.IsNullOrWhiteSpace(paymentMethodId)) return null;
            try
            {
                var setupIntents = await new SetupIntentService().ListAsync(new SetupIntentListOptions
                {
                    Customer = stripeCustomerId,
                    PaymentMethod = paymentMethodId,
                    Limit = 10
                });
                if (setupIntents.Data.Any(s => s.Status == "succeeded")) return "setup_intent";

                // PaymentIntents cannot be filtered by payment method, so the customer's recent
                // ones are read and matched here.
                var paymentIntents = await new PaymentIntentService().ListAsync(new PaymentIntentListOptions
                {
                    Customer = stripeCustomerId,
                    Limit = 100
                });
                var saved = paymentIntents.Data.Any(i =>
                    i.PaymentMethodId == paymentMethodId
                    && string.Equals(i.SetupFutureUsage, "off_session", StringComparison.OrdinalIgnoreCase)
                    && i.Status is "succeeded" or "processing");

                return saved ? "payment_intent" : null;
            }
            catch (StripeException ex)
            {
                // "Don't know" is not "no consent": the caller leaves the card alone.
                _logger.LogWarning(ex, "Could not check save consent for card {PaymentMethodId}", paymentMethodId);
                throw new ApplicationException($"Payment processing error: {ex.Message}");
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
