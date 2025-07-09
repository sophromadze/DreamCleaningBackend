// DreamCleaningBackend/Controllers/GiftCardController.cs
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Services.Interfaces;
using DreamCleaningBackend.Services;
using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Data;

namespace DreamCleaningBackend.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class GiftCardController : ControllerBase
    {
        private readonly IGiftCardService _giftCardService;
        private readonly IEmailService _emailService;
        private readonly IStripeService _stripeService;
        private readonly ApplicationDbContext _context;

        public GiftCardController(IGiftCardService giftCardService, IEmailService emailService, IStripeService stripeService,
            ApplicationDbContext context)
        {
            _giftCardService = giftCardService;
            _emailService = emailService;
            _stripeService = stripeService;
            _context = context;
        }

        [HttpPost]
        public async Task<ActionResult<GiftCardPurchaseResponseDto>> CreateGiftCard(CreateGiftCardDto createDto)
        {
            try
            {
                var userId = GetUserId();
                var giftCard = await _giftCardService.CreateGiftCard(userId, createDto);

                // Create Stripe payment intent
                var metadata = new Dictionary<string, string>
                {
                    { "giftCardId", giftCard.Id.ToString() },
                    { "userId", userId.ToString() },
                    { "type", "gift_card" }
                };

                var paymentIntent = await _stripeService.CreatePaymentIntentAsync(createDto.Amount, metadata);

                // Update gift card with payment intent ID
                giftCard.PaymentIntentId = paymentIntent.Id;
                await _context.SaveChangesAsync();

                return Ok(new GiftCardPurchaseResponseDto
                {
                    GiftCardId = giftCard.Id,
                    Code = giftCard.Code,
                    Amount = giftCard.OriginalAmount,
                    Status = "pending_payment",
                    PaymentIntentId = paymentIntent.Id,
                    PaymentClientSecret = paymentIntent.ClientSecret
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = "Failed to create gift card: " + ex.Message });
            }
        }

        [HttpPost("confirm-payment/{giftCardId}")]
        public async Task<ActionResult> ConfirmGiftCardPayment(int giftCardId, [FromBody] ConfirmPaymentDto dto)
        {
            try
            {
                var giftCard = await _context.GiftCards.FindAsync(giftCardId);
                if (giftCard == null)
                    return NotFound(new { message = "Gift card not found" });

                // Verify payment with Stripe
                var paymentIntent = await _stripeService.GetPaymentIntentAsync(dto.PaymentIntentId);

                if (paymentIntent.Status == "succeeded")
                {
                    // Mark as paid
                    await _giftCardService.MarkGiftCardAsPaid(giftCardId, dto.PaymentIntentId);

                    // Get the updated gift card details to send the email
                    var userId = GetUserId();
                    var giftCards = await _giftCardService.GetUserGiftCards(userId);
                    var updatedGiftCard = giftCards.FirstOrDefault(gc => gc.Id == giftCardId);

                    if (updatedGiftCard != null)
                    {
                        // Send email notification to recipient
                        await _emailService.SendGiftCardNotificationAsync(
                            updatedGiftCard.RecipientEmail,
                            updatedGiftCard.RecipientName,
                            updatedGiftCard.SenderName,
                            updatedGiftCard.Code,
                            updatedGiftCard.OriginalAmount,
                            updatedGiftCard.Message,
                            updatedGiftCard.SenderEmail
                        );

                        return Ok(new
                        {
                            message = "Gift card payment processed successfully and email sent to recipient",
                            paymentIntentId = dto.PaymentIntentId
                        });
                    }
                    else
                    {
                        // Payment successful but couldn't send email (still return success)
                        return Ok(new
                        {
                            message = "Gift card payment processed successfully",
                            paymentIntentId = dto.PaymentIntentId,
                            warning = "Email notification could not be sent"
                        });
                    }
                }
                else
                {
                    return BadRequest(new { message = "Payment not completed" });
                }
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = "Failed to confirm payment: " + ex.Message });
            }
        }

        [HttpPost("validate")]
        public async Task<ActionResult<GiftCardValidationDto>> ValidateGiftCard(ApplyGiftCardDto applyDto)
        {
            try
            {
                var validation = await _giftCardService.ValidateGiftCard(applyDto.Code);
                return Ok(validation);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = "Failed to validate gift card: " + ex.Message });
            }
        }

        [HttpGet]
        public async Task<ActionResult<List<GiftCardDto>>> GetUserGiftCards()
        {
            try
            {
                var userId = GetUserId();
                var giftCards = await _giftCardService.GetUserGiftCards(userId);
                return Ok(giftCards);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = "Failed to get gift cards: " + ex.Message });
            }
        }

        [HttpGet("{code}/usage-history")]
        public async Task<ActionResult<List<GiftCardUsageDto>>> GetGiftCardUsageHistory(string code)
        {
            try
            {
                var userId = GetUserId();
                var usages = await _giftCardService.GetGiftCardUsageHistory(code, userId);
                return Ok(usages);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = "Failed to get usage history: " + ex.Message });
            }
        }

        private int GetUserId()
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
            {
                throw new UnauthorizedAccessException("User ID not found in token");
            }
            return userId;
        }
    }
}