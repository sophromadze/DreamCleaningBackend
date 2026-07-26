using DreamCleaningBackend.Data;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services
{
    public class CardOnFileService : ICardOnFileService
    {
        private readonly ApplicationDbContext _context;
        private readonly IStripeService _stripeService;
        private readonly ILogger<CardOnFileService> _logger;

        public CardOnFileService(
            ApplicationDbContext context,
            IStripeService stripeService,
            ILogger<CardOnFileService> logger)
        {
            _context = context;
            _stripeService = stripeService;
            _logger = logger;
        }

        public async Task<SavedCardDto?> GetSavedCardAsync(int userId)
        {
            var user = await _context.Users
                .AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => new { u.DefaultPaymentMethodId, u.SavedCardBrand, u.SavedCardLast4 })
                .FirstOrDefaultAsync();

            if (user == null || string.IsNullOrEmpty(user.DefaultPaymentMethodId))
                return null;

            return new SavedCardDto
            {
                PaymentMethodId = user.DefaultPaymentMethodId,
                Brand = user.SavedCardBrand,
                Last4 = user.SavedCardLast4
            };
        }

        public async Task<bool> TrySaveCardFromPaymentIntentAsync(int userId, string paymentIntentId)
        {
            try
            {
                var paymentIntent = await _stripeService.GetPaymentIntentAsync(paymentIntentId);
                if (string.IsNullOrEmpty(paymentIntent.PaymentMethodId))
                {
                    _logger.LogWarning("Card on file: payment intent {PaymentIntentId} has no payment method — nothing saved.", paymentIntentId);
                    return false;
                }

                var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);
                if (user == null) return false;

                // Self-heal: prepare-payment normally stamped StripeCustomerId, but if this
                // intent carries a customer the user row lacks, trust the intent. Without a
                // Customer the card wasn't actually saved on Stripe's side — don't pretend.
                if (string.IsNullOrEmpty(user.StripeCustomerId) && !string.IsNullOrEmpty(paymentIntent.CustomerId))
                    user.StripeCustomerId = paymentIntent.CustomerId;
                if (string.IsNullOrEmpty(user.StripeCustomerId))
                {
                    _logger.LogWarning("Card on file: no Stripe customer for user {UserId} — card not saved.", userId);
                    return false;
                }

                await PersistCardAsync(user, paymentIntent.PaymentMethodId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Card on file: failed to save card from payment intent {PaymentIntentId} for user {UserId}.",
                    paymentIntentId, userId);
                return false;
            }
        }

        public async Task<SavedCardDto> SaveCardFromSetupAsync(int userId, string paymentMethodId)
        {
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId)
                ?? throw new InvalidOperationException("We couldn't find your account. Please try again.");

            if (string.IsNullOrEmpty(user.StripeCustomerId))
                throw new InvalidOperationException("We couldn't find your saved card profile. Please try again.");

            Stripe.PaymentMethod paymentMethod;
            try
            {
                paymentMethod = await _stripeService.GetPaymentMethodAsync(paymentMethodId);
            }
            catch (ApplicationException)
            {
                throw new InvalidOperationException("We couldn't verify the card. Please try again.");
            }

            // The card must belong to THIS user's payment profile (it was attached to their
            // Customer by the SetupIntent) — otherwise anyone could save an arbitrary pm id.
            if (paymentMethod.CustomerId != user.StripeCustomerId)
                throw new InvalidOperationException("We couldn't verify the card. Please try again.");

            await PersistCardAsync(user, paymentMethod.Id, paymentMethod);

            return new SavedCardDto
            {
                PaymentMethodId = user.DefaultPaymentMethodId!,
                Brand = user.SavedCardBrand,
                Last4 = user.SavedCardLast4
            };
        }

        public async Task RemoveSavedCardAsync(int userId)
        {
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null || string.IsNullOrEmpty(user.DefaultPaymentMethodId))
                return;

            // Detach in Stripe first (best-effort inside DetachPaymentMethodAsync — a card
            // already gone on Stripe's side must not block clearing our columns).
            await _stripeService.DetachPaymentMethodAsync(user.DefaultPaymentMethodId);

            user.DefaultPaymentMethodId = null;
            user.SavedCardBrand = null;
            user.SavedCardLast4 = null;
            user.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
        }

        /// <summary>Persists the pm id + display copies on the tracked user and detaches the
        /// card being replaced (if different). Loads card details when not supplied.</summary>
        private async Task PersistCardAsync(Models.User user, string paymentMethodId, Stripe.PaymentMethod? paymentMethod = null)
        {
            if (paymentMethod == null)
            {
                try
                {
                    paymentMethod = await _stripeService.GetPaymentMethodAsync(paymentMethodId);
                }
                catch (ApplicationException ex)
                {
                    // Display copies are a nicety; the pm id is what matters.
                    _logger.LogWarning(ex, "Card on file: could not read card details for user {UserId}.", user.Id);
                }
            }

            var previousPaymentMethodId = user.DefaultPaymentMethodId;

            user.DefaultPaymentMethodId = paymentMethodId;
            user.SavedCardBrand = paymentMethod?.Card?.Brand;
            user.SavedCardLast4 = paymentMethod?.Card?.Last4;
            user.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            if (!string.IsNullOrEmpty(previousPaymentMethodId) && previousPaymentMethodId != paymentMethodId)
                await _stripeService.DetachPaymentMethodAsync(previousPaymentMethodId);
        }
    }
}
