using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using System.Text;

namespace DreamCleaningBackend.Services
{
    public class GiftCardService : IGiftCardService
    {
        private readonly ApplicationDbContext _context;
        private readonly IAuditService _auditService;
        private readonly IEmailService _emailService;
        private readonly ILogger<GiftCardService> _logger;

        // Profile send / resend limits (the per-IP limiter is GiftCardController.SendRateLimitPolicy).
        public static readonly TimeSpan ResendCooldown = TimeSpan.FromMinutes(10);
        public const int MaxProfileEmailsPerUserPerHour = 5;

        public GiftCardService(ApplicationDbContext context, IAuditService auditService,
            IEmailService emailService, ILogger<GiftCardService> logger)
        {
            _context = context;
            _auditService = auditService;
            _emailService = emailService;
            _logger = logger;
        }

        public string GenerateUniqueGiftCardCode()
        {
            string code;
            bool exists;

            do
            {
                code = GenerateRandomCode();
                exists = _context.GiftCards.Any(g => g.Code == code);
            }
            while (exists);

            return code;
        }

        private string GenerateRandomCode()
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            var random = new Random();
            var result = new StringBuilder();

            for (int i = 0; i < 12; i++)
            {
                if (i > 0 && i % 4 == 0)
                {
                    result.Append('-');
                }
                result.Append(chars[random.Next(chars.Length)]);
            }

            return result.ToString();
        }

        public async Task<GiftCard> CreateGiftCard(int? userId, CreateGiftCardDto createDto)
        {
            // "Send later" cards carry no recipient or message until they are sent from the profile.
            var sendLater = createDto.SendLater;
            var giftCard = new GiftCard
            {
                Code = GenerateUniqueGiftCardCode(),
                OriginalAmount = createDto.Amount,
                CurrentBalance = createDto.Amount,
                RecipientName = sendLater ? null : createDto.RecipientName,
                RecipientEmail = sendLater ? null : createDto.RecipientEmail,
                SenderName = createDto.SenderName,
                SenderEmail = createDto.SenderEmail,
                Message = sendLater ? null : createDto.Message,
                IsPendingSend = sendLater,
                PurchasedByUserId = userId, // Null for anonymous purchases
                CreatedAt = DateTime.UtcNow
            };

            _context.GiftCards.Add(giftCard);
            await _context.SaveChangesAsync();

            // ADD THIS: LOG THE CREATION
            await _auditService.LogCreateAsync(giftCard);

            return giftCard;
        }

        public async Task<GiftCardValidationDto> ValidateGiftCard(string code)
        {
            var giftCard = await _context.GiftCards
                .FirstOrDefaultAsync(g => g.Code == code);

            if (giftCard == null)
            {
                return new GiftCardValidationDto
                {
                    IsValid = false,
                    AvailableBalance = 0,
                    Message = "Invalid gift card code"
                };
            }

            if (!giftCard.IsActive)
            {
                return new GiftCardValidationDto
                {
                    IsValid = false,
                    AvailableBalance = 0,
                    Message = "This gift card has been deactivated"
                };
            }

            if (!giftCard.IsPaid)
            {
                return new GiftCardValidationDto
                {
                    IsValid = false,
                    AvailableBalance = 0,
                    Message = "This gift card payment is still pending"
                };
            }

            if (giftCard.CurrentBalance <= 0)
            {
                return new GiftCardValidationDto
                {
                    IsValid = false,
                    AvailableBalance = 0,
                    Message = "This gift card has been fully used"
                };
            }

            return new GiftCardValidationDto
            {
                IsValid = true,
                AvailableBalance = giftCard.CurrentBalance,
                RecipientName = giftCard.RecipientName
            };
        }

