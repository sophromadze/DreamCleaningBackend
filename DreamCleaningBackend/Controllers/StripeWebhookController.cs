using Stripe;
using Microsoft.AspNetCore.Mvc;
using DreamCleaningBackend.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using DreamCleaningBackend.DTOs;
using System.Text.Json;
using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Controllers
{
    [ApiController]
    [Route("api/stripewebhook")]
    public class StripeWebhookController : ControllerBase
    {
        private readonly IConfiguration _configuration;
        private readonly ApplicationDbContext _context;
        private readonly ILogger<StripeWebhookController> _logger;

        public StripeWebhookController(
            IConfiguration configuration,
            ApplicationDbContext context,
            ILogger<StripeWebhookController> logger)
        {
            _configuration = configuration;
            _context = context;
            _logger = logger;
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

                // Validate Stripe event with proper error handling
                Event stripeEvent;
                try
                {
                    stripeEvent = EventUtility.ConstructEvent(
                        json,
                        stripeSignature,
                        webhookSecret
                    );
                }
                catch (StripeException ex)
                {
                    _logger.LogError(ex, "Failed to construct Stripe event: {Message}", ex.Message);
                    return BadRequest($"Invalid webhook signature: {ex.Message}");
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
                            await HandlePaymentIntentSucceeded(paymentIntent, cts.Token);
                            break;
                        case "payment_intent.payment_failed":
                            var failedPayment = stripeEvent.Data.Object as PaymentIntent;
                            await HandlePaymentIntentFailed(failedPayment, cts.Token);
                            break;
                        case "payment_intent.canceled":
                            var canceledPayment = stripeEvent.Data.Object as PaymentIntent;
                            await HandlePaymentIntentCanceled(canceledPayment, cts.Token);
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
                _logger.LogInformation("Processing payment intent succeeded: {PaymentIntentId}", paymentIntent.Id);
                
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
                _logger.LogError(ex, "Error handling payment intent succeeded: {PaymentIntentId}", paymentIntent.Id);
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
                    order.Status = "Confirmed";

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
                var order = await _context.Orders.FindAsync(updateOrderId, cancellationToken);
                if (order != null)
                {
                    // Log the additional payment
                    _logger.LogInformation("Additional payment of ${AdditionalAmount} received for order {OrderId}", additionalAmount, updateOrderId);

                    // Mark the latest update history as paid
                    var updateHistory = await _context.OrderUpdateHistories
                        .Where(h => h.OrderId == updateOrderId && !h.IsPaid)
                        .OrderByDescending(h => h.UpdatedAt)
                        .FirstOrDefaultAsync(cancellationToken);

                    if (updateHistory != null)
                    {
                        updateHistory.PaymentIntentId = paymentIntent.Id;
                        updateHistory.IsPaid = true;
                        updateHistory.PaidAt = DateTime.UtcNow;
                        await _context.SaveChangesAsync(cancellationToken);

                        _logger.LogInformation("Marked OrderUpdateHistory {UpdateHistoryId} as paid", updateHistory.Id);
                    }
                    else
                    {
                        _logger.LogWarning("No unpaid update history found for order {OrderId}", updateOrderId);
                    }
                }
                else
                {
                    _logger.LogWarning("Order {OrderId} not found for update payment", updateOrderId);
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

        private async Task HandlePaymentIntentFailed(PaymentIntent paymentIntent, CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogWarning("Payment failed for intent: {PaymentIntentId}", paymentIntent.Id);
                
                // Log the failure reason if available
                if (!string.IsNullOrEmpty(paymentIntent.LastPaymentError?.Message))
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
                _logger.LogError(ex, "Error handling payment intent failed: {PaymentIntentId}", paymentIntent.Id);
                throw; // Re-throw to be caught by the main handler
            }
        }

        private async Task HandlePaymentIntentCanceled(PaymentIntent paymentIntent, CancellationToken cancellationToken = default)
        {
            _logger.LogWarning("Payment intent {PaymentIntentId} was canceled", paymentIntent.Id);
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