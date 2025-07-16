using Stripe;
using Microsoft.AspNetCore.Mvc;
using DreamCleaningBackend.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using DreamCleaningBackend.DTOs;

namespace DreamCleaningBackend.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
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
        public async Task<IActionResult> Handle()
        {
            var json = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync();

            try
            {
                var stripeEvent = EventUtility.ConstructEvent(
                    json,
                    Request.Headers["Stripe-Signature"],
                    _configuration["Stripe:WebhookSecret"]
                );

                // Handle the event - Using string constants instead of Events class
                switch (stripeEvent.Type)
                {
                    case "payment_intent.succeeded":
                        var paymentIntent = stripeEvent.Data.Object as PaymentIntent;
                        await HandlePaymentIntentSucceeded(paymentIntent);
                        break;
                    case "payment_intent.payment_failed":
                        var failedPayment = stripeEvent.Data.Object as PaymentIntent;
                        await HandlePaymentIntentFailed(failedPayment);
                        break;
                    default:
                        _logger.LogInformation($"Unhandled event type: {stripeEvent.Type}");
                        break;
                }

                return Ok();
            }
            catch (StripeException e)
            {
                _logger.LogError(e, "Stripe webhook error");
                return BadRequest();
            }
        }

        private async Task HandlePaymentIntentSucceeded(PaymentIntent paymentIntent)
        {
            var metadata = paymentIntent.Metadata;

            if (metadata.TryGetValue("type", out var type))
            {
                switch (type)
                {
                    case "booking":
                        if (metadata.TryGetValue("orderId", out var orderIdStr) &&
                            int.TryParse(orderIdStr, out var orderId))
                        {
                            var order = await _context.Orders.FindAsync(orderId);
                            if (order != null && !order.IsPaid)
                            {
                                order.IsPaid = true;
                                order.PaidAt = DateTime.Now;
                                order.Status = "Confirmed";

                                // Set initial values when order is first paid
                                // Remove the check for InitialTotal == 0 to handle edge cases
                                if (order.InitialSubTotal == 0 && order.InitialTax == 0 && order.InitialTotal == 0)
                                {
                                    order.InitialSubTotal = order.SubTotal;
                                    order.InitialTax = order.Tax;
                                    order.InitialTips = order.Tips;
                                    order.InitialCompanyDevelopmentTips = order.CompanyDevelopmentTips;
                                    order.InitialTotal = order.Total;

                                    _logger.LogInformation($"Set initial pricing for order {orderId}: " +
                                        $"SubTotal=${order.InitialSubTotal}, Tax=${order.InitialTax}, " +
                                        $"Tips=${order.InitialTips}, CompanyTips=${order.InitialCompanyDevelopmentTips}, " +
                                        $"Total=${order.InitialTotal}");
                                }

                                await _context.SaveChangesAsync();
                            }
                        }
                        break;

                    case "order_update":
                        if (metadata.TryGetValue("orderId", out var updateOrderIdStr) &&
                            int.TryParse(updateOrderIdStr, out var updateOrderId) &&
                            metadata.TryGetValue("additionalAmount", out var additionalAmountStr) &&
                            decimal.TryParse(additionalAmountStr, out var additionalAmount))
                        {
                            var order = await _context.Orders.FindAsync(updateOrderId);
                            if (order != null)
                            {
                                // Log the additional payment
                                _logger.LogInformation($"Additional payment of ${additionalAmount} received for order {updateOrderId}");

                                // ADD THIS - Mark the latest update history as paid
                                var updateHistory = await _context.OrderUpdateHistories
                                    .Where(h => h.OrderId == updateOrderId && !h.IsPaid)
                                    .OrderByDescending(h => h.UpdatedAt)
                                    .FirstOrDefaultAsync();

                                if (updateHistory != null)
                                {
                                    updateHistory.PaymentIntentId = paymentIntent.Id;
                                    updateHistory.IsPaid = true;
                                    updateHistory.PaidAt = DateTime.Now;
                                    await _context.SaveChangesAsync();

                                    _logger.LogInformation($"Marked OrderUpdateHistory {updateHistory.Id} as paid");
                                }
                            }
                        }
                        break;
                    case "gift_card":
                        if (metadata.TryGetValue("giftCardId", out var giftCardIdStr) &&
                            int.TryParse(giftCardIdStr, out var giftCardId))
                        {
                            var giftCard = await _context.GiftCards.FindAsync(giftCardId);
                            if (giftCard != null && !giftCard.IsPaid)
                            {
                                giftCard.IsPaid = true;
                                giftCard.PaidAt = DateTime.Now;
                                await _context.SaveChangesAsync();
                            }
                        }
                        break;
                }
            }
        }

        private async Task HandlePaymentIntentFailed(PaymentIntent paymentIntent)
        {
            _logger.LogWarning($"Payment failed for intent: {paymentIntent.Id}");
            // Implement failure handling logic
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