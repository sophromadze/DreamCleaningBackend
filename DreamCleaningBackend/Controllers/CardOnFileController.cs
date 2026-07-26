using System.Security.Claims;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Controllers
{
    /// <summary>
    /// Customer self-service for their card on file: view brand/last4, save/replace via the
    /// SetupIntent flow, remove. Saving is all this controller does — charging the card always
    /// happens through the normal payment endpoints on an explicit customer/admin action.
    /// Response messages are plain language (shown verbatim in the UI).
    /// </summary>
    [Route("api/card-on-file")]
    [ApiController]
    [Authorize]
    public class CardOnFileController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IStripeService _stripeService;
        private readonly ICardOnFileService _cardOnFileService;
        private readonly ILogger<CardOnFileController> _logger;

        public CardOnFileController(
            ApplicationDbContext context,
            IStripeService stripeService,
            ICardOnFileService cardOnFileService,
            ILogger<CardOnFileController> logger)
        {
            _context = context;
            _stripeService = stripeService;
            _cardOnFileService = cardOnFileService;
            _logger = logger;
        }

        /// <summary>The caller's saved card, or { card: null }. The pm id is included because
        /// the owner's browser needs it to pay with the saved card.</summary>
        [HttpGet("saved-card")]
        public async Task<ActionResult> GetSavedCard()
        {
            var card = await _cardOnFileService.GetSavedCardAsync(GetUserId());
            return Ok(new { card });
        }

        /// <summary>Starts the save/replace-card flow (profile page). Returns the client secret
        /// the frontend uses with Stripe Elements to collect the card — no charge happens.</summary>
        [HttpPost("setup-intent")]
        public async Task<ActionResult> CreateSetupIntent()
        {
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == GetUserId());
            if (user == null) return Unauthorized();

            try
            {
                await _stripeService.CreateOrGetCustomerAsync(user);
                await _context.SaveChangesAsync(); // persist a freshly created StripeCustomerId

                var setupIntent = await _stripeService.CreateSetupIntentAsync(user.StripeCustomerId!);
                return Ok(new { clientSecret = setupIntent.ClientSecret });
            }
            catch (ApplicationException)
            {
                return BadRequest(new { message = "We couldn't start saving your card. Please try again in a moment." });
            }
        }

        /// <summary>Finishes the save/replace flow with the pm id the confirmed SetupIntent produced.</summary>
        [HttpPut("saved-card")]
        public async Task<ActionResult> SaveCard([FromBody] SaveCardDto dto)
        {
            try
            {
                var card = await _cardOnFileService.SaveCardFromSetupAsync(GetUserId(), dto.PaymentMethodId);
                return Ok(new { card, message = "Your card is saved." });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpDelete("saved-card")]
        public async Task<ActionResult> RemoveCard()
        {
            await _cardOnFileService.RemoveSavedCardAsync(GetUserId());
            return Ok(new { message = "Your card has been removed." });
        }

        private int GetUserId()
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                throw new Exception("Invalid user");
            return userId;
        }
    }
}