        public async Task<decimal> ApplyGiftCardToOrder(string code, decimal orderAmount, int orderId, int userId)
        {
            var giftCard = await _context.GiftCards
                .FirstOrDefaultAsync(g => g.Code == code);

            if (giftCard == null || !giftCard.IsActive || !giftCard.IsPaid || giftCard.CurrentBalance <= 0)
            {
                throw new InvalidOperationException("Invalid or unusable gift card");
            }

            // FULL scalar snapshot - see AuditSnapshot. The hand-picked copy omitted PaidAt,
            // PaymentIntentId, PurchasedByUserId, Message and CreatedAt, so every one of those
            // was recorded as cleared and Undo would have replayed the blanks.
            var originalGiftCard = AuditSnapshot.Of(giftCard);

            // Calculate the amount to apply
            var amountToApply = Math.Min(giftCard.CurrentBalance, orderAmount);

            // Update gift card balance
            giftCard.CurrentBalance -= amountToApply;
            giftCard.UpdatedAt = DateTime.UtcNow;

            // Save gift card changes first
            await _context.SaveChangesAsync();

            // Log the gift card update AFTER saving
            await _auditService.LogUpdateAsync(originalGiftCard, giftCard);

            // Create usage record
            var usage = new GiftCardUsage
            {
                GiftCardId = giftCard.Id,
                OrderId = orderId,
                UserId = userId,
                AmountUsed = amountToApply,
                BalanceAfterUsage = giftCard.CurrentBalance,
                UsedAt = DateTime.UtcNow
            };

            _context.GiftCardUsages.Add(usage);
            await _context.SaveChangesAsync();

            // Create a simple usage object for logging (without navigation properties)
            var usageForLogging = new GiftCardUsage
            {
                Id = usage.Id,
                GiftCardId = usage.GiftCardId,
                OrderId = usage.OrderId,
                UserId = usage.UserId,
                AmountUsed = usage.AmountUsed,
                BalanceAfterUsage = usage.BalanceAfterUsage,
                UsedAt = usage.UsedAt
            };

            // Log the usage creation
            await _auditService.LogCreateAsync(usageForLogging);

            return amountToApply;
        }

        public async Task<List<GiftCardDto>> GetUserGiftCards(int userId)
        {
            var giftCards = await _context.GiftCards
                .Include(g => g.PurchasedByUser)
                .Where(g => g.PurchasedByUserId == userId)
                .OrderByDescending(g => g.CreatedAt)
                .Select(g => new GiftCardDto
                {
                    Id = g.Id,
                    Code = g.Code,
                    OriginalAmount = g.OriginalAmount,
                    CurrentBalance = g.CurrentBalance,
                    RecipientName = g.RecipientName,
                    RecipientEmail = g.RecipientEmail,
                    SenderName = g.SenderName,
                    SenderEmail = g.SenderEmail,
                    Message = g.Message,
                    IsActive = g.IsActive,
                    IsUsed = g.CurrentBalance <= 0,  // Calculate based on balance
                    CreatedAt = g.CreatedAt,
                    UsedAt = null,  // We'll get this from usage history if needed
                    PurchasedByUserName = g.PurchasedByUser.FirstName + " " + g.PurchasedByUser.LastName,
                    UsedByUserName = null  // Remove this or get from usage history
                })
                .ToListAsync();

            // A sent card's code belongs to its recipient - same masking as the profile list.
            var pendingIds = await _context.GiftCards
                .Where(g => g.PurchasedByUserId == userId && g.IsPendingSend)
                .Select(g => g.Id)
                .ToListAsync();
            foreach (var card in giftCards.Where(c => !pendingIds.Contains(c.Id)))
                card.Code = MaskCode(card.Code);

            return giftCards;
        }

        // ---- Profile -> Gift Cards ("buy for myself - send later") ----

        // Last 4 characters only, e.g. "••••-••••-AB12" - a sent card belongs to its recipient.
        public static string MaskCode(string code)
        {
            if (string.IsNullOrEmpty(code) || code.Length <= 4) return code;
            var tail = code.Substring(code.Length - 4);
            return code.Length == 14 ? $"••••-••••-{tail}" : new string('•', code.Length - 4) + tail;
        }

        private static MyGiftCardDto ToMyGiftCardDto(GiftCard g)
        {
            var isSent = !g.IsPendingSend;
            var fullyUsed = g.CurrentBalance <= 0;
            return new MyGiftCardDto
            {
                Id = g.Id,
                Code = isSent ? MaskCode(g.Code) : g.Code,
                IsCodeMasked = isSent,
                OriginalAmount = g.OriginalAmount,
                CurrentBalance = g.CurrentBalance,
                AmountUsed = g.OriginalAmount - g.CurrentBalance,
                PurchasedAt = g.PaidAt ?? g.CreatedAt,
                Status = fullyUsed ? "FullyUsed" : isSent ? "Sent" : "NotSent",
                IsPendingSend = g.IsPendingSend,
                IsActive = g.IsActive,
                RecipientName = isSent ? g.RecipientName : null,
                RecipientEmail = isSent ? g.RecipientEmail : null,
                // "Send now" cards (and every card created before this feature) were sent at payment.
                SentAt = isSent ? (g.SentAt ?? g.PaidAt ?? g.CreatedAt) : null,
                SenderName = g.SenderName,
                Message = isSent ? g.Message : null,
                CanSend = g.IsPendingSend && g.IsActive && !fullyUsed,
                // Resend always goes to the recipient already on the card - never a new one.
                CanResend = isSent && g.IsActive && !fullyUsed
                    && !string.IsNullOrEmpty(g.RecipientEmail),
                Usages = g.GiftCardUsages
                    .OrderByDescending(u => u.UsedAt)
                    .Select(u => new MyGiftCardUsageDto { UsedAt = u.UsedAt, AmountUsed = u.AmountUsed })
                    .ToList()
            };
        }

