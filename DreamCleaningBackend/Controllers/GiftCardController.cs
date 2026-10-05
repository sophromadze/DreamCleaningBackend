// DreamCleaningBackend/Controllers/GiftCardController.cs
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
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
        private readonly ILogger<GiftCardController> _logger;

        public GiftCardController(IGiftCardService giftCardService, IEmailService emailService, IStripeService stripeService,
            ApplicationDbContext context, ILogger<GiftCardController> logger)
        {
            _giftCardService = giftCardService;
            _emailService = emailService;
            _stripeService = stripeService;
            _context = context;
            _logger = logger;
        }

        [HttpPost]
        [AllowAnonymous]
        public async Task<ActionResult<GiftCardPurchaseResponseDto>> CreateGiftCard(CreateGiftCardDto createDto)
        {
            try
            {
                // Get userId if user is authenticated, otherwise use null for anonymous purchases
                int? userId = null;
                try
                {
                    userId = GetUserId();
                }
                catch
                {
                    // User is not authenticated - allow anonymous purchase
                    userId = null;
                }

                if (createDto.SendLater)
                {
                    // "Buy for myself - send later" lives in the buyer's profile, so it needs an account.
                    if (userId == null)
                        return Unauthorized(new { message = "Please log in to buy a gift card for yourself and send it later." });

                    // Sender name/email come from the account, not from the form.
                    var buyer = await _context.Users.AsNoTracking()
                        .Where(u => u.Id == userId.Value)
                        .Select(u => new { u.FirstName, u.LastName, u.Email })
                        .FirstOrDefaultAsync();
                    if (buyer == null)
                        return Unauthorized(new { message = "Please log in to buy a gift card for yourself and send it later." });

                    createDto.SenderName = $"{buyer.FirstName} {buyer.LastName}".Trim();
                    createDto.SenderEmail = buyer.Email;
                    createDto.RecipientName = null;
                    createDto.RecipientEmail = null;
                    createDto.Message = null;
                }
                else if (string.IsNullOrWhiteSpace(createDto.RecipientName) || string.IsNullOrWhiteSpace(createDto.RecipientEmail))
                {
                    // Today's "send now" flow: the recipient is required, exactly as before.
                    return BadRequest(new { message = "Recipient name and email are required." });
                }

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
        [AllowAnonymous]
        public async Task<ActionResult> ConfirmGiftCardPayment(int giftCardId, [FromBody] ConfirmPaymentDto dto)
        {
            try
            {
                var giftCard = await _context.GiftCards.FindAsync(giftCardId);
                if (giftCard == null)
                    return NotFound(new { message = "Gift card not found" });

                // The payment must be the one created for THIS card - any other successful payment
                // must not be able to mark it paid.
                if (string.IsNullOrEmpty(dto.PaymentIntentId)
                    || !string.Equals(giftCard.PaymentIntentId, dto.PaymentIntentId, StringComparison.Ordinal))
                {
                    _logger.LogWarning("[GIFT CARD CONTROLLER] confirm-payment for gift card {GiftCardId} with a payment that does not belong to it", giftCardId);
                    return BadRequest(new { message = "Payment does not match this gift card" });
                }

                // A repeated confirm (retry, refresh, second tab) must not send the emails again.
                // Only the browser's confirm sends them - the webhook only marks the card paid - so a
                // card already paid via the webhook has not had its emails yet: "already confirmed"
                // means a previous confirm-payment got as far as sending.
                if (giftCard.IsPaid && giftCard.EmailsSentOnPurchase)
                {
                    return Ok(new
                    {
                        message = "Gift card payment already confirmed",
                        paymentIntentId = dto.PaymentIntentId,
                        alreadyConfirmed = true
                    });
                }

                // Verify payment with Stripe
                var paymentIntent = await _stripeService.GetPaymentIntentAsync(dto.PaymentIntentId);

                if (paymentIntent.Status == "succeeded")
                {
                    // Claim the email send atomically so two concurrent confirms can't both send.
                    var claimedEmails = await _context.GiftCards
                        .Where(gc => gc.Id == giftCardId && !gc.EmailsSentOnPurchase)
                        .ExecuteUpdateAsync(s => s.SetProperty(gc => gc.EmailsSentOnPurchase, true));

                    // Mark as paid
                    await _giftCardService.MarkGiftCardAsPaid(giftCardId, dto.PaymentIntentId);

                    if (claimedEmails == 0)
                    {
                        return Ok(new
                        {
                            message = "Gift card payment already confirmed",
                            paymentIntentId = dto.PaymentIntentId,
                            alreadyConfirmed = true
                        });
                    }

                    if (giftCard.IsPendingSend)
                    {
                        // "Buy for myself - send later": the buyer's receipt only. Nothing goes to a
                        // recipient until the card is sent from the profile.
                        try
                        {
                            await _emailService.SendGiftCardSenderConfirmationAsync(
                                giftCard.SenderEmail, giftCard.SenderName, null, null,
                                giftCard.Code, giftCard.OriginalAmount, null, sendLater: true);
                        }
                        catch (Exception emailEx)
                        {
                            _logger.LogError(emailEx, "[GIFT CARD CONTROLLER] Send-later receipt email failed for gift card {GiftCardId}", giftCardId);
                        }

                        return Ok(new
                        {
                            message = "Gift card payment processed successfully. The gift card is saved in your profile.",
                            paymentIntentId = dto.PaymentIntentId,
                            sendLater = true
                        });
                    }

                    // Get the updated gift card details to send the email
                    // For anonymous purchases, get gift card directly from context
                    var updatedGiftCard = await _context.GiftCards
                        .Where(gc => gc.Id == giftCardId)
                        .Select(gc => new
                        {
                            gc.RecipientEmail,
                            gc.RecipientName,
                            gc.SenderName,
                            gc.Code,
                            gc.OriginalAmount,
                            gc.Message,
                            gc.SenderEmail
                        })
                        .FirstOrDefaultAsync();

                    if (updatedGiftCard != null)
                    {
                        _logger.LogInformation($"[GIFT CARD CONTROLLER] Payment confirmed for gift card {giftCardId}. Sending emails to recipient: {updatedGiftCard.RecipientEmail} and sender: {updatedGiftCard.SenderEmail}");
                        
                        // Send email notification to recipient
                        _logger.LogInformation($"[GIFT CARD CONTROLLER] Sending notification email to recipient: {updatedGiftCard.RecipientEmail}");
                        await _emailService.SendGiftCardNotificationAsync(
                            updatedGiftCard.RecipientEmail ?? "",
                            updatedGiftCard.RecipientName ?? "",
                            updatedGiftCard.SenderName,
                            updatedGiftCard.Code,
                            updatedGiftCard.OriginalAmount,
                            updatedGiftCard.Message ?? "",
                            updatedGiftCard.SenderEmail
                        );
                        _logger.LogInformation($"[GIFT CARD CONTROLLER] Recipient email sent successfully");

                        // ADDED: Send confirmation email to sender
                        _logger.LogInformation($"[GIFT CARD CONTROLLER] Sending confirmation email to sender: {updatedGiftCard.SenderEmail}");
                        await _emailService.SendGiftCardSenderConfirmationAsync(
                            updatedGiftCard.SenderEmail,
                            updatedGiftCard.SenderName,
                            updatedGiftCard.RecipientName,
                            updatedGiftCard.RecipientEmail,
                            // The card now belongs to the recipient: the buyer sees the last 4 only.
                            GiftCardService.MaskCode(updatedGiftCard.Code),
                            updatedGiftCard.OriginalAmount,
                            updatedGiftCard.Message ?? ""
                        );
                        _logger.LogInformation($"[GIFT CARD CONTROLLER] Sender confirmation email sent successfully");

                        return Ok(new
                        {
                            message = "Gift card payment processed successfully and emails sent to both recipient and sender",
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
                            warning = "Email notifications could not be sent"
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

        // ---- Profile -> Gift Cards. Every action is scoped to the token's user in GiftCardService:
        // another user's card id answers 404, exactly like an id that does not exist. ----

        public const string SendRateLimitPolicy = "gift-card-send";

        [HttpGet("mine")]
        public async Task<ActionResult<List<MyGiftCardDto>>> GetMyGiftCards()
        {
            try
            {
                var userId = GetUserId();
                return Ok(await _giftCardService.GetMyGiftCards(userId));
            }
            catch (UnauthorizedAccessException)
            {
                return Unauthorized();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load the user's gift cards");
                return BadRequest(new { message = "Failed to load your gift cards." });
            }
        }

        [HttpPost("mine/{id:int}/send")]
        [EnableRateLimiting(SendRateLimitPolicy)]
        public async Task<ActionResult<MyGiftCardDto>> SendMyGiftCard(int id, [FromBody] SendMyGiftCardDto dto)
        {
            int userId;
            try { userId = GetUserId(); }
            catch (UnauthorizedAccessException) { return Unauthorized(); }

            var result = await _giftCardService.SendMyGiftCard(id, userId, dto);
            return ToActionResult(result);
        }

        [HttpPost("mine/{id:int}/resend")]
        [EnableRateLimiting(SendRateLimitPolicy)]
        public async Task<ActionResult<MyGiftCardDto>> ResendMyGiftCard(int id)
        {
            int userId;
            try { userId = GetUserId(); }
            catch (UnauthorizedAccessException) { return Unauthorized(); }

            var result = await _giftCardService.ResendMyGiftCard(id, userId);
            return ToActionResult(result);
        }

        private ActionResult ToActionResult(GiftCardSendResult result) => result.Kind switch
        {
            GiftCardSendResult.ResultKind.Ok => Ok(result.Card),
            GiftCardSendResult.ResultKind.NotFound => NotFound(new { message = result.Message }),
            GiftCardSendResult.ResultKind.TooMany => StatusCode(StatusCodes.Status429TooManyRequests, new { message = result.Message }),
            // The card IS sent (recipient saved) but the email bounced - 502 with the updated card.
            GiftCardSendResult.ResultKind.EmailFailed => StatusCode(StatusCodes.Status502BadGateway, new { message = result.Message, card = result.Card }),
            GiftCardSendResult.ResultKind.Failed => StatusCode(StatusCodes.Status502BadGateway, new { message = result.Message }),
            _ => BadRequest(new { message = result.Message })
        };

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