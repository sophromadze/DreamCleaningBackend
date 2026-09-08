using Stripe;
using Microsoft.AspNetCore.Mvc;
using DreamCleaningBackend.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using DreamCleaningBackend.DTOs;
using System.Text.Json;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using DreamCleaningBackend.Helpers;

namespace DreamCleaningBackend.Controllers
{
    [ApiController]
    [Route("api/stripewebhook")]
    public class StripeWebhookController : ControllerBase
    {
        private readonly IConfiguration _configuration;
        private readonly ApplicationDbContext _context;
        private readonly IOrderPaymentStatusReconciler _reconciler;
        private readonly ILogger<StripeWebhookController> _logger;
        private readonly IHostEnvironment _environment;

        public StripeWebhookController(
            IConfiguration configuration,
            ApplicationDbContext context,
            IOrderPaymentStatusReconciler reconciler,
            ILogger<StripeWebhookController> logger,
            IHostEnvironment environment)
        {
            _configuration = configuration;
            _context = context;
            _reconciler = reconciler;
            _logger = logger;
            _environment = environment;
        }

        [HttpPost]
        [AllowAnonymous]  // ADD THIS - This is critical!
        public async Task<IActionResult> Handle()
        {
            var startTime = DateTime.UtcNow;
            _logger.LogInformation("Webhook request received at {StartTime}", startTime);
            
            try
            {
                // Set a timeout for the entire webhook processing
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25)); // 25 second timeout
                
                var json = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync();
                
                // Don't log the full webhook body in production as it may contain sensitive data
                _logger.LogInformation("Received webhook event with body length: {BodyLength}", json?.Length ?? 0);

                // Check for empty body
                if (string.IsNullOrEmpty(json))
                {
                    _logger.LogWarning("Empty request body received");
                    return BadRequest("Empty request body");
                }

                // Check for Stripe signature header
                var stripeSignature = Request.Headers["Stripe-Signature"].FirstOrDefault();
                if (string.IsNullOrEmpty(stripeSignature))
                {
                    _logger.LogWarning("Missing Stripe-Signature header");
                    return BadRequest("Missing Stripe-Signature header");
                }

                // Check for webhook secret configuration
                var webhookSecret = _configuration["Stripe:WebhookSecret"];
                if (string.IsNullOrEmpty(webhookSecret))
                {
                    _logger.LogError("Webhook secret not configured");
                    return StatusCode(500, "Webhook configuration error");
                }

                // Validate Stripe event with proper error handling.
                //
                // The signature is verified on every environment. Only the API-VERSION check is
                // relaxed, and only in Development, so `stripe listen` can forward events formatted
                // with the CLI account's older default version instead of 400-ing before the
                // handler runs — a failure that reads exactly like a wrong signing secret.
                // See Helpers/StripeWebhookEventParser.cs.
                Event stripeEvent;
                var tolerateApiVersionMismatch =
                    StripeWebhookEventParser.TolerateApiVersionMismatch(_environment);
                try
                {
                    var parsed = StripeWebhookEventParser.Parse(
                        json,
                        stripeSignature,
                        webhookSecret,
                        tolerateApiVersionMismatch);

                    stripeEvent = parsed.Event;

                    if (parsed.ApiVersionMismatched)
                    {
                        _logger.LogWarning(
                            "DEVELOPMENT ONLY: processing Stripe event {EventId} formatted with API " +
                            "version {ReceivedApiVersion} while Stripe.net expects {ExpectedApiVersion}. " +
                            "Fields added or renamed between those two versions can deserialize as null, " +
                            "so treat anything verified on this path as unconfirmed. Production rejects " +
                            "this event outright — its webhook destination is pinned to the expected " +
                            "version. Point `stripe listen` at a destination on that version to be sure.",
                            parsed.Event.Id,
                            parsed.ReceivedApiVersion,
                            StripeWebhookEventParser.ExpectedApiVersion);
                    }
                }
                catch (StripeException ex)
                {
                    // Two different failures land here — a bad signature and an unexpected API
                    // version — and calling both "invalid signature" is what sent people hunting
                    // for a signing secret that was never wrong. Let the Stripe message say which.
                    _logger.LogError(ex, "Failed to verify or parse Stripe event: {Message}", ex.Message);
                    return BadRequest($"Webhook rejected: {ex.Message}");
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, "Invalid JSON payload received");
                    return BadRequest("Invalid JSON payload");
                }