        public async Task<List<MyGiftCardDto>> GetMyGiftCards(int userId)
        {
            // Only PAID cards linked to this account at purchase time. SenderEmail is never used
            // to link: it is typed by the buyer and unverified.
            var cards = await _context.GiftCards
                .AsNoTracking()
                .Include(g => g.GiftCardUsages)
                .Where(g => g.PurchasedByUserId == userId && g.IsPaid)
                .OrderByDescending(g => g.CreatedAt)
                .ToListAsync();

            return cards.Select(ToMyGiftCardDto).ToList();
        }

        // Loads a card only if it belongs to userId - another user's card reads as "not found".
        private async Task<GiftCard?> LoadOwnedCardAsync(int giftCardId, int userId)
        {
            return await _context.GiftCards
                .Include(g => g.GiftCardUsages)
                .FirstOrDefaultAsync(g => g.Id == giftCardId && g.PurchasedByUserId == userId && g.IsPaid);
        }

        private async Task<string?> CheckUserHourlyCapAsync(int userId)
        {
            var since = DateTime.UtcNow.AddHours(-1);
            var recent = await _context.GiftCards
                .CountAsync(g => g.PurchasedByUserId == userId && g.LastEmailSentAt != null && g.LastEmailSentAt > since);
            return recent >= MaxProfileEmailsPerUserPerHour
                ? "You've sent several gift card emails in the last hour. Please try again later."
                : null;
        }

        public async Task<GiftCardSendResult> SendMyGiftCard(int giftCardId, int userId, SendMyGiftCardDto dto)
        {
            var recipientName = dto.RecipientName?.Trim() ?? "";
            var recipientEmail = dto.RecipientEmail?.Trim() ?? "";
            var senderName = dto.SenderName?.Trim() ?? "";
            var message = string.IsNullOrWhiteSpace(dto.Message) ? null : dto.Message.Trim();

            if (recipientName.Length == 0) return GiftCardSendResult.Invalid("Recipient name is required.");
            if (senderName.Length == 0) return GiftCardSendResult.Invalid("Your name is required.");
            var emailProblem = Helpers.EmailAddressValidator.DescribeProblem(recipientEmail);
            if (emailProblem != null) return GiftCardSendResult.Invalid(emailProblem);

            var card = await LoadOwnedCardAsync(giftCardId, userId);
            if (card == null) return GiftCardSendResult.NotFound();
            if (!card.IsPendingSend) return GiftCardSendResult.Invalid("This gift card has already been sent. You can resend it to the same recipient instead.");
            if (!card.IsActive) return GiftCardSendResult.Invalid("This gift card has been deactivated. Please contact us.");
            if (card.CurrentBalance <= 0) return GiftCardSendResult.Invalid("This gift card has been fully used and can't be sent.");

            var capProblem = await CheckUserHourlyCapAsync(userId);
            if (capProblem != null) return GiftCardSendResult.TooMany(capProblem);

            // Claim the card first with a conditional update: a double-click or two tabs can't both
            // send it, and the recipient can never be overwritten once set.
            var now = DateTime.UtcNow;
            var claimed = await _context.GiftCards
                .Where(g => g.Id == card.Id && g.IsPendingSend)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(g => g.IsPendingSend, false)
                    .SetProperty(g => g.RecipientName, recipientName)
                    .SetProperty(g => g.RecipientEmail, recipientEmail)
                    .SetProperty(g => g.SenderName, senderName)
                    .SetProperty(g => g.Message, message)
                    .SetProperty(g => g.SentAt, now)
                    .SetProperty(g => g.LastEmailSentAt, now)
                    .SetProperty(g => g.UpdatedAt, now));
            if (claimed == 0)
                return GiftCardSendResult.Invalid("This gift card has already been sent. You can resend it to the same recipient instead.");

            var original = AuditSnapshot.Of(card);
            await _context.Entry(card).ReloadAsync();
            try { await _auditService.LogUpdateAsync(original, card); }
            catch (Exception ex) { _logger.LogError(ex, "Audit logging failed for gift card {GiftCardId} send", card.Id); }

