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
                                await _context.SaveChangesAsync();
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