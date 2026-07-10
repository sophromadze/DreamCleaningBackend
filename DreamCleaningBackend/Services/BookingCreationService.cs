using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services
{
    /// <summary>Flow-specific knobs for order creation. Defaults reproduce the
    /// self-service booking flow (Stripe, status Pending).</summary>
    public class BookingCreationOptions
    {
        public string InitialStatus { get; set; } = "Pending";
        public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.Normal;
        public string? PaymentReference { get; set; }
        public string? PaymentNotes { get; set; }
        /// <summary>Admin who recorded a manual payment (create-for-user flow).</summary>
        public int? ManualPaymentRecordedByUserId { get; set; }
        /// <summary>Admin creating the order via create-for-user (any payment method).
        /// Null = the customer booked it themselves.</summary>
        public int? BookedByAdminUserId { get; set; }
    }

    public interface IBookingCreationService
    {
        /// <summary>
        /// Creates and persists an order from a booking DTO: pricing through the shared
        /// calculator, loyalty stacking, special-offer consumption and gift-card
        /// application, all inside one transaction. Flow-specific follow-ups
        /// (subscription activation, notifications, payment intents) stay at call sites.
        /// </summary>
        Task<Order> CreateOrderAsync(CreateBookingDto dto, int orderUserId, BookingCreationOptions? options = null);

        /// <summary>
        /// A promo code matching the XXXX-XXXX-XXXX gift card format is treated as a gift
        /// card. The single definition of that rule — also used by prepare-payment and the
        /// controllers' promo-minimum validation.
        /// </summary>
        (string? promoCode, string? giftCardCode, decimal giftCardAmountToUse) ResolveGiftCardAndPromo(CreateBookingDto dto);

        /// <summary>
        /// Server-side derivation of the promo/first-time/special-offer discount and the
        /// subscription discount from the DB — the client's dollar figures are never trusted,
        /// only WHICH code/offer/subscription was selected. Same trust model as
        /// LoyaltyDiscountService.CalculateForOrderAsync (loyalty itself stays there).
        /// Throws InvalidOperationException when a claimed discount is invalid, so the caller
        /// rejects the booking instead of silently charging more than the client previewed.
        /// </summary>
        Task<(decimal discountAmount, decimal subscriptionDiscountAmount)> ResolveDiscountsAsync(
            CreateBookingDto dto, int orderUserId, decimal subTotal);
    }

    /// <summary>
    /// SINGLE SOURCE OF TRUTH for creating an order from a booking request. Used by
    /// BookingController's create, create-for-user, and confirm-payment (guest) flows —
    /// do not re-implement order construction / gift card / special offer handling there.
    /// </summary>
    public class BookingCreationService : IBookingCreationService
    {
        private readonly ApplicationDbContext _context;
        private readonly ILoyaltyDiscountService _loyaltyDiscountService;
        private readonly IGiftCardService _giftCardService;
        private readonly ILogger<BookingCreationService> _logger;

        public BookingCreationService(
            ApplicationDbContext context,
            ILoyaltyDiscountService loyaltyDiscountService,
            IGiftCardService giftCardService,
            ILogger<BookingCreationService> logger)
        {
            _context = context;
            _loyaltyDiscountService = loyaltyDiscountService;
            _giftCardService = giftCardService;
            _logger = logger;
        }

        public (string? promoCode, string? giftCardCode, decimal giftCardAmountToUse) ResolveGiftCardAndPromo(CreateBookingDto dto)
        {
            string? giftCardCode = dto.GiftCardCode;
            string? promoCode = dto.PromoCode;

            if (!string.IsNullOrEmpty(dto.PromoCode) &&
                string.IsNullOrEmpty(dto.GiftCardCode) &&
                System.Text.RegularExpressions.Regex.IsMatch(dto.PromoCode, @"^[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}$"))
            {
                giftCardCode = dto.PromoCode;
                promoCode = null;
            }

            // No gift card code — no gift card draw. Without this a request could claim a
            // GiftCardAmountToUse with no card at all and shrink the charged total.
            var giftCardAmountToUse = string.IsNullOrEmpty(giftCardCode) ? 0m : dto.GiftCardAmountToUse;

            return (promoCode, giftCardCode, giftCardAmountToUse);
        }

        public async Task<(decimal discountAmount, decimal subscriptionDiscountAmount)> ResolveDiscountsAsync(
            CreateBookingDto dto, int orderUserId, decimal subTotal)
        {
            var (promoCode, _, _) = ResolveGiftCardAndPromo(dto);

            // Promo slot priority mirrors the booking page's calculateTotal():
            // special offer > first-time marker ("firstUse") > regular promo code.
            var discountAmount = 0m;

            if (dto.UserSpecialOfferId.HasValue && dto.UserSpecialOfferId.Value > 0)
            {
                var userOffer = await _context.UserSpecialOffers
                    .AsNoTracking()
                    .Include(uso => uso.SpecialOffer)
                    .FirstOrDefaultAsync(uso =>
                        uso.Id == dto.UserSpecialOfferId.Value &&
                        uso.UserId == orderUserId &&
                        !uso.IsUsed);

                if (userOffer == null || !userOffer.SpecialOffer.IsActive)
                    throw new InvalidOperationException("The selected special offer is not available on this account.");
                if (userOffer.ExpiresAt.HasValue && userOffer.ExpiresAt.Value < DateTime.UtcNow)
                    throw new InvalidOperationException("The selected special offer has expired.");

                discountAmount = userOffer.SpecialOffer.IsPercentage
                    ? OrderPricingCalculator.Round2(subTotal * userOffer.SpecialOffer.DiscountValue / 100m)
                    : Math.Min(userOffer.SpecialOffer.DiscountValue, subTotal);
            }
            else if (dto.SpecialOfferId.HasValue && dto.SpecialOfferId.Value > 0)
            {
                // Public special offer (guest flow — no per-user grant). Same eligibility
                // rules as the /special-offers/public listing endpoint.
                var now = DateTime.UtcNow;
                var offer = await _context.SpecialOffers
                    .AsNoTracking()
                    .FirstOrDefaultAsync(o => o.Id == dto.SpecialOfferId.Value &&
                                              o.IsActive &&
                                              (o.ValidFrom == null || o.ValidFrom <= now) &&
                                              (o.ValidTo == null || o.ValidTo > now));

                if (offer == null)
                    throw new InvalidOperationException("The selected special offer is no longer available.");
                if (offer.MinimumOrderAmount.HasValue && subTotal < offer.MinimumOrderAmount.Value)
                    throw new InvalidOperationException(
                        $"Minimum order amount of ${offer.MinimumOrderAmount.Value:0.##} required to use this special offer.");
                if (offer.RequiresFirstTimeCustomer)
                {
                    var isFirstTimeCustomer = await _context.Users
                        .AsNoTracking()
                        .Where(u => u.Id == orderUserId)
                        .Select(u => (bool?)u.FirstTimeOrder)
                        .FirstOrDefaultAsync() ?? false;
                    if (!isFirstTimeCustomer)
                        throw new InvalidOperationException("This special offer is for first-time customers only.");
                }

                discountAmount = offer.IsPercentage
                    ? OrderPricingCalculator.Round2(subTotal * offer.DiscountValue / 100m)
                    : Math.Min(offer.DiscountValue, subTotal);
            }
            else if (promoCode == "firstUse")
            {
                // First-time discount: the user must actually still be a first-time customer,
                // and the percentage comes from their granted FirstTime special offer in the DB.
                var isFirstTime = await _context.Users
                    .AsNoTracking()
                    .Where(u => u.Id == orderUserId)
                    .Select(u => (bool?)u.FirstTimeOrder)
                    .FirstOrDefaultAsync() ?? false;

                var firstTimeOffer = await _context.UserSpecialOffers
                    .AsNoTracking()
                    .Include(uso => uso.SpecialOffer)
                    .Where(uso => uso.UserId == orderUserId &&
                                  !uso.IsUsed &&
                                  uso.SpecialOffer.IsActive &&
                                  uso.SpecialOffer.Type == OfferType.FirstTime)
                    .OrderByDescending(uso => uso.GrantedAt)
                    .FirstOrDefaultAsync();

                if (!isFirstTime || firstTimeOffer == null)
                    throw new InvalidOperationException("The first-time discount is no longer available on this account.");

                discountAmount = firstTimeOffer.SpecialOffer.IsPercentage
                    ? OrderPricingCalculator.Round2(subTotal * firstTimeOffer.SpecialOffer.DiscountValue / 100m)
                    : Math.Min(firstTimeOffer.SpecialOffer.DiscountValue, subTotal);
            }
            else if (!string.IsNullOrEmpty(promoCode) && !promoCode.StartsWith("SPECIAL_OFFER:", StringComparison.OrdinalIgnoreCase))
            {
                // Same checks as the validate-promo endpoint, re-run at charge time.
                var pc = await _context.PromoCodes
                    .AsNoTracking()
                    .FirstOrDefaultAsync(p => p.Code.ToLower() == promoCode.ToLower() && p.IsActive);

                if (pc == null)
                    throw new InvalidOperationException("Invalid promo code.");
                if (pc.ValidFrom.HasValue && pc.ValidFrom.Value > DateTime.UtcNow)
                    throw new InvalidOperationException("Promo code is not yet valid.");
                if (pc.ValidTo.HasValue && pc.ValidTo.Value < DateTime.UtcNow)
                    throw new InvalidOperationException("Promo code has expired.");
                if (pc.MaxUsageCount.HasValue && pc.CurrentUsageCount >= pc.MaxUsageCount.Value)
                    throw new InvalidOperationException("Promo code usage limit reached.");

                discountAmount = pc.IsPercentage
                    ? OrderPricingCalculator.Round2(subTotal * pc.DiscountValue / 100m)
                    : pc.DiscountValue;
            }

            // Subscription discount: only when the ORDER OWNER's active (non-expired)
            // subscription matches the selected tier — the booking page's rule.
            var subscriptionDiscountAmount = 0m;
            if (dto.SubscriptionId > 0)
            {
                var selected = await _context.Subscriptions
                    .AsNoTracking()
                    .FirstOrDefaultAsync(s => s.Id == dto.SubscriptionId);

                if (selected != null && selected.SubscriptionDays > 0 && selected.DiscountPercentage > 0)
                {
                    var owner = await _context.Users
                        .AsNoTracking()
                        .Include(u => u.Subscription)
                        .FirstOrDefaultAsync(u => u.Id == orderUserId);

                    var hasActiveSubscription = owner?.SubscriptionId != null &&
                        (!owner.SubscriptionExpiryDate.HasValue || owner.SubscriptionExpiryDate.Value >= DateTime.UtcNow);

                    if (hasActiveSubscription && owner!.Subscription != null &&
                        owner.Subscription.SubscriptionDays == selected.SubscriptionDays)
                    {
                        subscriptionDiscountAmount = OrderPricingCalculator.Round2(
                            subTotal * selected.DiscountPercentage / 100m);
                    }
                }
            }

            return (discountAmount, subscriptionDiscountAmount);
        }

        public async Task<Order> CreateOrderAsync(CreateBookingDto dto, int orderUserId, BookingCreationOptions? options = null)
        {
            options ??= new BookingCreationOptions();
            var manualPayment = options.PaymentMethod != PaymentMethod.Normal;

            var serviceType = await _context.ServiceTypes
                .Include(st => st.Services)
                .FirstOrDefaultAsync(st => st.Id == dto.ServiceTypeId)
                ?? throw new InvalidOperationException("Invalid service type");

            var (promoCode, giftCardCode, giftCardAmountUsed) = ResolveGiftCardAndPromo(dto);

            // Price through the shared calculator (single source of truth).
            var quoteInput = await OrderPricingInputBuilder.FromBookingDtoAsync(_context, serviceType, dto);
            var quote = OrderPricingCalculator.CalculateQuote(quoteInput);

            if (Math.Abs(dto.TotalDuration - quote.TotalDuration) > 5)
            {
                _logger.LogWarning($"Duration mismatch — frontend sent {dto.TotalDuration}, backend calculated {quote.TotalDuration}. Backend value wins.");
            }

            // Promo/first-time/special-offer and subscription discounts are derived from the
            // DB against the backend subtotal — dto.DiscountAmount / dto.SubscriptionDiscountAmount
            // are never trusted (same model as the loyalty slot below).
            var (discountAmount, subscriptionDiscountAmount) =
                await ResolveDiscountsAsync(dto, orderUserId, quote.SubTotal);

            var order = new Order
            {
                UserId = orderUserId,
                ServiceTypeId = dto.ServiceTypeId,
                // Per-order display label, only meaningful for the custom service type.
                CustomServiceDisplayName = serviceType.IsCustom
                    ? (string.IsNullOrWhiteSpace(dto.CustomServiceDisplayName) ? null : dto.CustomServiceDisplayName.Trim())
                    : null,
                ApartmentId = dto.ApartmentId,
                ApartmentName = dto.ApartmentName,
                ServiceAddress = dto.ServiceAddress,
                AptSuite = dto.AptSuite,
                City = dto.City,
                State = dto.State,
                ZipCode = dto.ZipCode,
                ServiceDate = dto.ServiceDate,
                ServiceTime = TimeSpan.Parse(dto.ServiceTime),
                EntryMethod = dto.EntryMethod,
                SpecialInstructions = dto.SpecialInstructions,
                FloorTypes = dto.FloorTypes,
                FloorTypeOther = dto.FloorTypeOther,
                ContactFirstName = dto.ContactFirstName,
                ContactLastName = dto.ContactLastName,
                // Empty (never null) for no-email cash customers — the column is non-nullable.
                ContactEmail = dto.ContactEmail?.Trim() ?? "",
                ContactPhone = dto.ContactPhone,
                PromoCode = promoCode,
                GiftCardCode = giftCardCode,
                GiftCardAmountUsed = 0,
                Tips = dto.Tips,
                CompanyDevelopmentTips = dto.CompanyDevelopmentTips,
                Status = options.InitialStatus,
                // Secret for tokenized payment links — lets the emailed/SMSed link open the
                // payment page without login while the order still has something unpaid.
                PaymentAccessToken = PaymentLinkHelper.GenerateToken(),
                OrderDate = DateTime.UtcNow,
                SubscriptionId = dto.SubscriptionId == 0 ? null : (int?)dto.SubscriptionId,
                OrderServices = new List<Models.OrderService>(),
                OrderExtraServices = new List<OrderExtraService>(),
                CreatedAt = DateTime.UtcNow,
                IsPaid = false, // IsPaid is Stripe-only; manual payments leave it false too
                // Manual payment tracking — only stamped when paymentMethod != Normal.
                PaymentMethod = options.PaymentMethod,
                PaymentReference = manualPayment ? options.PaymentReference : null,
                PaymentNotes = manualPayment ? options.PaymentNotes : null,
                ManualPaymentRecordedAt = manualPayment ? DateTime.UtcNow : (DateTime?)null,
                ManualPaymentRecordedByUserId = manualPayment ? options.ManualPaymentRecordedByUserId : null,
                // Who booked it: the create-for-user flow passes the admin; self-service leaves null.
                BookedByAdminUserId = options.BookedByAdminUserId,
                // Backend-authoritative pricing from the shared calculator (Tax/Total are
                // finalized below after loyalty stacking).
                MaidsCount = quote.MaidsCount,
                TotalDuration = quote.TotalDuration,
                SubTotal = quote.SubTotal,
                DiscountAmount = discountAmount, // promo/first-time discount ONLY (server-derived)
                SubscriptionDiscountAmount = subscriptionDiscountAmount,
                BedroomsQuantity = dto.BedroomsQuantity,
                BathroomsQuantity = dto.BathroomsQuantity
            };

            // Persist per-line costs/durations from the shared calculator.
            OrderPricingCalculator.AddOrderLinesFromQuote(order, quote);

            // Loyalty Discount + stacking for the ORDER OWNER (for admin-on-behalf bookings
            // that's the target customer — the admin's own discount must never leak in).
            // Must run before the tax/total math so tax sees the post-stacking slate.
            await ApplyLoyaltyDiscountAndStackingAsync(order, orderUserId);

            var totals = OrderPricingCalculator.CalculateTotals(new OrderPricingCalculator.TotalsInput
            {
                SubTotal = order.SubTotal,
                DiscountAmount = order.DiscountAmount,
                SubscriptionDiscountAmount = order.SubscriptionDiscountAmount,
                LoyaltyDiscountAmount = order.LoyaltyDiscountAmount,
                Tips = order.Tips,
                CompanyDevelopmentTips = order.CompanyDevelopmentTips,
                GiftCardAmountUsed = giftCardAmountUsed
            });
            order.Tax = totals.Tax;
            order.Total = totals.Total;

            // Cleaner salary defaults from the shared calculator.
            order.CleanerHourlyRate = OrderPricingCalculator.GetDefaultCleanerHourlyRate(quote.DeepCleaningFee);
            order.CleanerTotalSalary = OrderPricingCalculator.CalculateCleanerTotalSalary(
                order.TotalDuration, order.MaidsCount, quote.HasCleanerService, order.CleanerHourlyRate);

            // One transaction so order, special offer and gift card usage land together.
            using (var transaction = await _context.Database.BeginTransactionAsync())
            {
                try
                {
                    _context.Orders.Add(order);
                    await _context.SaveChangesAsync();

                    await ConsumeSpecialOfferAsync(dto, orderUserId, order);
                    await ApplyGiftCardAsync(order, giftCardCode, giftCardAmountUsed, totals.TotalBeforeGiftCard, orderUserId);

                    await transaction.CommitAsync();
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    var innerMsg = ex.InnerException?.InnerException?.Message ?? ex.InnerException?.Message ?? "";
                    throw new InvalidOperationException(
                        $"Failed to create order: {ex.Message}" + (string.IsNullOrEmpty(innerMsg) ? "" : $" | Detail: {innerMsg}"));
                }
            }

            // The customer just booked, so any open win-back alert for them is moot — auto-resolve
            // it so the CRM automation feed doesn't keep showing a stale "hasn't ordered" prompt.
            // Best-effort: a failure here must never undo the committed order.
            await ResolveOpenWinbackAlertsAsync(orderUserId);

            return order;
        }

        // Closes any Open/Snoozed win-back alert for a customer who has just placed an order.
        // The win-back alert's Reason text is a frozen snapshot from when it was created, and the
        // evaluator skips users who already have an open alert — so without this the alert would
        // linger with a stale last-order date until an admin cleared it by hand.
        private async Task ResolveOpenWinbackAlertsAsync(int userId)
        {
            try
            {
                var openAlerts = await _context.AutomationAlerts
                    .Where(a => a.UserId == userId
                                && a.RuleKey == AutomationRuleKeys.Winback
                                && (a.Status == AutomationAlertStatus.Open
                                    || a.Status == AutomationAlertStatus.Snoozed))
                    .ToListAsync();

                if (openAlerts.Count == 0) return;

                foreach (var alert in openAlerts)
                {
                    alert.Status = AutomationAlertStatus.Done;
                    alert.RemindAt = null;
                    alert.ResolvedAt = DateTime.UtcNow;
                    alert.ResolvedByAdminId = null;
                    alert.ResolvedByAdminName = "System (customer rebooked)";
                }

                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to auto-resolve win-back alerts for user {UserId} after booking.", userId);
            }
        }

        // Resolves the loyalty discount + stacking for the target user, then mutates the
        // order's three discount slots to the post-stacking values. The backend is
        // authoritative: any client-supplied LoyaltyDiscountAmount is ignored.
        private async Task ApplyLoyaltyDiscountAndStackingAsync(Order order, int targetUserId)
        {
            var (loyaltyCandidate, loyaltyPct) =
                await _loyaltyDiscountService.CalculateForOrderAsync(targetUserId, order.SubTotal);

            var (loyaltyAmount, finalLoyaltyPct, subscriptionAmount, promoAmount) =
                _loyaltyDiscountService.ResolveStacking(
                    loyaltyCandidate, loyaltyPct,
                    order.SubscriptionDiscountAmount, order.DiscountAmount);

            order.LoyaltyDiscountAmount = loyaltyAmount;
            order.LoyaltyDiscountPercentage = finalLoyaltyPct;
            order.SubscriptionDiscountAmount = subscriptionAmount;
            order.DiscountAmount = promoAmount;
        }

        // Marks the user's special offer as used and stamps the offer name onto the order.
        private async Task ConsumeSpecialOfferAsync(CreateBookingDto dto, int orderUserId, Order order)
        {
            if (!dto.UserSpecialOfferId.HasValue || dto.UserSpecialOfferId.Value <= 0)
                return;

            var userSpecialOffer = await _context.UserSpecialOffers
                .FirstOrDefaultAsync(uso =>
                    uso.Id == dto.UserSpecialOfferId.Value &&
                    uso.UserId == orderUserId &&
                    !uso.IsUsed);

            if (userSpecialOffer != null)
            {
                userSpecialOffer.IsUsed = true;
                userSpecialOffer.UsedAt = DateTime.UtcNow;
                userSpecialOffer.UsedOnOrderId = order.Id;
                await _context.SaveChangesAsync();
            }

            var specialOfferDetails = await _context.UserSpecialOffers
                .Include(uso => uso.SpecialOffer)
                .FirstOrDefaultAsync(uso => uso.Id == dto.UserSpecialOfferId.Value);

            if (specialOfferDetails != null)
            {
                // Prefix identifies a special offer (not a regular promo code).
                order.PromoCode = $"SPECIAL_OFFER:{specialOfferDetails.SpecialOffer.Name}";
                await _context.SaveChangesAsync();
            }
        }

        // Draws the gift card against the pre-gift-card total and reconciles the order
        // when the actually-used amount differs from what the client requested.
        private async Task ApplyGiftCardAsync(Order order, string? giftCardCode, decimal giftCardAmountUsed, decimal totalBeforeGiftCard, int orderUserId)
        {
            if (string.IsNullOrEmpty(giftCardCode) || giftCardAmountUsed <= 0)
                return;

            var actualAmountUsed = await _giftCardService.ApplyGiftCardToOrder(
                giftCardCode,
                totalBeforeGiftCard,
                order.Id,
                orderUserId
            );

            order.GiftCardAmountUsed = actualAmountUsed;
            if (actualAmountUsed != giftCardAmountUsed)
            {
                // Same rounding + clamping the calculator applies everywhere else.
                order.Total = OrderPricingCalculator.Round2(Math.Max(0m, totalBeforeGiftCard - actualAmountUsed));
            }
            await _context.SaveChangesAsync();
        }
    }
}