            // Same email, template and background image as the "send now" purchase. The amount is the
            // REMAINING balance - a partly used card is worth what is left on it.
            try
            {
                await _emailService.SendGiftCardNotificationAsync(
                    recipientEmail, recipientName, senderName, card.Code, card.CurrentBalance,
                    message ?? "", card.SenderEmail);
            }
            catch (Exception ex)
            {
                // The recipient is saved (it can't change now), but no email went out: clear the stamp
                // so "Resend email" is available immediately rather than after the cooldown.
                _logger.LogError(ex, "Gift card {GiftCardId} recipient email failed", card.Id);
                await _context.GiftCards.Where(g => g.Id == card.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(g => g.LastEmailSentAt, (DateTime?)null));
                card.LastEmailSentAt = null;
                return GiftCardSendResult.EmailFailed(ToMyGiftCardDto(card),
                    $"The gift card is now assigned to {recipientName}, but the email could not be delivered. Please press \"Resend email\" in a few minutes.");
            }

            // Buyer confirmation: the existing purchase-confirmation email, which describes exactly this.
            // The card now belongs to the recipient, so the buyer sees the last 4 characters only.
            try
            {
                await _emailService.SendGiftCardSenderConfirmationAsync(
                    card.SenderEmail, senderName, recipientName, recipientEmail, MaskCode(card.Code),
                    card.CurrentBalance, message ?? "");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Gift card {GiftCardId} sent, but the buyer confirmation email failed", card.Id);
            }