                _logger.LogInformation("Processing Stripe event: {EventType} for {EventId}", stripeEvent.Type, stripeEvent.Id);

                // Check for duplicate events (optional but recommended)
                if (await IsEventAlreadyProcessed(stripeEvent.Id))
                {
                    _logger.LogInformation("Event {EventId} already processed, skipping", stripeEvent.Id);
                    return Ok(); // Return 200 to acknowledge receipt
                }

                // Handle the event with timeout
                try
                {
                    switch (stripeEvent.Type)
                    {
                        case "payment_intent.succeeded":
                            var paymentIntent = stripeEvent.Data.Object as PaymentIntent;
                            if (paymentIntent != null)
                            {
                                await HandlePaymentIntentSucceeded(paymentIntent, cts.Token);
                            }
                            else
                            {
                                _logger.LogWarning("PaymentIntent object is null for event: {EventType}", stripeEvent.Type);
                            }
                            break;
                        case "payment_intent.payment_failed":
                            var failedPayment = stripeEvent.Data.Object as PaymentIntent;
                            if (failedPayment != null)
                            {
                                await HandlePaymentIntentFailed(failedPayment, cts.Token);
                            }
                            else
                            {
                                _logger.LogWarning("PaymentIntent object is null for event: {EventType}", stripeEvent.Type);
                            }
                            break;
                        case "payment_intent.canceled":
                            var canceledPayment = stripeEvent.Data.Object as PaymentIntent;
                            if (canceledPayment != null)
                            {
                                await HandlePaymentIntentCanceled(canceledPayment, cts.Token);
                            }
                            else
                            {
                                _logger.LogWarning("PaymentIntent object is null for event: {EventType}", stripeEvent.Type);
                            }
                            break;

                        // ── Commercial invoicing (2026-09) ──────────────────────────────────
                        // Three events the residential flow never needed, because a card charge is
                        // synchronous and ACH is not. An ACH debit is authorized on one day and
                        // settles days later, so the lifecycle has a middle that has to be tracked.
                        //
                        // All three are no-ops for residential payments: each one checks the
                        // commercial metadata discriminator first and returns if it is absent.
                        case "payment_intent.processing":
                            var processingIntent = stripeEvent.Data.Object as PaymentIntent;
                            if (processingIntent != null)
                            {
                                await HandleCommercialProcessing(processingIntent);
                            }
                            break;

                        case "checkout.session.completed":
                            var completedSession = stripeEvent.Data.Object as Stripe.Checkout.Session;
                            if (completedSession != null)
                            {
                                await HandleCommercialCheckoutCompleted(completedSession);
                            }
                            break;

                        case "checkout.session.expired":
                            var expiredSession = stripeEvent.Data.Object as Stripe.Checkout.Session;
                            if (expiredSession != null)
                            {
                                await HandleCommercialCheckoutExpired(expiredSession);
                            }
                            break;

                        default:
                            _logger.LogInformation("Unhandled event type: {EventType}", stripeEvent.Type);
                            break;
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogError("Webhook processing timed out after 25 seconds for event: {EventType}", stripeEvent.Type);
                    return StatusCode(408, "Request timeout");
                }

                // Mark event as processed
                await MarkEventAsProcessed(stripeEvent.Id);

                var processingTime = DateTime.UtcNow - startTime;
                _logger.LogInformation("Successfully processed webhook event: {EventType} in {ProcessingTime}ms", 
                    stripeEvent.Type, processingTime.TotalMilliseconds);
                
                return Ok();
            }
            catch (StripeException e)
            {
                _logger.LogError(e, "Stripe webhook error: {Message}", e.Message);
                return BadRequest($"Webhook Error: {e.Message}");
            }
            catch (Exception e)
            {
                var processingTime = DateTime.UtcNow - startTime;
                _logger.LogError(e, "Unexpected error in webhook handler after {ProcessingTime}ms: {Message}", 
                    processingTime.TotalMilliseconds, e.Message);
                return StatusCode(500, "Internal server error");
            }
        }

        private async Task HandlePaymentIntentSucceeded(PaymentIntent paymentIntent, CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation("Processing payment intent succeeded: {PaymentIntentId}", paymentIntent?.Id);
                
                if (paymentIntent?.Metadata == null)
                {
                    _logger.LogWarning("Payment intent or metadata is null");
                    return;
                }

                var metadata = paymentIntent.Metadata;

                if (metadata.TryGetValue("type", out var type))
                {
                    switch (type)
                    {
                        case "booking":
                            await HandleBookingPayment(paymentIntent, cancellationToken);
                            break;

                        case "order_update":
                            await HandleOrderUpdatePayment(paymentIntent, cancellationToken);
                            break;
                        
                        case "gift_card":
                            await HandleGiftCardPayment(paymentIntent, cancellationToken);
                            break;

                        // Commercial invoicing (2026-09). Slots in as another discriminator value
                        // rather than a second webhook system, so there is one signature
                        // verification, one idempotency table and one endpoint to configure —
                        // while commercial events stay clearly distinguishable from residential
                        // ones by this very switch.
                        case Services.Commercial.StripeCommercialInvoiceMetadata.TypeValue:
                            await HandleCommercialInvoicePayment(paymentIntent);
                            break;

                        default:
                            _logger.LogWarning("Unknown payment type: {PaymentType}", type);
                            break;
                    }
                }
                else
                {
                    _logger.LogWarning("Payment intent {PaymentIntentId} has no type metadata", paymentIntent.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling payment intent succeeded: {PaymentIntentId}", paymentIntent?.Id);
                throw; // Re-throw to be caught by the main handler
            }
        }

        private async Task HandleBookingPayment(PaymentIntent paymentIntent, CancellationToken cancellationToken = default)
        {
            var metadata = paymentIntent.Metadata;
            
            if (metadata.TryGetValue("orderId", out var orderIdStr) &&
                int.TryParse(orderIdStr, out var orderId))
            {
                var order = await _context.Orders.FindAsync(orderId, cancellationToken);
                if (order != null && !order.IsPaid)
                {
                    order.IsPaid = true;
                    order.PaidAt = DateTime.UtcNow;
                    order.Status = "Active";

                    // Set initial values when order is first paid
                    if (order.InitialSubTotal == 0 && order.InitialTax == 0 && order.InitialTotal == 0)
                    {
                        order.InitialSubTotal = order.SubTotal;
                        order.InitialTax = order.Tax;
                        order.InitialTips = order.Tips;
                        order.InitialCompanyDevelopmentTips = order.CompanyDevelopmentTips;
                        order.InitialTotal = order.Total;

                        _logger.LogInformation("Set initial pricing for order {OrderId}: SubTotal=${SubTotal}, Tax=${Tax}, Tips=${Tips}, CompanyTips=${CompanyTips}, Total=${Total}", 
                            orderId, order.InitialSubTotal, order.InitialTax, order.InitialTips, order.InitialCompanyDevelopmentTips, order.InitialTotal);
                    }

                    await _context.SaveChangesAsync(cancellationToken);
                    _logger.LogInformation("Successfully marked order {OrderId} as paid", orderId);
                }
                else
                {
                    _logger.LogWarning("Order {OrderId} not found or already paid", orderId);
                }
            }
            else
            {
                _logger.LogWarning("Invalid orderId in payment intent metadata: {PaymentIntentId}", paymentIntent.Id);
            }
        }

        private async Task HandleOrderUpdatePayment(PaymentIntent paymentIntent, CancellationToken cancellationToken = default)
        {
            var metadata = paymentIntent.Metadata;
            
            if (metadata.TryGetValue("orderId", out var updateOrderIdStr) &&
                int.TryParse(updateOrderIdStr, out var updateOrderId) &&
                metadata.TryGetValue("additionalAmount", out var additionalAmountStr) &&
                decimal.TryParse(additionalAmountStr, out var additionalAmount))
            {
                _logger.LogInformation("Additional payment of ${AdditionalAmount} received for order {OrderId}", additionalAmount, updateOrderId);

                // Settling the rows and the Pending -> Active flip both live in the reconciler, which
                // saves before it checks for remaining unpaid amounts. Doing that inline here was the
                // bug: the check ran against unsaved data, always looked unpaid, and the flip never
                // fired — so an order the customer had paid in full stayed "Pending" forever.
                // Idempotent, so it is safe when the browser's confirm call already handled this.
                var result = await _reconciler.ApplyStripeAdditionalPaymentAsync(
                    updateOrderId, paymentIntent.Id, cancellationToken);

                if (!result.OrderFound)
                {
                    _logger.LogWarning("Order {OrderId} not found for update payment", updateOrderId);
                }
                else if (result.StatusReactivated)
                {
                    _logger.LogInformation("Order {OrderId} moved Pending -> Active after additional payment {PaymentIntentId}",
                        updateOrderId, paymentIntent.Id);
                }
            }
            else
            {
                _logger.LogWarning("Invalid metadata for order update payment: {PaymentIntentId}", paymentIntent.Id);
            }
        }

        private async Task HandleGiftCardPayment(PaymentIntent paymentIntent, CancellationToken cancellationToken = default)
        {
            var metadata = paymentIntent.Metadata;
            
            if (metadata.TryGetValue("giftCardId", out var giftCardIdStr) &&
                int.TryParse(giftCardIdStr, out var giftCardId))
            {
                var giftCard = await _context.GiftCards.FindAsync(giftCardId, cancellationToken);
                if (giftCard != null && !giftCard.IsPaid)
                {
                    giftCard.IsPaid = true;
                    giftCard.PaidAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync(cancellationToken);
                    _logger.LogInformation("Successfully marked gift card {GiftCardId} as paid", giftCardId);
                }
                else
                {
                    _logger.LogWarning("Gift card {GiftCardId} not found or already paid", giftCardId);
                }
            }
            else
            {
                _logger.LogWarning("Invalid giftCardId in payment intent metadata: {PaymentIntentId}", paymentIntent.Id);
            }
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  Commercial invoicing
        //
        //  Every method here returns immediately unless the Stripe object carries the commercial
        //  metadata discriminator, so residential payments pass through completely untouched.
        //  Failures are logged and swallowed rather than rethrown: a commercial bookkeeping
        //  problem must never make the endpoint 500 and cause Stripe to retry a residential
        //  booking payment that already succeeded.
        // ══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Money settled on a commercial invoice. Delegates to the service that owns the
        /// transaction, the ledger write and the idempotency guard.
        /// </summary>
        private async Task HandleCommercialInvoicePayment(PaymentIntent paymentIntent)
        {
            try
            {
                var commercial = HttpContext.RequestServices
                    .GetRequiredService<Services.Commercial.InvoiceStripePaymentService>();

                var sourceLabel = Services.Commercial.InvoiceStripePaymentService
                    .BuildSourceLabel(paymentIntent);

                var result = await commercial.RecordSucceededAsync(
                    paymentIntent.Id,
                    // AmountReceived is what actually settled, which is the figure to record.
                    paymentIntent.AmountReceived > 0 ? paymentIntent.AmountReceived : paymentIntent.Amount,
                    paymentIntent.Currency,
                    paymentIntent.LatestChargeId,
                    paymentIntent.Metadata,
                    sourceLabel);

                // Sent AFTER the transaction commits, and only when this delivery is the one that
                // recorded the payment — so a Stripe retry cannot mail the customer twice.
                if (result.Recorded && result.InvoiceBecamePaid && result.InvoiceId.HasValue)
                {
                    await SendCommercialPaymentReceiptAsync(result.InvoiceId.Value);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to record commercial invoice payment for {PaymentIntentId}.", paymentIntent.Id);
            }
        }

        /// <summary>ACH authorized; Stripe is moving the money. Nothing is paid yet.</summary>
        private async Task HandleCommercialProcessing(PaymentIntent paymentIntent)
        {
            if (!Services.Commercial.StripeCommercialInvoiceMetadata
                    .IsCommercialInvoice(paymentIntent.Metadata))
                return;

            try
            {
                var commercial = HttpContext.RequestServices
                    .GetRequiredService<Services.Commercial.InvoiceStripePaymentService>();

                await commercial.HandleProcessingAsync(
                    paymentIntent.Id,
                    paymentIntent.Metadata,
                    Services.Commercial.InvoiceStripePaymentService.BuildSourceLabel(paymentIntent));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to mark commercial payment {PaymentIntentId} as processing.", paymentIntent.Id);
            }
        }

        /// <summary>
        /// The customer finished Stripe's hosted flow. For ACH this is an AUTHORIZATION, not a
        /// settlement — it records the PaymentIntent id and nothing more.
        /// </summary>
        private async Task HandleCommercialCheckoutCompleted(Stripe.Checkout.Session session)
        {
            if (!Services.Commercial.StripeCommercialInvoiceMetadata.IsCommercialInvoice(session.Metadata))
                return;

            try
            {
                var commercial = HttpContext.RequestServices
                    .GetRequiredService<Services.Commercial.InvoiceStripePaymentService>();

                await commercial.HandleCheckoutCompletedAsync(
                    session.Id, session.PaymentIntentId, session.Metadata);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to handle commercial checkout completion for session {SessionId}.", session.Id);
            }
        }

        /// <summary>The session lapsed unused. Frees the invoice for another attempt.</summary>
        private async Task HandleCommercialCheckoutExpired(Stripe.Checkout.Session session)
        {
            if (!Services.Commercial.StripeCommercialInvoiceMetadata.IsCommercialInvoice(session.Metadata))
                return;

            try
            {
                var commercial = HttpContext.RequestServices
                    .GetRequiredService<Services.Commercial.InvoiceStripePaymentService>();

                await commercial.HandleCheckoutExpiredAsync(session.Id, session.Metadata);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to expire commercial checkout session {SessionId}.", session.Id);
            }
        }

        /// <summary>
        /// Mails the "payment received" confirmation. Guarded against duplicates by the email log:
        /// Stripe retries deliveries, and a customer receiving three receipts for one payment
        /// reads as a billing system that has lost track of itself.
        /// </summary>
        private async Task SendCommercialPaymentReceiptAsync(int invoiceId)
        {
            try
            {
                var alreadySent = await _context.CommercialInvoiceEmailLogs.AnyAsync(e =>
                    e.CommercialInvoiceId == invoiceId
                    && e.EmailType == Models.Commercial.InvoiceEmailType.PaymentReceipt
                    && e.Status == Models.Commercial.InvoiceEmailStatus.Sent);

                if (alreadySent) return;

                var emails = HttpContext.RequestServices
                    .GetRequiredService<Services.Commercial.InvoiceEmailService>();

                await emails.SendPaymentReceiptAsync(invoiceId, 0);
            }
            catch (Exception ex)
            {
                // A failed receipt must never undo a recorded payment — the money is banked
                // either way, and the admin can resend from the invoice page.
                _logger.LogError(ex,
                    "Payment recorded for invoice {InvoiceId}, but the receipt email failed.", invoiceId);
            }
        }

        private async Task HandlePaymentIntentFailed(PaymentIntent paymentIntent, CancellationToken cancellationToken = default)
        {
            // Commercial ACH failures need the attempt marked so the customer can retry; the
            // residential logging below is left exactly as it was.
            if (Services.Commercial.StripeCommercialInvoiceMetadata
                    .IsCommercialInvoice(paymentIntent?.Metadata))
            {
                try
                {
                    var commercial = HttpContext.RequestServices
                        .GetRequiredService<Services.Commercial.InvoiceStripePaymentService>();

                    await commercial.HandleFailedAsync(
                        paymentIntent!.Id,
                        paymentIntent.LastPaymentError?.Code,
                        paymentIntent.LastPaymentError?.Message,
                        paymentIntent.Metadata);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Failed to mark commercial payment {PaymentIntentId} as failed.", paymentIntent?.Id);
                }

                return;
            }

            try
            {
                _logger.LogWarning("Payment failed for intent: {PaymentIntentId}", paymentIntent?.Id);
                
                if (paymentIntent?.LastPaymentError?.Message != null)
                {
                    _logger.LogWarning("Payment failure reason: {FailureReason}", paymentIntent.LastPaymentError.Message);
                }

                // You can implement additional failure handling logic here:
                // - Send notification to customer
                // - Update order status
                // - Log to audit trail
                // - Trigger retry logic
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling payment intent failed: {PaymentIntentId}", paymentIntent?.Id);
                throw; // Re-throw to be caught by the main handler
            }
        }

        private async Task HandlePaymentIntentCanceled(PaymentIntent paymentIntent, CancellationToken cancellationToken = default)
        {
            _logger.LogWarning("Payment intent {PaymentIntentId} was canceled", paymentIntent?.Id);
            // Implement cancellation handling logic
        }

        // Helper method to check for duplicate events
        private async Task<bool> IsEventAlreadyProcessed(string eventId)
        {
            try
            {
                var existingEvent = await _context.Set<WebhookEvent>()
                    .FirstOrDefaultAsync(e => e.EventId == eventId);
                
                return existingEvent?.IsProcessed == true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking if event {EventId} was already processed", eventId);
                return false; // Assume not processed if we can't check
            }
        }

        // Helper method to mark events as processed
        private async Task MarkEventAsProcessed(string eventId)
        {
            try
            {
                var webhookEvent = new WebhookEvent
                {
                    EventId = eventId,
                    EventType = "payment_intent", // You can make this more specific
                    ProcessedAt = DateTime.UtcNow,
                    IsProcessed = true
                };

                _context.Set<WebhookEvent>().Add(webhookEvent);
                await _context.SaveChangesAsync();
                
                _logger.LogInformation("Marked event {EventId} as processed", eventId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error marking event {EventId} as processed", eventId);
            }
        }

        [HttpGet("health")]
        [AllowAnonymous]
        public IActionResult HealthCheck()
        {
            try
            {
                // Check if webhook secret is configured
                var webhookSecret = _configuration["Stripe:WebhookSecret"];
                if (string.IsNullOrEmpty(webhookSecret))
                {
                    return StatusCode(503, new { status = "unhealthy", message = "Webhook secret not configured" });
                }

                // Check database connectivity
                var canConnect = _context.Database.CanConnect();
                if (!canConnect)
                {
                    return StatusCode(503, new { status = "unhealthy", message = "Database connection failed" });
                }

                return Ok(new { 
                    status = "healthy", 
                    timestamp = DateTime.UtcNow,
                    webhook_secret_configured = !string.IsNullOrEmpty(webhookSecret),
                    database_connected = canConnect
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Health check failed");
                return StatusCode(503, new { status = "unhealthy", message = "Health check failed", error = ex.Message });
            }
        }

        [HttpPost("create-payment-intent")]
        [Authorize]
        public async Task<ActionResult> CreatePaymentIntent([FromBody] CreatePaymentIntentDto dto)
        {
            try
            {
                var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

                var options = new PaymentIntentCreateOptions
                {
                    Amount = dto.Amount,
                    Currency = dto.Currency ?? "usd",
                    Metadata = dto.Metadata ?? new Dictionary<string, string>()
                };

                // Add user ID to metadata
                options.Metadata["userId"] = userId;

                var service = new PaymentIntentService();
                var paymentIntent = await service.CreateAsync(options);

                return Ok(new
                {
                    client_secret = paymentIntent.ClientSecret,
                    id = paymentIntent.Id
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = "Failed to create payment intent: " + ex.Message });
            }
        }
    }
}