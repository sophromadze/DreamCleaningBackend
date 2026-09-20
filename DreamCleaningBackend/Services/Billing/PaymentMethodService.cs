using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Models.Billing;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services.Billing
{
    /// <summary>A rule the customer can act on. Mapped to 400 with the message shown verbatim.</summary>
    public class BillingRuleException : Exception
    {
        public BillingRuleException(string message, string? code = null) : base(message) { Code = code; }

        /// <summary>Stable machine-readable reason for the frontend (e.g. "choose_new_primary").</summary>
        public string? Code { get; }
    }

    public interface IPaymentMethodService
    {
        Task<List<SavedCardDto>> ListAsync(int userId, bool includePaymentMethodIds);
        Task<SetupIntentResponseDto> CreateSetupIntentAsync(int userId);
        Task<SavedCardDto> CompleteSetupIntentAsync(int userId, string setupIntentId);
        Task<SavedCardDto?> SaveFromPaymentIntentAsync(int userId, string paymentIntentId);
        Task<string> SetPrimaryAsync(int userId, int cardId);
        Task<string> SetBackupAsync(int userId, int? cardId);
        Task<string> RemoveAsync(int userId, int cardId, int? newPrimaryCardId);
        Task HandleDetachedAtStripeAsync(string stripePaymentMethodId);
        Task RefreshFromStripeAsync(string stripePaymentMethodId);
        Task RecordChargeFailureAsync(int cardId, string? failureCode, string? declineCode);
        Task RecordChargeSuccessAsync(int cardId);
        Task<CustomerPaymentMethod?> GetUsableCardAsync(int userId, int? cardId);

        /// <summary>
        /// Brings this customer's wallet back in step with Stripe (2026-09): recovers a card Stripe
        /// holds that we failed to record, and finishes a detach that never completed. Throttled,
        /// idempotent, and it never throws — a reconciliation problem must not break the page that
        /// triggered it. Returns how many cards were recovered.
        /// </summary>
        Task<int> ReconcileWithStripeAsync(int userId, bool force = false);
    }

    /// <summary>
    /// THE ONLY WRITER of <see cref="CustomerPaymentMethod"/> rows and of the Primary/Backup
    /// pointers on <see cref="User"/> (plus the legacy one-card mirror columns).
    ///
    /// ══ INVARIANTS, AND WHAT HOLDS EACH ══
    ///  • At most one Primary / one Backup — they are single columns, so by construction.
    ///  • Primary ≠ Backup, and no Backup without a Primary — CHECK constraints in the database,
    ///    so even two racing requests cannot commit a violation; the loser gets a 409.
    ///  • A Primary whenever a usable card exists — maintained here: the first card saved becomes
    ///    Primary, and removing the Primary requires naming its replacement.
    ///  • Card mutations for one customer are SERIALISED by a row lock on their Users row, so
    ///    "remove card A" and "make A Backup" cannot interleave.
    ///
    /// Saving a card NEVER turns AutoPay on. Removing the last usable card turns it OFF, because
    /// an AutoPay configuration pointing at nothing would be exactly the contradictory state the
    /// Billing tab promises never to show.
    /// </summary>
    public class PaymentMethodService : IPaymentMethodService
    {
        private readonly ApplicationDbContext _context;
        private readonly IStripeService _stripe;
        private readonly IAuditService _audit;
        private readonly IBillingNotificationService _notifications;
        private readonly ILogger<PaymentMethodService> _logger;

        /// <summary>Codes after which the issuer asks never to try the card again.</summary>
        public static readonly HashSet<string> NeverRetryDeclineCodes = new(StringComparer.OrdinalIgnoreCase)
        {
            "lost_card", "stolen_card", "pickup_card", "fraudulent", "restricted_card",
            "revocation_of_authorization", "revocation_of_all_authorizations", "stop_payment_order",
            "security_violation", "invalid_account", "card_not_supported", "incorrect_number"
        };

        public PaymentMethodService(
            ApplicationDbContext context,
            IStripeService stripe,
            IAuditService audit,
            IBillingNotificationService notifications,
            ILogger<PaymentMethodService> logger)
        {
            _context = context;
            _stripe = stripe;
            _audit = audit;
            _notifications = notifications;
            _logger = logger;
        }

        // ── Reading ────────────────────────────────────────────────────────────────────────────

        public async Task<List<SavedCardDto>> ListAsync(int userId, bool includePaymentMethodIds)
        {
            var roles = await _context.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => new { u.PrimaryPaymentMethodId, u.BackupPaymentMethodId })
                .FirstOrDefaultAsync();
            if (roles == null) return new List<SavedCardDto>();

            var cards = await _context.CustomerPaymentMethods
                .Where(c => c.UserId == userId && c.Status != CustomerPaymentMethodStatus.Removed)
                .OrderByDescending(c => c.Id == roles.PrimaryPaymentMethodId)
                .ThenByDescending(c => c.Id == roles.BackupPaymentMethodId)
                .ThenByDescending(c => c.CreatedAt)
                .ToListAsync();

            await BackfillMissingExpiryAsync(cards);

            return cards.Select(c => ToDto(c, roles.PrimaryPaymentMethodId, roles.BackupPaymentMethodId, includePaymentMethodIds))
                .ToList();
        }

        public static SavedCardDto ToDto(CustomerPaymentMethod card, int? primaryId, int? backupId, bool includePaymentMethodId)
        {
            var expired = card.IsExpiredAt(NyTimeHelper.NowNy);
            return new SavedCardDto
            {
                Id = card.Id,
                PaymentMethodId = includePaymentMethodId ? card.StripePaymentMethodId : null,
                Brand = card.Brand,
                Last4 = card.Last4,
                ExpMonth = card.ExpMonth,
                ExpYear = card.ExpYear,
                Wallet = card.Wallet,
                IsPrimary = primaryId == card.Id,
                IsBackup = backupId == card.Id,
                IsExpired = expired,
                Status = card.Status == CustomerPaymentMethodStatus.Blocked ? "blocked" : "active",
                StatusMessage = card.Status == CustomerPaymentMethodStatus.Blocked
                    ? "Your bank declined this card and asked us not to try it again. Please add another card."
                    : expired ? "This card has expired." : null,
                IsUsable = card.Status == CustomerPaymentMethodStatus.Active && !expired,
                CreatedAt = card.CreatedAt
            };
        }

        /// <summary>A card of this customer that may be charged now, or null. Null id = the Primary.</summary>
        public async Task<CustomerPaymentMethod?> GetUsableCardAsync(int userId, int? cardId)
        {
            var id = cardId ?? await _context.Users.Where(u => u.Id == userId)
                .Select(u => u.PrimaryPaymentMethodId).FirstOrDefaultAsync();
            if (id == null) return null;

            var card = await _context.CustomerPaymentMethods
                .FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId);
            if (card == null || card.Status != CustomerPaymentMethodStatus.Active) return null;
            return card.IsExpiredAt(NyTimeHelper.NowNy) ? null : card;
        }

        // ── Adding a card ──────────────────────────────────────────────────────────────────────

        public async Task<SetupIntentResponseDto> CreateSetupIntentAsync(int userId)
        {
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId)
                ?? throw new BillingRuleException("We couldn't find your account. Please sign in again.");

            try
            {
                await _stripe.CreateOrGetCustomerAsync(user);
                await _context.SaveChangesAsync(); // persist a freshly created StripeCustomerId

                var setupIntent = await _stripe.CreateSetupIntentAsync(user.StripeCustomerId!,
                    new Dictionary<string, string> { ["userId"] = userId.ToString(), ["purpose"] = "billing_add_card" });

                return new SetupIntentResponseDto { ClientSecret = setupIntent.ClientSecret, SetupIntentId = setupIntent.Id };
            }
            catch (ApplicationException ex)
            {
                _logger.LogWarning(ex, "Could not start a card setup for user {UserId}.", userId);
                throw new BillingRuleException("We couldn't start adding your card. Please try again in a moment.");
            }
        }

        /// <summary>
        /// Records the card a confirmed SetupIntent attached. The SETUP INTENT is what is trusted,
        /// read back from Stripe — never a pm id the browser names — and it must belong to this
        /// customer's Stripe Customer and have succeeded. Idempotent: completing the same setup
        /// twice returns the one card.
        /// </summary>
        public async Task<SavedCardDto> CompleteSetupIntentAsync(int userId, string setupIntentId)
        {
            if (string.IsNullOrWhiteSpace(setupIntentId) || !setupIntentId.StartsWith("seti_"))
                throw new BillingRuleException("We couldn't verify the card. Please try again.");

            var user = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId)
                ?? throw new BillingRuleException("We couldn't find your account. Please sign in again.");

            Stripe.SetupIntent setupIntent;
            try
            {
                setupIntent = await _stripe.GetSetupIntentAsync(setupIntentId);
            }
            catch (ApplicationException)
            {
                throw new BillingRuleException("We couldn't verify the card. Please try again.");
            }

            if (string.IsNullOrEmpty(user.StripeCustomerId) || setupIntent.CustomerId != user.StripeCustomerId)
                throw new BillingRuleException("We couldn't verify the card. Please try again.");

            if (setupIntent.Status != "succeeded" || string.IsNullOrEmpty(setupIntent.PaymentMethodId))
                throw new BillingRuleException("The card wasn't confirmed by your bank. Please try again.");

            var card = await UpsertAsync(userId, user.StripeCustomerId, setupIntent.PaymentMethodId, "setup_intent");
            return (await ListAsync(userId, true)).First(c => c.Id == card.Id);
        }

        /// <summary>
        /// Records the card that paid a PaymentIntent — ONLY when the customer ticked "save this
        /// card" before paying, which is the one case Stripe attaches it (setup_future_usage).
        /// Everything is verified against Stripe: the intent succeeded, belongs to this customer,
        /// carried setup_future_usage, and its card is attached to this customer. Never throws;
        /// returns null when there is nothing to save. Idempotent.
        /// </summary>
        public async Task<SavedCardDto?> SaveFromPaymentIntentAsync(int userId, string paymentIntentId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(paymentIntentId) || !paymentIntentId.StartsWith("pi_")) return null;

                var user = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
                if (user == null) return null;

                var intent = await _stripe.GetPaymentIntentAsync(paymentIntentId);
                if (intent.Status is not ("succeeded" or "processing")) return null;
                if (string.IsNullOrEmpty(intent.PaymentMethodId) || string.IsNullOrEmpty(intent.CustomerId)) return null;
                if (!string.Equals(intent.SetupFutureUsage, "off_session", StringComparison.OrdinalIgnoreCase)) return null;

                // The intent must be THIS customer's. A Customer the user row does not know about
                // is adopted only when the row has none yet (a card saved during the very first
                // booking, where prepare-payment created the Customer a moment earlier).
                var customerId = user.StripeCustomerId;
                if (string.IsNullOrEmpty(customerId))
                {
                    var tracked = await _context.Users.FirstAsync(u => u.Id == userId);
                    if (!string.IsNullOrEmpty(tracked.StripeCustomerId)) return null;
                    var paymentMethodForAdoption = await _stripe.GetPaymentMethodAsync(intent.PaymentMethodId);
                    if (paymentMethodForAdoption.CustomerId != intent.CustomerId) return null;
                    tracked.StripeCustomerId = intent.CustomerId;
                    await _context.SaveChangesAsync();
                    customerId = intent.CustomerId;
                }

                if (intent.CustomerId != customerId) return null;

                var card = await UpsertAsync(userId, customerId, intent.PaymentMethodId, "checkout_payment");
                return (await ListAsync(userId, true)).FirstOrDefault(c => c.Id == card.Id);
            }
            catch (Exception ex)
            {
                // A card-save problem must never surface as a payment problem.
                _logger.LogError(ex, "Saving the card from payment {PaymentIntentId} for user {UserId} failed.",
                    paymentIntentId, userId);
                return null;
            }
        }

        private async Task<CustomerPaymentMethod> UpsertAsync(int userId, string stripeCustomerId, string stripePaymentMethodId, string source)
        {
            // Read card details BEFORE taking the lock — a Stripe round-trip inside a row lock
            // would hold every other card action for this customer hostage to the network.
            var paymentMethod = await _stripe.GetPaymentMethodAsync(stripePaymentMethodId);
            if (paymentMethod.CustomerId != stripeCustomerId)
                throw new BillingRuleException("We couldn't verify the card. Please try again.");
            if (!string.Equals(paymentMethod.Type, "card", StringComparison.OrdinalIgnoreCase))
                throw new BillingRuleException("Only cards can be saved.");

            await using var transaction = await BeginUserLockAsync(userId);

            var existing = await _context.CustomerPaymentMethods
                .FirstOrDefaultAsync(c => c.StripePaymentMethodId == stripePaymentMethodId);

            if (existing != null && existing.UserId != userId)
            {
                // A pm id belongs to exactly one Stripe Customer, and we verified it is this
                // user's; another user's row naming it would be a data error. Refuse loudly.
                _logger.LogError("Payment method {PaymentMethodId} is recorded for user {OtherUserId} but attached to user {UserId}'s customer.",
                    stripePaymentMethodId, existing.UserId, userId);
                throw new BillingRuleException("We couldn't verify the card. Please try again.");
            }

            var card = existing ?? new CustomerPaymentMethod
            {
                UserId = userId,
                StripePaymentMethodId = stripePaymentMethodId,
                CreatedAt = DateTime.UtcNow,
                Source = source
            };

            card.StripeCustomerId = stripeCustomerId;
            card.Brand = Truncate(paymentMethod.Card?.Brand, 20);
            card.Last4 = Truncate(paymentMethod.Card?.Last4, 4);
            card.ExpMonth = (int?)paymentMethod.Card?.ExpMonth;
            card.ExpYear = (int?)paymentMethod.Card?.ExpYear;
            card.Funding = Truncate(paymentMethod.Card?.Funding, 20);
            card.Wallet = Truncate(paymentMethod.Card?.Wallet?.Type, 30);
            card.UpdatedAt = DateTime.UtcNow;

            var wasRemoved = existing != null && existing.Status == CustomerPaymentMethodStatus.Removed;
            if (existing == null || wasRemoved)
            {
                card.Status = CustomerPaymentMethodStatus.Active;
                card.StatusReason = null;
                card.RemovedAt = null;
            }

            if (existing == null) _context.CustomerPaymentMethods.Add(card);
            await _context.SaveChangesAsync();

            var user = await _context.Users.FirstAsync(u => u.Id == userId);
            var becamePrimary = false;
            if (user.PrimaryPaymentMethodId == null && card.Status == CustomerPaymentMethodStatus.Active)
            {
                // The first usable card is the Primary. Later cards stay ordinary until the
                // customer gives them a role — never auto-promoted to Backup.
                user.PrimaryPaymentMethodId = card.Id;
                becamePrimary = true;
            }
            // The mirror follows the Primary only — adding an ordinary card must leave it alone.
            if (user.PrimaryPaymentMethodId == card.Id) MirrorLegacyColumns(user, card);
            await _context.SaveChangesAsync();
            await transaction.CommitAsync();

            if (existing == null || wasRemoved)
            {
                await AuditAsync(userId, "CardAdded", new
                {
                    CardId = card.Id,
                    Card = Label(card),
                    Source = source,
                    BecamePrimary = becamePrimary
                });
            }

            return card;
        }

        // ── Roles ─────────────────────────────────────────────────────────────────────────────

        public async Task<string> SetPrimaryAsync(int userId, int cardId)
        {
            await using var transaction = await BeginUserLockAsync(userId);

            var user = await _context.Users.FirstAsync(u => u.Id == userId);
            var card = await RequireOwnedActiveCardAsync(userId, cardId);

            if (card.IsExpiredAt(NyTimeHelper.NowNy))
                throw new BillingRuleException("This card has expired and can't be your Primary card. Please add a new card.");

            if (user.PrimaryPaymentMethodId == card.Id)
                return "This card is already your Primary card.";

            var previousPrimary = user.PrimaryPaymentMethodId;
            var backupCleared = false;

            // A card cannot hold both roles. Promoting the Backup therefore empties the Backup
            // slot — said out loud in the response, never done silently.
            if (user.BackupPaymentMethodId == card.Id)
            {
                user.BackupPaymentMethodId = null;
                backupCleared = true;
            }

            user.PrimaryPaymentMethodId = card.Id;
            MirrorLegacyColumns(user, card);
            user.UpdatedAt = DateTime.UtcNow;

            await SaveRoleChangeAsync();
            await transaction.CommitAsync();

            await AuditAsync(userId, "PrimaryCardChanged", new
            {
                PreviousPrimaryCardId = previousPrimary,
                NewPrimaryCardId = card.Id,
                Card = Label(card),
                BackupCleared = backupCleared
            });

            return backupCleared
                ? $"{Label(card)} is now your Primary card. It was your Backup card, so you no longer have a Backup card."
                : $"{Label(card)} is now your Primary card.";
        }

        public async Task<string> SetBackupAsync(int userId, int? cardId)
        {
            await using var transaction = await BeginUserLockAsync(userId);

            var user = await _context.Users.FirstAsync(u => u.Id == userId);
            var previousBackup = user.BackupPaymentMethodId;

            if (cardId == null)
            {
                if (user.BackupPaymentMethodId == null) return "You don't have a Backup card.";
                user.BackupPaymentMethodId = null;
                user.UpdatedAt = DateTime.UtcNow;
                await SaveRoleChangeAsync();
                await transaction.CommitAsync();
                await AuditAsync(userId, "BackupCardCleared", new { PreviousBackupCardId = previousBackup });
                return "Your Backup card was removed from that role. Automatic payments will no longer fall back to another card.";
            }

            var card = await RequireOwnedActiveCardAsync(userId, cardId.Value);

            if (user.PrimaryPaymentMethodId == null)
                throw new BillingRuleException("Choose a Primary card before choosing a Backup card.");
            if (user.PrimaryPaymentMethodId == card.Id)
                throw new BillingRuleException("Your Primary card can't also be your Backup card. Choose a different card.",
                    "same_as_primary");
            if (card.IsExpiredAt(NyTimeHelper.NowNy))
                throw new BillingRuleException("This card has expired and can't be your Backup card.");

            if (user.BackupPaymentMethodId == card.Id) return "This card is already your Backup card.";

            user.BackupPaymentMethodId = card.Id;
            user.UpdatedAt = DateTime.UtcNow;
            await SaveRoleChangeAsync();
            await transaction.CommitAsync();

            await AuditAsync(userId, "BackupCardChanged", new
            {
                PreviousBackupCardId = previousBackup,
                NewBackupCardId = card.Id,
                Card = Label(card)
            });

            return $"{Label(card)} is now your Backup card.";
        }

        // ── Removing ──────────────────────────────────────────────────────────────────────────

        public async Task<string> RemoveAsync(int userId, int cardId, int? newPrimaryCardId)
        {
            CustomerPaymentMethod card;
            var autoPayTurnedOff = false;
            string message;

            await using (var transaction = await BeginUserLockAsync(userId))
            {
                var user = await _context.Users.FirstAsync(u => u.Id == userId);
                card = await _context.CustomerPaymentMethods
                    .FirstOrDefaultAsync(c => c.Id == cardId && c.UserId == userId && c.Status != CustomerPaymentMethodStatus.Removed)
                    ?? throw new BillingRuleException("That card was not found.");

                // Never pull a card out from under a charge that is still in flight on it — the
                // outcome has to be known before the card can go.
                var inFlight = await _context.BillingPaymentAttempts.AnyAsync(a =>
                    a.CustomerPaymentMethodId == card.Id && a.ActiveLockKey != null);
                if (inFlight)
                    throw new BillingRuleException("A payment with this card is being processed right now. Please try again in a few minutes.",
                        "payment_in_progress");

                var others = await _context.CustomerPaymentMethods
                    .Where(c => c.UserId == userId && c.Id != card.Id && c.Status == CustomerPaymentMethodStatus.Active)
                    .ToListAsync();
                var usableOthers = others.Where(c => !c.IsExpiredAt(NyTimeHelper.NowNy)).ToList();

                var removingPrimary = user.PrimaryPaymentMethodId == card.Id;
                var removingBackup = user.BackupPaymentMethodId == card.Id;

                if (removingPrimary)
                {
                    if (usableOthers.Count > 0)
                    {
                        // Removing the Primary while other cards exist needs an explicit successor:
                        // silently promoting one would change which card AutoPay charges.
                        if (newPrimaryCardId == null)
                            throw new BillingRuleException(
                                "This is your Primary card. Choose which card should become your Primary card before removing it.",
                                "choose_new_primary");

                        var successor = usableOthers.FirstOrDefault(c => c.Id == newPrimaryCardId)
                            ?? throw new BillingRuleException("Choose one of your other saved cards as the new Primary card.");

                        if (user.BackupPaymentMethodId == successor.Id) user.BackupPaymentMethodId = null;
                        user.PrimaryPaymentMethodId = successor.Id;
                        MirrorLegacyColumns(user, successor);
                    }
                    else
                    {
                        // The last usable card. No Primary, no Backup, and AutoPay goes off rather
                        // than pointing at nothing.
                        user.PrimaryPaymentMethodId = null;
                        user.BackupPaymentMethodId = null;
                        MirrorLegacyColumns(user, null);
                        if (user.AutoPayEnabled)
                        {
                            user.AutoPayEnabled = false;
                            user.AutoPayUpdatedAt = DateTime.UtcNow;
                            autoPayTurnedOff = true;
                            await RevokeGeneralAuthorizationAsync(userId, "The last saved card was removed.");
                        }
                    }
                }
                else if (removingBackup)
                {
                    user.BackupPaymentMethodId = null;
                }

                card.Status = CustomerPaymentMethodStatus.Removed;
                card.RemovedAt = DateTime.UtcNow;
                card.UpdatedAt = DateTime.UtcNow;
                user.UpdatedAt = DateTime.UtcNow;

                await SaveRoleChangeAsync();
                await transaction.CommitAsync();

                message = removingPrimary && usableOthers.Count > 0
                    ? $"{Label(card)} was removed. Your new Primary card is set."
                    : removingPrimary
                        ? $"{Label(card)} was removed. You have no saved cards left" + (autoPayTurnedOff ? ", so Automatic Payments were turned off." : ".")
                        : removingBackup
                            ? $"{Label(card)} was removed. You no longer have a Backup card, so automatic payments won't fall back to another card."
                            : $"{Label(card)} was removed.";

                await AuditAsync(userId, "CardRemoved", new
                {
                    CardId = card.Id,
                    Card = Label(card),
                    WasPrimary = removingPrimary,
                    WasBackup = removingBackup,
                    NewPrimaryCardId = removingPrimary ? user.PrimaryPaymentMethodId : null,
                    AutoPayTurnedOff = autoPayTurnedOff
                });
            }

            // Detach at Stripe AFTER the database says the card is gone: from this moment nothing
            // here will charge it, so a failed detach (Stripe down) leaves an orphan attachment,
            // never a card we still think is removable but keep charging.
            await _stripe.DetachPaymentMethodAsync(card.StripePaymentMethodId);

            return message;
        }

        /// <summary>
        /// payment_method.detached — the card left the Customer outside this app (Stripe dashboard,
        /// another integration). Marked removed, and the roles are repaired: the Backup is promoted
        /// to Primary first, because the customer already chose it as the card to fall back to.
        /// </summary>
        public async Task HandleDetachedAtStripeAsync(string stripePaymentMethodId)
        {
            var card = await _context.CustomerPaymentMethods.AsNoTracking()
                .FirstOrDefaultAsync(c => c.StripePaymentMethodId == stripePaymentMethodId);
            if (card == null || card.Status == CustomerPaymentMethodStatus.Removed) return;

            var userId = card.UserId;
            string? promoted = null;
            var autoPayTurnedOff = false;

            await using (var transaction = await BeginUserLockAsync(userId))
            {
                var tracked = await _context.CustomerPaymentMethods.FirstAsync(c => c.Id == card.Id);
                if (tracked.Status == CustomerPaymentMethodStatus.Removed) return;

                var user = await _context.Users.FirstAsync(u => u.Id == userId);
                tracked.Status = CustomerPaymentMethodStatus.Removed;
                tracked.RemovedAt = DateTime.UtcNow;
                tracked.StatusReason = "detached_at_stripe";
                tracked.UpdatedAt = DateTime.UtcNow;

                if (user.BackupPaymentMethodId == tracked.Id) user.BackupPaymentMethodId = null;

                if (user.PrimaryPaymentMethodId == tracked.Id)
                {
                    var candidates = await _context.CustomerPaymentMethods
                        .Where(c => c.UserId == userId && c.Id != tracked.Id && c.Status == CustomerPaymentMethodStatus.Active)
                        .OrderByDescending(c => c.CreatedAt)
                        .ToListAsync();
                    var nowNy = NyTimeHelper.NowNy;
                    var successor = candidates.FirstOrDefault(c => c.Id == user.BackupPaymentMethodId && !c.IsExpiredAt(nowNy))
                                    ?? candidates.FirstOrDefault(c => !c.IsExpiredAt(nowNy));

                    if (successor != null)
                    {
                        if (user.BackupPaymentMethodId == successor.Id) user.BackupPaymentMethodId = null;
                        user.PrimaryPaymentMethodId = successor.Id;
                        MirrorLegacyColumns(user, successor);
                        promoted = Label(successor);
                    }
                    else
                    {
                        user.PrimaryPaymentMethodId = null;
                        user.BackupPaymentMethodId = null;
                        MirrorLegacyColumns(user, null);
                        if (user.AutoPayEnabled)
                        {
                            user.AutoPayEnabled = false;
                            user.AutoPayUpdatedAt = DateTime.UtcNow;
                            autoPayTurnedOff = true;
                            await RevokeGeneralAuthorizationAsync(userId, "The last saved card was removed at the payment processor.");
                        }
                    }
                }

                await SaveRoleChangeAsync();
                await transaction.CommitAsync();
            }

            await AuditAsync(userId, "CardDetachedExternally", new
            {
                CardId = card.Id,
                Card = Label(card),
                PromotedToPrimary = promoted,
                AutoPayTurnedOff = autoPayTurnedOff
            });

            await _notifications.NotifyCardRemovedAsync(userId, card.Id, Label(card), promoted, autoPayTurnedOff);
        }

        /// <summary>payment_method.updated / automatically_updated — new expiry or number from the
        /// card network's account updater. Display data only.</summary>
        public async Task RefreshFromStripeAsync(string stripePaymentMethodId)
        {
            var card = await _context.CustomerPaymentMethods
                .FirstOrDefaultAsync(c => c.StripePaymentMethodId == stripePaymentMethodId);
            if (card == null || card.Status == CustomerPaymentMethodStatus.Removed) return;

            var paymentMethod = await _stripe.GetPaymentMethodAsync(stripePaymentMethodId);
            card.Brand = Truncate(paymentMethod.Card?.Brand, 20) ?? card.Brand;
            card.Last4 = Truncate(paymentMethod.Card?.Last4, 4) ?? card.Last4;
            card.ExpMonth = (int?)paymentMethod.Card?.ExpMonth ?? card.ExpMonth;
            card.ExpYear = (int?)paymentMethod.Card?.ExpYear ?? card.ExpYear;
            card.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            var user = await _context.Users.FirstAsync(u => u.Id == card.UserId);
            if (user.PrimaryPaymentMethodId == card.Id)
            {
                MirrorLegacyColumns(user, card);
                await _context.SaveChangesAsync();
            }
        }

        public async Task RecordChargeFailureAsync(int cardId, string? failureCode, string? declineCode)
        {
            var card = await _context.CustomerPaymentMethods.FirstOrDefaultAsync(c => c.Id == cardId);
            if (card == null) return;

            card.LastFailedAt = DateTime.UtcNow;
            card.LastFailureCode = Truncate(declineCode ?? failureCode, 100);
            card.UpdatedAt = DateTime.UtcNow;

            // The issuer asked us never to try this card again: it stays visible, marked, and is
            // never charged — automatic retries of a card like this are what gets merchants flagged.
            if ((declineCode != null && NeverRetryDeclineCodes.Contains(declineCode))
                || (failureCode != null && NeverRetryDeclineCodes.Contains(failureCode)))
            {
                card.Status = CustomerPaymentMethodStatus.Blocked;
                card.StatusReason = Truncate(declineCode ?? failureCode, 100);
            }

            await _context.SaveChangesAsync();
        }

        public async Task RecordChargeSuccessAsync(int cardId)
        {
            var card = await _context.CustomerPaymentMethods.FirstOrDefaultAsync(c => c.Id == cardId);
            if (card == null) return;
            card.LastUsedAt = DateTime.UtcNow;
            card.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
        }

        // ── Helpers ───────────────────────────────────────────────────────────────────────────

        private async Task<CustomerPaymentMethod> RequireOwnedActiveCardAsync(int userId, int cardId)
        {
            var card = await _context.CustomerPaymentMethods
                .FirstOrDefaultAsync(c => c.Id == cardId && c.UserId == userId);

            if (card == null || card.Status == CustomerPaymentMethodStatus.Removed)
                throw new BillingRuleException("That card was not found.");
            if (card.Status == CustomerPaymentMethodStatus.Blocked)
                throw new BillingRuleException("Your bank asked us not to use this card again. Please add another card.");
            return card;
        }

        /// <summary>
        /// Opens a transaction and locks this customer's Users row (<c>SELECT … FOR UPDATE</c>) so
        /// every card mutation for one customer runs one at a time. The in-memory provider used by
        /// unit tests has neither, and gets a no-op transaction.
        /// </summary>
        private async Task<ITransactionScope> BeginUserLockAsync(int userId)
        {
            if (!_context.Database.IsRelational())
                return NoopTransaction.Instance;

            var transaction = await _context.Database.BeginTransactionAsync();
            await _context.Database.ExecuteSqlRawAsync("SELECT `Id` FROM `Users` WHERE `Id` = {0} FOR UPDATE", userId);
            return new EfTransactionScope(transaction);
        }

        private async Task SaveRoleChangeAsync()
        {
            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException ex)
            {
                // A CHECK constraint refused the combination (a concurrent request changed the
                // other role first). Nothing was committed.
                _logger.LogWarning(ex, "Card role change refused by the database.");
                throw new BillingRuleException("Your cards were changed at the same time from another window. Please refresh and try again.",
                    "concurrent_change");
            }
        }

        private async Task RevokeGeneralAuthorizationAsync(int userId, string reason)
        {
            var general = await _context.PaymentAuthorizations
                .Where(a => a.UserId == userId && a.Scope == PaymentAuthorizationScope.General
                            && a.Status == PaymentAuthorizationStatus.Active)
                .ToListAsync();
            foreach (var row in general)
            {
                row.Status = PaymentAuthorizationStatus.Revoked;
                row.ActiveScopeKey = null;
                row.RevokedAt = DateTime.UtcNow;
                row.RevokedReason = reason;
                row.UpdatedAt = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Keeps the pre-2026-09 one-card columns equal to the Primary card, so a rollback or any
        /// report still reading them sees the truth. Nothing new reads them.
        /// </summary>
        private static void MirrorLegacyColumns(User user, CustomerPaymentMethod? primary)
        {
            user.DefaultPaymentMethodId = primary?.StripePaymentMethodId;
            user.SavedCardBrand = primary?.Brand;
            user.SavedCardLast4 = primary?.Last4;
        }

        // ── Reconciling with Stripe ───────────────────────────────────────────────────────────

        /// <summary>Per-customer throttle for <see cref="ReconcileWithStripeAsync"/>. Static: the
        /// service is scoped, and the point is to stop every page load costing Stripe calls.</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, DateTime> _lastReconcileUtc = new();

        public static readonly TimeSpan ReconcileInterval = TimeSpan.FromMinutes(10);

        /// <inheritdoc />
        /// <remarks>
        /// The case this exists for: a SetupIntent (or a payment confirmed with
        /// setup_future_usage) succeeded, Stripe attached the card — and the call that would have
        /// recorded it here failed. The customer's card is then real at Stripe and invisible in
        /// their Billing tab.
        ///
        /// Rules, in the order they matter:
        ///  • A card is only ever adopted with EVIDENCE that this customer authorised saving it
        ///    (<see cref="IStripeService.FindCardSaveConsentAsync"/>), and only from their OWN
        ///    Stripe Customer. No evidence: left strictly alone — never shown, never detached.
        ///  • A card the customer REMOVED here but whose detach never landed is detached again,
        ///    unless a payment attempt still holds it. Their removal is honoured; it is never
        ///    "recovered" back into the wallet.
        ///  • Idempotent: re-running changes nothing. Throttled per customer. Never throws.
        /// </remarks>
        public async Task<int> ReconcileWithStripeAsync(int userId, bool force = false)
        {
            try
            {
                var now = DateTime.UtcNow;
                if (!force && _lastReconcileUtc.TryGetValue(userId, out var last) && now - last < ReconcileInterval)
                    return 0;

                var customerId = await _context.Users.AsNoTracking()
                    .Where(u => u.Id == userId).Select(u => u.StripeCustomerId).FirstOrDefaultAsync();
                if (string.IsNullOrEmpty(customerId))
                {
                    _lastReconcileUtc[userId] = now;
                    return 0;
                }

                var attached = await _stripe.ListCustomerCardsAsync(customerId);
                _lastReconcileUtc[userId] = DateTime.UtcNow;   // stamped only once Stripe answered

                var known = await _context.CustomerPaymentMethods
                    .Where(c => c.UserId == userId).ToListAsync();
                var recovered = 0;

                foreach (var card in attached)
                {
                    // Belt and braces: the listing is already scoped to this Customer.
                    if (!string.Equals(card.CustomerId, customerId, StringComparison.Ordinal)) continue;

                    var row = known.FirstOrDefault(c => c.StripePaymentMethodId == card.Id);
                    if (row != null)
                    {
                        if (row.Status == CustomerPaymentMethodStatus.Removed)
                            await RetryDetachAsync(card.Id);
                        continue;
                    }

                    // The pm belongs to another account's wallet: never touch it.
                    if (await _context.CustomerPaymentMethods.AnyAsync(c =>
                            c.StripePaymentMethodId == card.Id && c.UserId != userId))
                        continue;

                    var consent = await _stripe.FindCardSaveConsentAsync(customerId, card.Id);
                    if (consent == null)
                    {
                        _logger.LogInformation(
                            "Card {PaymentMethodId} is attached to user {UserId}'s Stripe customer with no save authorisation on record; left alone.",
                            card.Id, userId);
                        continue;
                    }

                    await UpsertAsync(userId, customerId, card.Id, consent == "setup_intent" ? "recovered_setup" : "recovered_payment");
                    recovered++;
                    _logger.LogInformation(
                        "Recovered saved card {PaymentMethodId} for user {UserId} from Stripe ({Evidence}).",
                        card.Id, userId, consent);
                }

                return recovered;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reconciling saved cards with Stripe failed for user {UserId}.", userId);
                return 0;
            }
        }

        /// <summary>A removal whose detach never landed, finished now — unless a payment attempt
        /// still holds the card, in which case the next pass tries again.</summary>
        private async Task RetryDetachAsync(string stripePaymentMethodId)
        {
            var inFlight = await _context.BillingPaymentAttempts.AnyAsync(a =>
                a.ActiveLockKey != null && a.StripePaymentMethodId == stripePaymentMethodId);
            if (inFlight) return;

            await _stripe.DetachPaymentMethodAsync(stripePaymentMethodId);
            _logger.LogInformation("Detached removed card {PaymentMethodId} that was still attached at Stripe.", stripePaymentMethodId);
        }

        private async Task BackfillMissingExpiryAsync(List<CustomerPaymentMethod> cards)
        {
            // Cards migrated from the one-card columns have no expiry on record. Read it once from
            // Stripe; a failure just leaves it unknown (unknown is never treated as expired).
            var missing = cards.Where(c => c.ExpMonth == null && c.Status == CustomerPaymentMethodStatus.Active).ToList();
            if (missing.Count == 0) return;

            foreach (var card in missing)
            {
                try
                {
                    var paymentMethod = await _stripe.GetPaymentMethodAsync(card.StripePaymentMethodId);
                    card.ExpMonth = (int?)paymentMethod.Card?.ExpMonth;
                    card.ExpYear = (int?)paymentMethod.Card?.ExpYear;
                    card.Funding ??= Truncate(paymentMethod.Card?.Funding, 20);
                    card.UpdatedAt = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    _logger.LogInformation(ex, "Could not backfill expiry for card {CardId}.", card.Id);
                }
            }

            try { await _context.SaveChangesAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not save backfilled card expiry."); }
        }

        public static string Label(CustomerPaymentMethod card)
        {
            var brand = string.IsNullOrWhiteSpace(card.Brand)
                ? "Card"
                : char.ToUpperInvariant(card.Brand[0]) + card.Brand[1..];
            return string.IsNullOrWhiteSpace(card.Last4) ? brand : $"{brand} ending {card.Last4}";
        }

        private async Task AuditAsync(int userId, string action, object payload)
        {
            try
            {
                await _audit.LogActionAsync(AuditEntityTypes.CustomerPaymentMethodAction, userId, action, null, payload,
                    actingUserId: userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not audit card action {Action} for user {UserId}.", action, userId);
            }
        }

        private static string? Truncate(string? value, int max) =>
            string.IsNullOrEmpty(value) ? value : value.Length <= max ? value : value[..max];
    }

    /// <summary>A transaction that may be a no-op (in-memory provider in unit tests).</summary>
    public interface ITransactionScope : IAsyncDisposable
    {
        Task CommitAsync();
    }

    internal sealed class NoopTransaction : ITransactionScope
    {
        public static readonly NoopTransaction Instance = new();
        public Task CommitAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal sealed class EfTransactionScope : ITransactionScope
    {
        private readonly Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction _transaction;
        public EfTransactionScope(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction) => _transaction = transaction;
        public Task CommitAsync() => _transaction.CommitAsync();
        public ValueTask DisposeAsync() => _transaction.DisposeAsync();
    }
}