            return GiftCardSendResult.Ok(ToMyGiftCardDto(card));
        }

        public async Task<GiftCardSendResult> ResendMyGiftCard(int giftCardId, int userId)
        {
            var card = await LoadOwnedCardAsync(giftCardId, userId);
            if (card == null) return GiftCardSendResult.NotFound();
            if (card.IsPendingSend) return GiftCardSendResult.Invalid("This gift card hasn't been sent yet.");
            if (string.IsNullOrEmpty(card.RecipientEmail))
                return GiftCardSendResult.Invalid("This gift card can't be resent from your profile. Please contact us.");
            if (!card.IsActive) return GiftCardSendResult.Invalid("This gift card has been deactivated. Please contact us.");
            if (card.CurrentBalance <= 0) return GiftCardSendResult.Invalid("This gift card has been fully used.");

            var now = DateTime.UtcNow;
            if (card.LastEmailSentAt != null && now - card.LastEmailSentAt.Value < ResendCooldown)
            {
                var wait = (int)Math.Ceiling((ResendCooldown - (now - card.LastEmailSentAt.Value)).TotalMinutes);
                return GiftCardSendResult.TooMany($"The email was just sent. Please wait {wait} minute{(wait == 1 ? "" : "s")} before resending.");
            }

            var capProblem = await CheckUserHourlyCapAsync(userId);
            if (capProblem != null) return GiftCardSendResult.TooMany(capProblem);

            // Stamp first (conditional on the stamp we read) so two quick clicks send one email.
            var lastSeen = card.LastEmailSentAt;
            var claimed = await _context.GiftCards
                .Where(g => g.Id == card.Id && g.LastEmailSentAt == lastSeen)
                .ExecuteUpdateAsync(s => s.SetProperty(g => g.LastEmailSentAt, now));
            if (claimed == 0)
                return GiftCardSendResult.TooMany("The email was just sent. Please wait a few minutes before resending.");

            // Same recipient, same email - never a new recipient.
            try
            {
                await _emailService.SendGiftCardNotificationAsync(
                    card.RecipientEmail, card.RecipientName ?? "", card.SenderName, card.Code,
                    card.CurrentBalance, card.Message ?? "", card.SenderEmail);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Gift card {GiftCardId} resend email failed", card.Id);
                await _context.GiftCards.Where(x => x.Id == card.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastEmailSentAt, lastSeen));
                return GiftCardSendResult.Failed("The email could not be delivered. Please try again in a few minutes.");
            }

            card.LastEmailSentAt = now;
            return GiftCardSendResult.Ok(ToMyGiftCardDto(card));
        }

        public async Task<List<GiftCardUsageDto>> GetGiftCardUsageHistory(string code, int requestingUserId)
        {
            var giftCard = await _context.GiftCards
                .FirstOrDefaultAsync(g => g.Code == code);

            if (giftCard == null)
                throw new InvalidOperationException("Gift card not found");

            // Check if user has permission to view this gift card
            if (giftCard.PurchasedByUserId != requestingUserId)
                throw new UnauthorizedAccessException("You don't have permission to view this gift card");

            var usages = await _context.GiftCardUsages
                .Include(u => u.Order)
                .Include(u => u.User)
                .Where(u => u.GiftCardId == giftCard.Id)
                .OrderByDescending(u => u.UsedAt)
                .Select(u => new GiftCardUsageDto
                {
                    Id = u.Id,
                    GiftCardCode = code,
                    AmountUsed = u.AmountUsed,
                    BalanceAfterUsage = u.BalanceAfterUsage,
                    UsedAt = u.UsedAt,
                    OrderReference = $"Order #{u.OrderId}",
                    UsedByName = u.User.FirstName + " " + u.User.LastName,
                    UsedByEmail = u.User.Email
                })
                .ToListAsync();

            return usages;
        }

        public async Task<List<GiftCardAdminDto>> GetAllGiftCardsForAdmin()
        {
            var giftCards = await _context.GiftCards
                .Include(g => g.PurchasedByUser)
                .Include(g => g.GiftCardUsages)
                    .ThenInclude(u => u.User)
                .OrderByDescending(g => g.CreatedAt)
                .Select(g => new GiftCardAdminDto
                {
                    Id = g.Id,
                    Code = g.Code,
                    OriginalAmount = g.OriginalAmount,
                    CurrentBalance = g.CurrentBalance,
                    RecipientName = g.RecipientName,
                    RecipientEmail = g.RecipientEmail,
                    SenderName = g.SenderName,
                    SenderEmail = g.SenderEmail,
                    Message = g.Message,
                    IsActive = g.IsActive,
                    IsPaid = g.IsPaid,
                    CreatedAt = g.CreatedAt,
                    PaidAt = g.PaidAt,
                    PurchasedByUserName = g.PurchasedByUser.FirstName + " " + g.PurchasedByUser.LastName,
                    PurchasedByUserEmail = g.PurchasedByUser.Email,
                    IsPendingSend = g.IsPendingSend,
                    SentAt = g.SentAt,
                    TotalAmountUsed = g.OriginalAmount - g.CurrentBalance,
                    TimesUsed = g.GiftCardUsages.Count,
                    LastUsedAt = g.GiftCardUsages.OrderByDescending(u => u.UsedAt).FirstOrDefault() != null
                        ? g.GiftCardUsages.OrderByDescending(u => u.UsedAt).First().UsedAt
                        : (DateTime?)null,
                    Usages = g.GiftCardUsages.Select(u => new GiftCardUsageDto
                    {
                        Id = u.Id,
                        AmountUsed = u.AmountUsed,
                        BalanceAfterUsage = u.BalanceAfterUsage,
                        UsedAt = u.UsedAt,
                        OrderReference = $"Order #{u.OrderId}",
                        UsedByName = u.User.FirstName + " " + u.User.LastName,
                        UsedByEmail = u.User.Email
                    }).ToList()
                })
                .ToListAsync();

            return giftCards;
        }

        public async Task<GiftCard> GetGiftCardByCode(string code)
        {
            return await _context.GiftCards
                .Include(g => g.PurchasedByUser)
                .FirstOrDefaultAsync(g => g.Code == code);
        }

		public async Task<bool> MarkGiftCardAsPaid(int giftCardId, string paymentIntentId)
		{
			var giftCard = await _context.GiftCards.FindAsync(giftCardId);
			if (giftCard == null) return false;

			// NEW: Check if this is the initial payment
			bool isInitialPayment = !giftCard.IsPaid && giftCard.PaidAt == null;

			// FULL scalar snapshot - see AuditSnapshot. This copy carried only 6 of the GiftCard
			// model's 16 scalars, dropping OriginalAmount along with the recipient and sender
			// details, so marking a card paid logged its face value as falling to zero.
			var originalGiftCard = AuditSnapshot.Of(giftCard);

			// YOUR EXISTING CODE: All updates remain exactly the same
			giftCard.IsPaid = true;
			giftCard.PaidAt = DateTime.UtcNow;
			giftCard.PaymentIntentId = paymentIntentId;
			giftCard.UpdatedAt = DateTime.UtcNow;

			// MODIFIED: Only log if NOT initial payment
			if (!isInitialPayment)
			{
				await _auditService.LogUpdateAsync(originalGiftCard, giftCard);
			}

			// YOUR EXISTING CODE: SaveChanges in the same place
			await _context.SaveChangesAsync();
			return true;
		}

		public async Task<bool> SimulateGiftCardPayment(int giftCardId)
		{
			var paymentIntentId = "pi_simulated_" + Guid.NewGuid().ToString();
			return await MarkGiftCardAsPaid(giftCardId, paymentIntentId);
		}
	}
}