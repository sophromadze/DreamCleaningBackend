using System.Security.Claims;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models.Billing;
using DreamCleaningBackend.Services.Billing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace DreamCleaningBackend.Controllers
{
    /// <summary>
    /// The customer's Billing tab: saved cards, AutoPay, billing history, billing notices, and
    /// paying with a saved card. Replaces the one-card <c>api/card-on-file</c> endpoints (2026-09).
    ///
    /// EVERY ACTION ACTS ON THE SIGNED-IN USER ONLY. No request carries a user id, a Stripe
    /// Customer id, an amount or a paid flag — the user comes from the token, the amount from the
    /// order or invoice, and card ownership is verified against Stripe. The rollout switches
    /// (<see cref="BillingFeatures"/>) are enforced here, not just in the UI.
    /// </summary>
    [Route("api/billing")]
    [ApiController]
    [Authorize]
    public class BillingController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly BillingFeatures _features;
        private readonly IPaymentMethodService _cards;
        private readonly IPaymentAuthorizationService _authorizations;
        private readonly ISavedCardChargeService _charges;
        private readonly IBillingHistoryService _history;
        private readonly IBillingNotificationService _notifications;
        private readonly IMemoryCache _cache;
        private readonly ILogger<BillingController> _logger;

        public BillingController(
            ApplicationDbContext context,
            BillingFeatures features,
            IPaymentMethodService cards,
            IPaymentAuthorizationService authorizations,
            ISavedCardChargeService charges,
            IBillingHistoryService history,
            IBillingNotificationService notifications,
            IMemoryCache cache,
            ILogger<BillingController> logger)
        {
            _context = context;
            _features = features;
            _cards = cards;
            _authorizations = authorizations;
            _charges = charges;
            _history = history;
            _notifications = notifications;
            _cache = cache;
            _logger = logger;
        }

        /// <summary>What the frontend may draw. Anonymous: the booking page needs it for guests.</summary>
        [HttpGet("config")]
        [AllowAnonymous]
        public ActionResult<BillingConfigDto> GetConfig() => Ok(new BillingConfigDto
        {
            SavedCardsEnabled = _features.SavedCardsEnabled,
            AutoPayEnabled = _features.AutoPayEnabled
        });

        // ── Cards ─────────────────────────────────────────────────────────────────────────────

        [HttpGet("cards")]
        public async Task<ActionResult<List<SavedCardDto>>> GetCards()
        {
            if (!_features.SavedCardsEnabled) return Ok(new List<SavedCardDto>());
            // Recover anything Stripe holds for this customer that we failed to record — a card
            // saved during a payment whose save call was lost, say. Throttled and never throws,
            // so the wallet still lists if Stripe is unreachable.
            await _cards.ReconcileWithStripeAsync(UserId);
            return Ok(await _cards.ListAsync(UserId, includePaymentMethodIds: true));
        }

        /// <summary>
        /// Records the card that just paid, when the customer chose "Save Card &amp; Pay" in the
        /// pre-payment modal (2026-09). SEPARATE from payment confirmation on purpose: it cannot
        /// touch the order, the charge or any notification, and it never errors — a card that
        /// cannot be recorded is simply not recorded, and the next Billing-tab load recovers it.
        ///
        /// The choice itself is not taken from this request: <see cref="IPaymentMethodService"/>
        /// verifies Stripe's own record on the intent (setup_future_usage=off_session, succeeded,
        /// and belonging to THIS customer), so nobody can save a card by calling this endpoint.
        /// </summary>
        [HttpPost("cards/from-payment")]
        public async Task<ActionResult<SavedCardDto?>> SaveCardFromPayment([FromBody] SaveCardFromPaymentDto dto)
        {
            try
            {
                if (!_features.SavedCardsEnabled) return Ok((SavedCardDto?)null);
                return Ok(await _cards.SaveFromPaymentIntentAsync(UserId, dto?.PaymentIntentId ?? string.Empty));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Saving the card from a payment failed for user {UserId}.", UserId);
                return Ok((SavedCardDto?)null);
            }
        }

        [HttpPost("cards/setup-intent")]
        public async Task<ActionResult> CreateSetupIntent()
        {
            if (!_features.SavedCardsEnabled) return FeatureOff();
            // Card-testing bots love an endpoint that validates cards for free.
            if (!TryConsumeRate("setup", limit: 10, TimeSpan.FromHours(1)))
                return StatusCode(429, new { message = "Too many attempts to add a card. Please try again later." });

            return await Run(async () => Ok(await _cards.CreateSetupIntentAsync(UserId)));
        }

        [HttpPost("cards/complete-setup")]
        public async Task<ActionResult> CompleteSetup([FromBody] CompleteSetupIntentDto dto)
        {
            if (!_features.SavedCardsEnabled) return FeatureOff();
            return await Run(async () =>
            {
                var card = await _cards.CompleteSetupIntentAsync(UserId, dto.SetupIntentId);
                return Ok(new { card, cards = await _cards.ListAsync(UserId, true), message = "Your card was saved." });
            });
        }

        [HttpPost("cards/{cardId:int}/primary")]
        public async Task<ActionResult> SetPrimary(int cardId)
        {
            if (!_features.SavedCardsEnabled) return FeatureOff();
            return await Run(async () => Ok(await Mutation(await _cards.SetPrimaryAsync(UserId, cardId))));
        }

        [HttpPut("cards/backup")]
        public async Task<ActionResult> SetBackup([FromBody] SetBackupCardDto dto)
        {
            if (!_features.SavedCardsEnabled) return FeatureOff();
            return await Run(async () => Ok(await Mutation(await _cards.SetBackupAsync(UserId, dto.CardId))));
        }

        [HttpDelete("cards/{cardId:int}")]
        public async Task<ActionResult> RemoveCard(int cardId, [FromQuery] int? newPrimaryCardId = null)
        {
            // Removing is allowed even with the feature switched off — a customer must always be
            // able to take their card away.
            return await Run(async () => Ok(await Mutation(await _cards.RemoveAsync(UserId, cardId, newPrimaryCardId))));
        }

        // ── AutoPay ───────────────────────────────────────────────────────────────────────────

        [HttpGet("autopay")]
        public async Task<ActionResult> GetAutoPay() => await Run(async () => Ok(await _authorizations.GetOverviewAsync(UserId)));

        [HttpGet("autopay/terms")]
        public async Task<ActionResult> GetTerms([FromQuery] string scope, [FromQuery] int? seriesId,
            [FromQuery] int? clientId, [FromQuery] bool allowBackup = false) =>
            await Run(async () => Ok(await _authorizations.GetTermsAsync(UserId, scope, seriesId, clientId, allowBackup)));

        [HttpPost("autopay/enable")]
        public async Task<ActionResult> EnableAutoPay([FromBody] EnableAutoPayDto dto) =>
            await Run(async () => Ok(await _authorizations.EnableAutoPayAsync(UserId, dto, ClientIp(), UserAgent())));

        [HttpPost("autopay/disable")]
        public async Task<ActionResult> DisableAutoPay() =>
            await Run(async () => Ok(await _authorizations.DisableAutoPayAsync(UserId)));

        [HttpPost("autopay/authorizations")]
        public async Task<ActionResult> Authorize([FromBody] AuthorizeArrangementDto dto) =>
            await Run(async () => Ok(await _authorizations.AuthorizeAsync(UserId, dto, ClientIp(), UserAgent())));

        [HttpDelete("autopay/authorizations/{authorizationId:int}")]
        public async Task<ActionResult> Revoke(int authorizationId) =>
            await Run(async () => Ok(await _authorizations.RevokeAsync(UserId, authorizationId)));

        // ── History, outstanding balances, notices ────────────────────────────────────────────

        [HttpGet("history")]
        public async Task<ActionResult<BillingHistoryPageDto>> GetHistory([FromQuery] int page = 1, [FromQuery] int pageSize = 10) =>
            Ok(await _history.GetHistoryAsync(UserId, page, pageSize));

        [HttpGet("outstanding")]
        public async Task<ActionResult<List<OutstandingObligationDto>>> GetOutstanding() =>
            Ok(await _history.GetOutstandingAsync(UserId));

        [HttpGet("notifications")]
        public async Task<ActionResult<List<BillingNotificationDto>>> GetNotifications() =>
            Ok(await _notifications.ListForUserAsync(UserId));

        [HttpPost("notifications/{id:int}/read")]
        public async Task<ActionResult> MarkRead(int id)
        {
            await _notifications.MarkReadAsync(UserId, id);
            return Ok(new { });
        }

        // ── Saving the card that just paid ────────────────────────────────────────────────────

        // ── Paying with a saved card ──────────────────────────────────────────────────────────

        /// <summary>
        /// Pays one of the customer's own commercial invoices with a saved card, while they are
        /// present (3DS is completed in the browser). The amount is the invoice's balance, read
        /// server-side; the same lock, idempotency and conflict checks as AutoPay apply.
        /// </summary>
        [HttpPost("invoices/{invoiceId:int}/pay-with-saved-card")]
        public async Task<ActionResult<SavedCardChargeResponseDto>> PayInvoice(int invoiceId, [FromBody] PayWithSavedCardDto dto)
        {
            if (!_features.SavedCardsEnabled) return FeatureOff();
            if (!TryConsumeRate("pay", limit: 10, TimeSpan.FromHours(1)))
                return StatusCode(429, new { message = "Too many payment attempts. Please try again later." });

            var result = await _charges.ChargeInvoiceAsync(invoiceId, UserId, BillingAttemptTrigger.CustomerSavedCard,
                dto.CardId, UserId);
            return Ok(result);
        }

        /// <summary>After the browser finishes a 3DS challenge: settle from Stripe's own record.</summary>
        [HttpPost("payments/{paymentIntentId}/refresh")]
        public async Task<ActionResult<SavedCardChargeResponseDto>> RefreshPayment(string paymentIntentId)
        {
            if (string.IsNullOrWhiteSpace(paymentIntentId) || !paymentIntentId.StartsWith("pi_"))
                return BadRequest(new { message = "Unknown payment." });
            return Ok(await _charges.RefreshCustomerAttemptAsync(UserId, paymentIntentId));
        }

        // ── Helpers ───────────────────────────────────────────────────────────────────────────

        private int UserId
        {
            get
            {
                var raw = User.FindFirst("UserId")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                return int.TryParse(raw, out var id) ? id : 0;
            }
        }

        private async Task<CardMutationResultDto> Mutation(string message)
        {
            var autoPay = await _context.Users.AsNoTracking().Where(u => u.Id == UserId)
                .Select(u => u.AutoPayEnabled).FirstOrDefaultAsync();
            return new CardMutationResultDto
            {
                Message = message,
                Cards = await _cards.ListAsync(UserId, true),
                AutoPayEnabled = autoPay
            };
        }

        private async Task<ActionResult> Run(Func<Task<ActionResult>> action)
        {
            if (UserId == 0) return Unauthorized();
            try
            {
                return await action();
            }
            catch (BillingRuleException ex)
            {
                return BadRequest(new { message = ex.Message, code = ex.Code });
            }
        }

        private ActionResult FeatureOff() =>
            BadRequest(new { message = "Saved-card payments are not available yet.", code = "feature_off" });

        private bool TryConsumeRate(string bucket, int limit, TimeSpan window)
        {
            var key = $"billing-rate:{bucket}:{UserId}";
            var count = _cache.GetOrCreate(key, entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = window;
                return new RateCounter();
            })!;
            return Interlocked.Increment(ref count.Value) <= limit;
        }

        private sealed class RateCounter { public int Value; }

        private string? ClientIp()
        {
            var ip = Request.Headers["CF-Connecting-IP"].FirstOrDefault()
                     ?? Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim()
                     ?? HttpContext.Connection.RemoteIpAddress?.ToString();
            return ip?.Length > 45 ? ip[..45] : ip;
        }

        private string? UserAgent()
        {
            var ua = Request.Headers.UserAgent.ToString();
            return string.IsNullOrWhiteSpace(ua) ? null : ua.Length > 300 ? ua[..300] : ua;
        }
    }
}
