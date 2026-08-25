using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services
{
    public interface IOrderReorderPreviewService
    {
        /// <summary>
        /// Builds the "recreate this order" preview: the prefill for the mini booking form plus
        /// the diff an admin has to read before committing to it.
        /// </summary>
        /// <param name="allowCustomPricing">
        /// Half (b) of the custom-pricing gate — see
        /// <see cref="OrderPricingInputBuilder.ShouldHonourCustomPricing"/>. Deliberately has no
        /// default: this preview promises a price the create path will honour, so it has to make
        /// the same decision under the same rule rather than inherit an implicit "allowed".
        /// </param>
        Task<ReorderPreviewDto> BuildAsync(int sourceOrderId, bool allowCustomPricing);
    }

    /// <summary>
    /// Answers "what would it cost to run this exact job again today, and what changed?".
    ///
    /// The recreated order deliberately carries NO discount from the source order. A promo code
    /// can have expired, a gift card can be spent, the first-time flag is gone, bubble points were
    /// redeemed long ago — copying any of those forward would either fail at create time or hand
    /// out a discount nobody is entitled to. So every discount slot is CLEARED in the prefill and
    /// REPORTED in <see cref="ReorderPreviewDto.Discounts"/> instead. Loyalty and subscription are
    /// the two the customer may still genuinely be entitled to; they are reported with
    /// <see cref="ReorderDiscountChangeDto.CanReapply"/> set so the modal can offer them as an
    /// explicit opt-in, and stay off by default like the rest.
    ///
    /// Pricing goes through the shared calculator (OrderPricingCalculator) via the shared input
    /// builder, so "what it costs today" is computed by exactly the code that will charge it.
    /// </summary>
    public class OrderReorderPreviewService : IOrderReorderPreviewService
    {
        private readonly ApplicationDbContext _context;
        private readonly ILoyaltyDiscountService _loyaltyDiscountService;

        public OrderReorderPreviewService(
            ApplicationDbContext context,
            ILoyaltyDiscountService loyaltyDiscountService)
        {
            _context = context;
            _loyaltyDiscountService = loyaltyDiscountService;
        }

        public async Task<ReorderPreviewDto> BuildAsync(int sourceOrderId, bool allowCustomPricing)
        {
            var order = await _context.Orders
                .Include(o => o.User)
                .Include(o => o.ServiceType)
                .Include(o => o.Subscription)
                .Include(o => o.OrderServices).ThenInclude(os => os.Service)
                .Include(o => o.OrderExtraServices).ThenInclude(oes => oes.ExtraService)
                .AsSplitQuery()
                .FirstOrDefaultAsync(o => o.Id == sourceOrderId);

            if (order == null)
                throw new KeyNotFoundException($"Order {sourceOrderId} not found.");

            // The LIVE catalogue for this service type. Thresholds/RateTiers are eager-loaded for
            // the same reason the booking path loads them: lazy loading is off, and an empty
            // Thresholds collection silently prices every service from unit one at a flat rate.
            var serviceType = await _context.ServiceTypes
                .Include(st => st.Services).ThenInclude(s => s.Thresholds)
                .Include(st => st.Services).ThenInclude(s => s.RateTiers)
                .AsSplitQuery()
                .FirstOrDefaultAsync(st => st.Id == order.ServiceTypeId);

            if (serviceType == null)
                throw new InvalidOperationException(
                    "The service type this order was booked on no longer exists, so it cannot be recreated.");

            var preview = new ReorderPreviewDto
            {
                SourceOrderId = order.Id,
                CustomerUserId = order.UserId,
                CustomerName = $"{order.ContactFirstName} {order.ContactLastName}".Trim(),
                OriginalServiceDate = order.ServiceDate,
                ServiceTypeName = order.GetDisplayServiceTypeName(),
                IsCustomServiceType = serviceType.IsCustom,
                NotificationEmail = NoEmailHelper.ResolveOrderNotificationEmail(order.ContactEmail, order.User),
                NotificationPhone = !string.IsNullOrWhiteSpace(order.ContactPhone)
                    ? order.ContactPhone
                    : order.User?.Phone,
                CustomerHasNoAccountEmail = NoEmailHelper.HasNoRealEmail(order.User)
            };

            // ── Which of the source order's lines still exist in the catalogue ────────────────
            var liveServiceIds = serviceType.Services
                .Where(s => s.IsActive)
                .Select(s => s.Id)
                .ToHashSet();

            var survivingServices = new List<Models.OrderService>();
            foreach (var line in order.OrderServices)
            {
                if (liveServiceIds.Contains(line.ServiceId))
                {
                    survivingServices.Add(line);
                    continue;
                }

                preview.Unavailable.Add(new ReorderUnavailableLineDto
                {
                    Kind = "Service",
                    Id = line.ServiceId,
                    Name = line.Service?.Name ?? $"Service #{line.ServiceId}",
                    Quantity = line.Quantity,
                    OriginalCost = line.Cost,
                    Reason = line.Service == null
                        ? "This service has been removed from the catalogue and will not be included."
                        : "This service is no longer active on this service type and will not be included."
                });
            }

            // Extras resolve through the SAME rule the booking grid uses, so a custom
            // ("Pre-Arranged") order keeps offering the whole de-duplicated catalogue rather than
            // reporting every one of its informational extras as unavailable.
            var activeExtras = await _context.ExtraServices
                .Where(es => es.IsActive)
                .OrderBy(es => es.DisplayOrder)
                .ToListAsync();
            var selectableExtraIds = CatalogDtoMapper
                .ResolveSelectableExtraServices(serviceType, activeExtras)
                .Select(es => es.Id)
                .ToHashSet();

            var survivingExtras = new List<OrderExtraService>();
            foreach (var line in order.OrderExtraServices)
            {
                if (selectableExtraIds.Contains(line.ExtraServiceId))
                {
                    survivingExtras.Add(line);
                    continue;
                }

                preview.Unavailable.Add(new ReorderUnavailableLineDto
                {
                    Kind = "Extra",
                    Id = line.ExtraServiceId,
                    Name = line.ExtraService?.Name ?? $"Extra #{line.ExtraServiceId}",
                    Quantity = line.Quantity,
                    OriginalCost = line.Cost,
                    Reason = line.ExtraService == null
                        ? "This extra has been removed from the catalogue and will not be included."
                        : "This extra is no longer available on this service type and will not be included."
                });
            }

            // ── The prefill: the same job, with every discount slot deliberately empty ─────────
            preview.Prefill = BuildPrefill(order, serviceType, survivingServices, survivingExtras);
            preview.Prefill.ApartmentId = await ResolveStillExistingApartmentIdAsync(order);

            // ── Re-price it against today's catalogue ─────────────────────────────────────────
            var quoteInput = await OrderPricingInputBuilder.FromBookingDtoAsync(
                _context, serviceType, preview.Prefill, allowCustomPricing);
            var quote = OrderPricingCalculator.CalculateQuote(quoteInput);

            preview.Prefill.SubTotal = quote.SubTotal;
            preview.Prefill.TotalDuration = quote.TotalDuration;
            preview.Prefill.MaidsCount = quote.MaidsCount;

            var recreatedTotals = OrderPricingCalculator.CalculateTotals(new OrderPricingCalculator.TotalsInput
            {
                SubTotal = quote.SubTotal,
                TaxOverride = quote.TaxOverride,
                Tips = order.Tips
            });
            preview.Prefill.Tax = recreatedTotals.Tax;
            preview.Prefill.Total = recreatedTotals.Total;

            preview.Original = new ReorderTotalsDto
            {
                SubTotal = order.SubTotal,
                DiscountAmount = order.DiscountAmount,
                SubscriptionDiscountAmount = order.SubscriptionDiscountAmount,
                LoyaltyDiscountAmount = order.LoyaltyDiscountAmount,
                GiftCardAmountUsed = order.GiftCardAmountUsed,
                PointsRedeemedDiscount = order.PointsRedeemedDiscount,
                RewardBalanceUsed = order.RewardBalanceUsed,
                Tax = order.Tax,
                Tips = order.Tips,
                Total = order.Total,
                TotalDuration = order.TotalDuration,
                MaidsCount = order.MaidsCount
            };

            preview.Recreated = new ReorderTotalsDto
            {
                SubTotal = quote.SubTotal,
                Tax = recreatedTotals.Tax,
                Tips = order.Tips,
                Total = recreatedTotals.Total,
                TotalDuration = quote.TotalDuration,
                MaidsCount = quote.MaidsCount
            };

            // ── Per-line diff ─────────────────────────────────────────────────────────────────
            AddServiceLineChanges(preview, survivingServices, quote);
            AddExtraLineChanges(preview, survivingExtras, quote);

            // ── Discount slots ────────────────────────────────────────────────────────────────
            await AddDiscountChangesAsync(preview, order, quote.SubTotal);

            preview.HasChanges =
                preview.LineChanges.Count > 0 ||
                preview.Unavailable.Count > 0 ||
                preview.Discounts.Count > 0 ||
                preview.Original.Total != preview.Recreated.Total;

            return preview;
        }

        /// <summary>
        /// The source order as a booking request. Every discount-bearing field is left at its
        /// empty value ON PURPOSE — PromoCode, GiftCardCode, GiftCardAmountToUse,
        /// UserSpecialOfferId, SpecialOfferId, PointsToRedeem, UseCredits, CreditsToApply and
        /// ReferralCode. Clearing them here rather than in the UI means a client that posts the
        /// prefill straight through, skipping the preview screen, still cannot resurrect a stale
        /// discount.
        /// </summary>
        private static CreateBookingDto BuildPrefill(
            Order order,
            ServiceType serviceType,
            List<Models.OrderService> services,
            List<OrderExtraService> extras)
        {
            var dto = new CreateBookingDto
            {
                ServiceTypeId = order.ServiceTypeId,
                CustomServiceDisplayName = serviceType.IsCustom ? order.CustomServiceDisplayName : null,
                Services = services
                    .Select(s => new BookingServiceDto { ServiceId = s.ServiceId, Quantity = s.Quantity })
                    .ToList(),
                ExtraServices = extras
                    .Select(e => new BookingExtraServiceDto
                    {
                        ExtraServiceId = e.ExtraServiceId,
                        Quantity = e.Quantity,
                        Hours = e.Hours
                    })
                    .ToList(),
                // The plan the job was booked on is job metadata, not a discount: it is what makes
                // the recreated order count as recurring in the CRM. Whether it actually TAKES a
                // discount is decided server-side from the customer's live subscription, and is
                // suppressed unless the admin opts in.
                SubscriptionId = order.SubscriptionId ?? 0,
                ServiceDate = order.ServiceDate,
                ServiceTime = order.ServiceTime.ToString(@"hh\:mm"),
                EntryMethod = order.EntryMethod ?? "",
                SpecialInstructions = order.SpecialInstructions,
                ContactFirstName = order.ContactFirstName,
                ContactLastName = order.ContactLastName,
                // Empty string is not a valid [EmailAddress]; a no-email cash customer posts null.
                ContactEmail = string.IsNullOrWhiteSpace(order.ContactEmail) || NoEmailHelper.IsPlaceholder(order.ContactEmail)
                    ? null
                    : order.ContactEmail,
                ContactPhone = order.ContactPhone,
                ServiceAddress = order.ServiceAddress,
                AptSuite = order.AptSuite,
                City = order.City,
                State = order.State,
                ZipCode = order.ZipCode,
                ApartmentName = order.ApartmentName,
                Tips = order.Tips,
                BedroomsQuantity = order.BedroomsQuantity,
                BathroomsQuantity = order.BathroomsQuantity,
                PropertyType = order.PropertyType,
                LevelsQuantity = order.LevelsQuantity,
                FloorTypes = order.FloorTypes,
                FloorTypeOther = order.FloorTypeOther
            };

            if (serviceType.IsCustom)
            {
                // Custom Pricing stores the tax-INCLUSIVE amount the admin typed, split into
                // SubTotal + Tax that add back to it exactly — so recombining them recovers the
                // typed figure to the cent. Duration is stored as per-cleaner × cleaners, so the
                // form's per-cleaner field is the quotient. (A job that hit the one-hour floor
                // cannot be reversed exactly; the admin sees the number and can correct it.)
                dto.IsCustomPricing = true;
                dto.CustomAmount = order.SubTotal + order.Tax;
                dto.CustomCleaners = Math.Max(1, order.MaidsCount);
                dto.CustomDuration = order.TotalDuration / Math.Max(1, order.MaidsCount);
            }

            return dto;
        }

        /// <summary>Saved addresses get deleted. Pointing a new order at a dead ApartmentId is an
        /// FK violation at insert time, so an address that is gone becomes a plain typed one —
        /// the street/city/zip are already copied onto the order itself.</summary>
        private async Task<int?> ResolveStillExistingApartmentIdAsync(Order order)
        {
            if (order.ApartmentId == null) return null;
            var exists = await _context.Apartments
                .AsNoTracking()
                .AnyAsync(a => a.Id == order.ApartmentId.Value && a.UserId == order.UserId);
            return exists ? order.ApartmentId : null;
        }

        private static void AddServiceLineChanges(
            ReorderPreviewDto preview,
            List<Models.OrderService> original,
            OrderPricingCalculator.QuoteResult quote)
        {
            foreach (var line in original)
            {
                var repriced = quote.ServiceLines.FirstOrDefault(sl => sl.ServiceId == line.ServiceId);
                if (repriced == null) continue;
                if (repriced.Cost == line.Cost && repriced.Duration == line.Duration) continue;

                preview.LineChanges.Add(new ReorderLineChangeDto
                {
                    Kind = "Service",
                    Id = line.ServiceId,
                    Name = line.Service?.Name ?? $"Service #{line.ServiceId}",
                    Quantity = line.Quantity,
                    OriginalCost = line.Cost,
                    NewCost = repriced.Cost,
                    OriginalDuration = line.Duration,
                    NewDuration = repriced.Duration
                });
            }
        }

        private static void AddExtraLineChanges(
            ReorderPreviewDto preview,
            List<OrderExtraService> original,
            OrderPricingCalculator.QuoteResult quote)
        {
            foreach (var line in original)
            {
                var repriced = quote.ExtraServiceLines
                    .FirstOrDefault(el => el.ExtraServiceId == line.ExtraServiceId);
                if (repriced == null) continue;
                if (repriced.Cost == line.Cost && repriced.Duration == line.Duration) continue;

                preview.LineChanges.Add(new ReorderLineChangeDto
                {
                    Kind = "Extra",
                    Id = line.ExtraServiceId,
                    Name = line.ExtraService?.Name ?? $"Extra #{line.ExtraServiceId}",
                    Quantity = line.Quantity,
                    Hours = line.Hours,
                    OriginalCost = line.Cost,
                    NewCost = repriced.Cost,
                    OriginalDuration = line.Duration,
                    NewDuration = repriced.Duration
                });
            }
        }

        // ─────────────────────────────────────────────────────────────────────────────────────
        // Discount slots
        //
        // One entry per slot the SOURCE order actually used, plus loyalty/subscription whenever
        // the customer is entitled to one today (they can have become eligible since). Reasons are
        // written for an admin explaining the difference to a customer on the phone, so they name
        // the actual cause — "used on order #412", "expired 12 Mar 2026" — rather than a generic
        // "not available".
        // ─────────────────────────────────────────────────────────────────────────────────────
        private async Task AddDiscountChangesAsync(ReorderPreviewDto preview, Order order, decimal newSubTotal)
        {
            var user = order.User;

            await AddPromoCodeChangeAsync(preview, order);
            AddFirstTimeChange(preview, order, user);
            AddSpecialOfferChange(preview, order);
            await AddGiftCardChangeAsync(preview, order);
            AddBubblePointsChange(preview, order, user);
            AddRewardBalanceChange(preview, order, user);
            await AddLoyaltyChangeAsync(preview, order, user, newSubTotal);
            AddSubscriptionChange(preview, order, user, newSubTotal);
        }

        private async Task AddPromoCodeChangeAsync(ReorderPreviewDto preview, Order order)
        {
            // "firstUse" is the first-time marker, not a code; gift cards ride in the same column
            // under the XXXX-XXXX-XXXX format. Both are reported by their own slot below.
            if (string.IsNullOrWhiteSpace(order.PromoCode) ||
                string.Equals(order.PromoCode, "firstUse", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(order.PromoCode, order.GiftCardCode, StringComparison.OrdinalIgnoreCase))
                return;

            // Copied to a non-nullable local before the query: the null check above does not carry
            // into a lambda's captured state, so EF's expression would otherwise warn.
            var code = order.PromoCode;
            var lowered = code.ToLower();

            var promo = await _context.PromoCodes
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Code.ToLower() == lowered);

            string reason;
            if (promo == null)
                reason = $"Promo code \"{code}\" no longer exists.";
            else if (!promo.IsActive)
                reason = $"Promo code \"{code}\" has been deactivated.";
            else if (promo.ValidTo.HasValue && promo.ValidTo.Value < DateTime.UtcNow)
                reason = $"Promo code \"{code}\" expired on {promo.ValidTo.Value:d MMM yyyy}.";
            else if (promo.ValidFrom.HasValue && promo.ValidFrom.Value > DateTime.UtcNow)
                reason = $"Promo code \"{code}\" is not valid until {promo.ValidFrom.Value:d MMM yyyy}.";
            else if (promo.MaxUsageCount.HasValue && promo.CurrentUsageCount >= promo.MaxUsageCount.Value)
                reason = $"Promo code \"{code}\" has reached its usage limit ({promo.CurrentUsageCount}/{promo.MaxUsageCount}).";
            else
                reason = $"Promo code \"{code}\" is still valid, but promo codes are never carried over automatically — apply it on the order afterwards if it should still count.";

            preview.Discounts.Add(new ReorderDiscountChangeDto
            {
                Kind = "PromoCode",
                Label = $"Promo code \"{code}\"",
                OriginalAmount = order.DiscountAmount,
                AvailableAmount = 0,
                CanReapply = false,
                Reason = reason
            });
        }

        private static void AddFirstTimeChange(ReorderPreviewDto preview, Order order, User? user)
        {
            var wasFirstTime = string.Equals(order.PromoCode, "firstUse", StringComparison.OrdinalIgnoreCase);
            if (!wasFirstTime) return;

            preview.Discounts.Add(new ReorderDiscountChangeDto
            {
                Kind = "FirstTime",
                Label = "First-time customer discount",
                OriginalAmount = order.DiscountAmount,
                AvailableAmount = 0,
                CanReapply = false,
                Reason = user != null && user.FirstTimeOrder
                    ? "This customer is still flagged as first-time, but the first-time discount is a one-off and is not carried over onto a recreated order."
                    : "This customer is no longer a first-time customer, so the first-time discount cannot apply again."
            });
        }

        private static void AddSpecialOfferChange(ReorderPreviewDto preview, Order order)
        {
            if (order.UserSpecialOfferId == null && string.IsNullOrWhiteSpace(order.SpecialOfferName)) return;
            // A first-time discount also stamps SpecialOfferName; it already has its own slot.
            if (string.Equals(order.PromoCode, "firstUse", StringComparison.OrdinalIgnoreCase)) return;

            preview.Discounts.Add(new ReorderDiscountChangeDto
            {
                Kind = "SpecialOffer",
                Label = string.IsNullOrWhiteSpace(order.SpecialOfferName)
                    ? "Special offer"
                    : $"Special offer \"{order.SpecialOfferName}\"",
                OriginalAmount = order.DiscountAmount,
                AvailableAmount = 0,
                CanReapply = false,
                Reason = "The special offer granted to this customer was consumed by the original order and cannot be used twice."
            });
        }

        private async Task AddGiftCardChangeAsync(ReorderPreviewDto preview, Order order)
        {
            if (string.IsNullOrWhiteSpace(order.GiftCardCode) && order.GiftCardAmountUsed <= 0) return;

            var code = order.GiftCardCode;
            var balance = 0m;
            var found = false;
            if (!string.IsNullOrWhiteSpace(code))
            {
                var lookup = code;
                var card = await _context.GiftCards
                    .AsNoTracking()
                    .FirstOrDefaultAsync(g => g.Code == lookup);
                if (card != null)
                {
                    found = true;
                    balance = card.CurrentBalance;
                }
            }

            var reason = !found
                ? "The gift card used on the original order can no longer be found."
                : balance <= 0
                    ? $"Gift card {code} has no balance left ($0.00 remaining)."
                    : $"Gift card {code} still has ${balance:0.00} on it, but gift cards are never applied automatically — enter the code on the order afterwards to draw on it.";

            preview.Discounts.Add(new ReorderDiscountChangeDto
            {
                Kind = "GiftCard",
                Label = string.IsNullOrWhiteSpace(code) ? "Gift card" : $"Gift card {code}",
                OriginalAmount = order.GiftCardAmountUsed,
                AvailableAmount = 0,
                CanReapply = false,
                Reason = reason
            });
        }

        private static void AddBubblePointsChange(ReorderPreviewDto preview, Order order, User? user)
        {
            if (order.PointsRedeemed <= 0 && order.PointsRedeemedDiscount <= 0) return;

            preview.Discounts.Add(new ReorderDiscountChangeDto
            {
                Kind = "BubblePoints",
                Label = $"Bubble points ({order.PointsRedeemed:N0} redeemed)",
                OriginalAmount = order.PointsRedeemedDiscount,
                AvailableAmount = 0,
                CanReapply = false,
                Reason = user == null
                    ? "Points redeemed on the original order were spent then and are not carried over."
                    : $"Those points were spent on the original order. The customer has {user.BubblePoints:N0} points now — redeem them from their profile if they should be used again."
            });
        }

        private static void AddRewardBalanceChange(ReorderPreviewDto preview, Order order, User? user)
        {
            if (order.RewardBalanceUsed <= 0) return;

            preview.Discounts.Add(new ReorderDiscountChangeDto
            {
                Kind = "RewardBalance",
                Label = "Reward balance",
                OriginalAmount = order.RewardBalanceUsed,
                AvailableAmount = 0,
                CanReapply = false,
                Reason = user == null
                    ? "Reward balance spent on the original order is not carried over."
                    : $"That balance was spent on the original order. The customer has ${user.BubbleCredits:0.00} of reward balance now — apply it from their profile if it should be used again."
            });
        }

        private async Task AddLoyaltyChangeAsync(
            ReorderPreviewDto preview, Order order, User? user, decimal newSubTotal)
        {
            var hadLoyalty = order.LoyaltyDiscountAmount > 0;

            var candidate = 0m;
            var percentage = 0m;
            if (user != null)
                (candidate, percentage) = await _loyaltyDiscountService.CalculateForOrderAsync(user.Id, newSubTotal);

            // Nothing to say when the order never had one and the customer has none now.
            if (!hadLoyalty && candidate <= 0) return;

            var reason = candidate > 0
                ? $"This customer currently has a {percentage:0.##}% loyalty discount available (${candidate:0.00} on this order). It is OFF by default on a recreated order — tick \"apply current discounts\" to use it, which will consume it."
                : hadLoyalty
                    ? "The loyalty discount used on the original order was consumed by it, and the customer has no loyalty discount available right now."
                    : "No loyalty discount is available on this account.";

            preview.Discounts.Add(new ReorderDiscountChangeDto
            {
                Kind = "Loyalty",
                Label = "Loyalty discount",
                OriginalAmount = order.LoyaltyDiscountAmount,
                AvailableAmount = candidate,
                CanReapply = candidate > 0,
                Reason = reason
            });
        }

        private static void AddSubscriptionChange(
            ReorderPreviewDto preview, Order order, User? user, decimal newSubTotal)
        {
            var hadSubscriptionDiscount = order.SubscriptionDiscountAmount > 0;

            // Same rule BookingCreationService.ResolveDiscountsAsync applies: the discount is only
            // real when the ORDER OWNER'S live, unexpired plan matches the tier on the order.
            var tier = order.Subscription;
            var subscriptionActive = user?.SubscriptionId != null &&
                (!user.SubscriptionExpiryDate.HasValue || user.SubscriptionExpiryDate.Value >= DateTime.UtcNow);
            var available = 0m;
            var percentage = 0m;
            if (tier != null && tier.SubscriptionDays > 0 && tier.DiscountPercentage > 0 && subscriptionActive &&
                user!.SubscriptionId == tier.Id)
            {
                percentage = tier.DiscountPercentage;
                available = OrderPricingCalculator.Round2(newSubTotal * percentage / 100m);
            }

            if (!hadSubscriptionDiscount && available <= 0) return;

            var reason = available > 0
                ? $"The customer is still on {tier!.Name} ({percentage:0.##}% off, ${available:0.00} on this order). It is OFF by default on a recreated order — tick \"apply current discounts\" to use it."
                : hadSubscriptionDiscount
                    ? $"The recurring-plan discount on the original order no longer applies — this customer is not on {(tier != null ? tier.Name : "that plan")} any more."
                    : "No recurring-plan discount applies to this customer right now.";

            preview.Discounts.Add(new ReorderDiscountChangeDto
            {
                Kind = "Subscription",
                Label = tier != null ? $"{tier.Name} plan discount" : "Recurring plan discount",
                OriginalAmount = order.SubscriptionDiscountAmount,
                AvailableAmount = available,
                CanReapply = available > 0,
                Reason = reason
            });
        }
    }
}
