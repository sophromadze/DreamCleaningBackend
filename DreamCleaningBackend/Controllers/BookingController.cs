using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using DreamCleaningBackend.Services;
using Microsoft.Extensions.DependencyInjection;
using DreamCleaningBackend.Hubs;
using Microsoft.AspNetCore.SignalR;
using System.Linq;
using Stripe;
// Stripe also defines a PaymentMethod type. Alias to our domain enum so unqualified
// PaymentMethod references in this file (Phase 1 manual payment tracking) resolve
// unambiguously — Stripe.PaymentMethod isn't referenced directly here anyway.
using PaymentMethod = DreamCleaningBackend.Models.PaymentMethod;

namespace DreamCleaningBackend.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class BookingController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly ISubscriptionService _subscriptionService;
        private readonly IGiftCardService _giftCardService;
        private readonly IEmailService _emailService;
        private readonly IBookingDataService _bookingDataService;
        private readonly IStripeService _stripeService;
        private readonly ISmsService _smsService;
        private readonly ILogger<BookingController> _logger;
        private readonly IHubContext<UserManagementHub> _hubContext;
        private readonly IAuthService _authService;
        private readonly IReferralService _referralService;
        private readonly IUserCleaningPhotoService _userCleaningPhotoService;
        private readonly ILoyaltyDiscountService _loyaltyDiscountService;
        private readonly IBookingCreationService _bookingCreationService;
        private readonly IAdminBonusService _adminBonusService;
        // Used ONLY by the admin create-for-user path: a customer booking their own cleaning is
        // not an admin action, so the customer flow deliberately writes no audit row.
        private readonly IAuditService _auditService;

        // Fire-and-forget notifications get a DI scope of their own through
        // Helpers/BackgroundWork — never this controller's scoped DbContext. See that file for
        // the double-charge incident behind the rule.
        private readonly IServiceScopeFactory _scopeFactory;

        // Stripe rejects any charge below $0.50 USD. When the payable total falls under this
        // (a gift card / credits fully cover the order), we skip Stripe entirely and treat the
        // order as fully paid — the customer pays nothing. Any sub-minimum remainder is waived.
        private const decimal StripeMinimumChargeAmount = 0.50m;

        public BookingController(ApplicationDbContext context,
            IConfiguration configuration,
            ISubscriptionService subscriptionService,
            IGiftCardService giftCardService,
            IEmailService emailService,
            IBookingDataService bookingDataService,
            IStripeService stripeService,
            ISmsService smsService,
            ILogger<BookingController> logger,
            IHubContext<UserManagementHub> hubContext,
            IAuthService authService,
            IReferralService referralService,
            IUserCleaningPhotoService userCleaningPhotoService,
            ILoyaltyDiscountService loyaltyDiscountService,
            IBookingCreationService bookingCreationService,
            IAdminBonusService adminBonusService,
            IAuditService auditService,
            IServiceScopeFactory scopeFactory)
        {
            _context = context;
            _configuration = configuration;
            _subscriptionService = subscriptionService;
            _giftCardService = giftCardService;
            _emailService = emailService;
            _bookingDataService = bookingDataService;
            _stripeService = stripeService;
            _smsService = smsService;
            _logger = logger;
            _hubContext = hubContext;
            _authService = authService;
            _referralService = referralService;
            _userCleaningPhotoService = userCleaningPhotoService;
            _loyaltyDiscountService = loyaltyDiscountService;
            _bookingCreationService = bookingCreationService;
            _adminBonusService = adminBonusService;
            _auditService = auditService;
            _scopeFactory = scopeFactory;
        }

        // Mirrors AuthController.SetAuthCookies — used by the guest auto-registration path in
        // cookie-auth mode so a freshly created guest gets the same httpOnly session cookies a
        // normal login would. Kept in sync with AuthController (HttpOnly, Secure off only when
        // Development:UseHttp, SameSite=Strict, 30-day expiry to match the refresh token).
        private void SetGuestAuthCookies(string token, string refreshToken)
        {
            var secure = !_configuration.GetValue<bool>("Development:UseHttp", false);
            var cookieOptions = new CookieOptions
            {
                HttpOnly = true,
                Secure = secure,
                SameSite = SameSiteMode.Strict,
                Expires = DateTime.UtcNow.AddDays(30)
            };

            Response.Cookies.Append("access_token", token, cookieOptions);
            Response.Cookies.Append("refresh_token", refreshToken, cookieOptions);
        }

        // Order creation (incl. loyalty stacking) lives in BookingCreationService —
        // see _bookingCreationService.CreateOrderAsync.

        // ===== Shared pricing (single source of truth: OrderPricingCalculator) =====
        // DTO→input mapping lives in OrderPricingInputBuilder; per-line persistence in
        // OrderPricingCalculator.AddOrderLinesFromQuote. ALL pricing flows in this
        // controller must price through these — never inline subtotal/tax/duration math.
        private Task<OrderPricingCalculator.QuoteInput> BuildQuoteInputAsync(
            ServiceType serviceType, CreateBookingDto dto, bool allowCustomPricing)
            => OrderPricingInputBuilder.FromBookingDtoAsync(_context, serviceType, dto, allowCustomPricing);

        // ===== Custom ("Pre-Arranged") pricing gate =====
        //
        // Custom pricing lets the caller name the price outright — the calculator's custom branch
        // skips the catalogue, the rate tiers and the MinimumPrice floor. The rule and its rationale
        // live in OrderPricingInputBuilder.ShouldHonourCustomPricing; these helpers only supply
        // half (b), "is the caller an Admin/SuperAdmin".

        /// <summary>
        /// Roles permitted to price through the custom branch. Moderator is deliberately excluded:
        /// it is a View-only role in PermissionService, and the booking UI already offers the
        /// custom service type to Admin/SuperAdmin only, so no working flow is lost.
        /// </summary>
        private static bool MayUseCustomPricing(UserRole? role)
            => role == UserRole.Admin || role == UserRole.SuperAdmin;

        /// <summary>
        /// Resolves the caller's role from the DATABASE — never the JWT "Role" claim. A token
        /// issued before a demotion stays valid until it expires, and this decision determines
        /// what a customer is charged. Returns null for an anonymous caller.
        ///
        /// Only used on paths that don't already have the user entity loaded (calculate), and only
        /// when the request actually asks for custom pricing, so the normal booking flow pays
        /// nothing for this.
        /// </summary>
        private async Task<UserRole?> GetCallerRoleFromDbAsync()
        {
            var userId = GetUserId();
            if (userId == 0) return null;

            return await _context.Users
                .AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => (UserRole?)u.Role)
                .FirstOrDefaultAsync();
        }

        /// <summary>
        /// Records a refused custom-pricing attempt. Deliberately detailed enough to tell a probe
        /// from a bug: a customer's own id showing up repeatedly with round amounts is an attack,
        /// one admin hitting it once is a UI problem.
        /// </summary>
        private void LogCustomPricingRefused(
            string endpoint, decimal? attemptedAmount, int serviceTypeId, UserRole? role, int userId)
        {
            _logger.LogWarning(
                "Custom pricing REFUSED on {Endpoint}: attemptedCustomAmount={AttemptedAmount}, "
                + "serviceTypeId={ServiceTypeId}, role={Role}, userId={UserId}. "
                + "Booking priced normally through the standard catalogue path.",
                endpoint,
                attemptedAmount,
                serviceTypeId,
                role?.ToString() ?? "anonymous",
                userId == 0 ? "anonymous" : userId.ToString());
        }

        [HttpGet("service-types")]
        public async Task<ActionResult<List<ServiceTypeDto>>> GetServiceTypes()
        {
            // Public endpoint hides inactive services (the admin one deliberately shows them).
            // Thresholds and tiers are eager-loaded so the frontend calculator can mirror the
            // backend's threshold/tier math exactly.
            var serviceTypes = await _context.ServiceTypes
                .Include(st => st.Services.Where(s => s.IsActive))
                    .ThenInclude(s => s.Thresholds).ThenInclude(t => t.SourceService)
                .Include(st => st.Services.Where(s => s.IsActive))
                    .ThenInclude(s => s.RateTiers)
                .AsSplitQuery()
                .Where(st => st.IsActive)
                .OrderBy(st => st.DisplayOrder)
                .ToListAsync();

            // Custom service type visibility is enforced on the frontend (Admin/SuperAdmin only).
            // API returns all types so the client can show/hide based on role.

            // The whole active catalogue, resolved per service type below. Loaded once rather than
            // queried per type: a custom type needs all of it anyway (see
            // CatalogDtoMapper.ResolveSelectableExtraServices).
            var allExtraServices = await _context.ExtraServices
                .Where(es => es.IsActive)
                .OrderBy(es => es.DisplayOrder)
                .ToListAsync();

            var result = new List<ServiceTypeDto>();

            foreach (var st in serviceTypes)
            {
                // Same mapper the admin endpoints use, so MinimumPrice, the zero-quantity fields,
                // thresholds and rate tiers all reach the booking page — the frontend calculator
                // needs them to mirror the backend.
                var serviceTypeDto = CatalogDtoMapper.ToServiceTypeDto(st, st.Services);
                serviceTypeDto.ExtraServices = CatalogDtoMapper
                    .ResolveSelectableExtraServices(st, allExtraServices)
                    .Select(CatalogDtoMapper.ToExtraServiceDto)
                    .ToList();

                result.Add(serviceTypeDto);
            }

            return Ok(result);
        }

        [HttpGet("subscriptions")]
        public async Task<ActionResult<List<SubscriptionDto>>> GetSubscriptions()
        {
            var subscriptions = await _context.Subscriptions
                .Where(f => f.IsActive)
                .OrderBy(f => f.DisplayOrder)
                .Select(f => new SubscriptionDto
                {
                    Id = f.Id,
                    Name = f.Name,
                    Description = f.Description,
                    DiscountPercentage = f.DiscountPercentage,
                    SubscriptionDays = f.SubscriptionDays
                })
                .ToListAsync();

            return Ok(subscriptions);
        }

        [HttpGet("user-subscription")]
        [Authorize]
        public async Task<ActionResult> GetUserSubscription()
        {
            var userId = GetUserId();
            if (userId == 0)
                return Unauthorized();

            var user = await _context.Users
                .Include(u => u.Subscription)
                .FirstOrDefaultAsync(u => u.Id == userId);

            if (user == null) return NotFound();

            // Check and update subscription status
            await _subscriptionService.CheckAndUpdateSubscriptionStatus(userId);

            // The plan the customer picked on their profile. A PREFERENCE that only pre-selects
            // the tier on the booking page — it grants no discount here or anywhere else. See
            // Helpers/PlanSelectionPolicy.
            var preferredSubscriptionId = user.PreferredSubscriptionId;

            if (user.SubscriptionId == null)
            {
                return Ok(new { hasSubscription = false, preferredSubscriptionId });
            }

            return Ok(new
            {
                hasSubscription = true,
                subscriptionId = user.SubscriptionId,
                subscriptionName = user.Subscription.Name,
                discountPercentage = user.Subscription.DiscountPercentage,
                expiryDate = user.SubscriptionExpiryDate,
                preferredSubscriptionId,
            });
        }

        [HttpPost("apply-gift-card")]
        [Authorize]
        public async Task<ActionResult> ApplyGiftCard(ApplyGiftCardToOrderDto dto)
        {
            try
            {
                var userId = GetUserId();
                if (userId == 0)
                    return Unauthorized();

                // Pass userId to the service
                var amountApplied = await _giftCardService.ApplyGiftCardToOrder(dto.Code, dto.OrderAmount, dto.OrderId, userId);
                return Ok(new { amountApplied, message = "Gift card applied successfully" });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("validate-promo")]
        public async Task<ActionResult<PromoCodeValidationDto>> ValidatePromoCode(ValidatePromoCodeDto dto)
        {
            try
            {
                // Check if it's a gift card format (XXXX-XXXX-XXXX)
                if (System.Text.RegularExpressions.Regex.IsMatch(dto.Code, @"^[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}$"))
                {
                    // Handle as gift card
                    var giftCardValidation = await _giftCardService.ValidateGiftCard(dto.Code);
                    return Ok(new PromoCodeValidationDto
                    {
                        IsValid = giftCardValidation.IsValid,
                        DiscountValue = giftCardValidation.AvailableBalance,
                        IsPercentage = false,
                        IsGiftCard = true,
                        AvailableBalance = giftCardValidation.AvailableBalance,
                        Message = giftCardValidation.Message
                    });
                }
                else
                {
                    // Handle as regular promo code - YOUR EXISTING LOGIC
                    var promoCode = await _context.PromoCodes
                        .FirstOrDefaultAsync(p => p.Code.ToLower() == dto.Code.ToLower() && p.IsActive);

                    if (promoCode == null)
                    {
                        return Ok(new PromoCodeValidationDto
                        {
                            IsValid = false,
                            Message = "Invalid promo code"
                        });
                    }

                    // Check validity dates - YOUR EXISTING LOGIC
                    if (promoCode.ValidFrom.HasValue && promoCode.ValidFrom.Value > DateTime.UtcNow)
                    {
                        return Ok(new PromoCodeValidationDto
                        {
                            IsValid = false,
                            Message = "Promo code is not yet valid"
                        });
                    }

                    if (promoCode.ValidTo.HasValue && promoCode.ValidTo.Value < DateTime.UtcNow)
                    {
                        return Ok(new PromoCodeValidationDto
                        {
                            IsValid = false,
                            Message = "Promo code has expired"
                        });
                    }

                    // Check usage limits - YOUR EXISTING LOGIC
                    if (promoCode.MaxUsageCount.HasValue && promoCode.CurrentUsageCount >= promoCode.MaxUsageCount.Value)
                    {
                        return Ok(new PromoCodeValidationDto
                        {
                            IsValid = false,
                            Message = "Promo code usage limit reached"
                        });
                    }

                    // Minimum order amount check. Only enforced when the caller passed a SubTotal
                    // (booking flow). Gift-card balance lookups from order-edit pass no SubTotal
                    // and must not be blocked by this check.
                    if (promoCode.MinimumOrderAmount.HasValue && dto.SubTotal.HasValue &&
                        dto.SubTotal.Value < promoCode.MinimumOrderAmount.Value)
                    {
                        return Ok(new PromoCodeValidationDto
                        {
                            IsValid = false,
                            Message = $"Minimum order amount of ${promoCode.MinimumOrderAmount.Value:0.##} required to use this promo code"
                        });
                    }

                    return Ok(new PromoCodeValidationDto
                    {
                        IsValid = true,
                        DiscountValue = promoCode.DiscountValue,
                        IsPercentage = promoCode.IsPercentage,
                        IsGiftCard = false // This is a regular promo code
                    });
                }
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = "Failed to validate code: " + ex.Message });
            }
        }  

        [HttpPost("calculate")]
        public async Task<ActionResult<BookingCalculationDto>> CalculateBooking(CreateBookingDto dto)
        {
            // Real server-side quote through the shared calculator (was a hardcoded stub).
            var serviceType = await _context.ServiceTypes
                .Include(st => st.Services)
                .FirstOrDefaultAsync(st => st.Id == dto.ServiceTypeId);

            if (serviceType == null)
                return BadRequest(new { message = "Invalid service type" });

            // Same gate as create/create-for-user, so a quote can never promise a price the
            // charging path would refuse to honour. The role lookup is skipped entirely unless
            // custom pricing was actually requested (this endpoint is anonymous and hot).
            UserRole? callerRole = dto.IsCustomPricing ? await GetCallerRoleFromDbAsync() : null;
            var allowCustomPricing = MayUseCustomPricing(callerRole);
            if (OrderPricingInputBuilder.NormalizeCustomPricing(
                    serviceType, dto, allowCustomPricing, out var refusedCustomAmount))
            {
                LogCustomPricingRefused("POST api/booking/calculate", refusedCustomAmount,
                    dto.ServiceTypeId, callerRole, GetUserId());
            }

            var quoteInput = await BuildQuoteInputAsync(serviceType, dto, allowCustomPricing);
            var quote = OrderPricingCalculator.CalculateQuote(quoteInput);

            var totals = OrderPricingCalculator.CalculateTotals(new OrderPricingCalculator.TotalsInput
            {
                SubTotal = quote.SubTotal,
                TaxOverride = quote.TaxOverride,
                DiscountAmount = dto.DiscountAmount,
                SubscriptionDiscountAmount = dto.SubscriptionDiscountAmount,
                LoyaltyDiscountAmount = dto.LoyaltyDiscountAmount,
                Tips = dto.Tips,
                GiftCardAmountUsed = dto.GiftCardAmountToUse
            });

            var calculation = new BookingCalculationDto
            {
                SubTotal = quote.SubTotal,
                Tax = totals.Tax,
                DiscountAmount = dto.DiscountAmount + dto.SubscriptionDiscountAmount + dto.LoyaltyDiscountAmount,
                Tips = dto.Tips,
                Total = totals.Total,
                TotalDuration = quote.DisplayDuration
            };

            return Ok(calculation);
        }

        [HttpPost("create")]
        [Authorize]
        public async Task<ActionResult<BookingResponseDto>> CreateBooking(CreateBookingDto dto)
        {
            try
            {
                var userId = GetUserId();
                if (userId == 0)
                    return Unauthorized();

                // Email stays mandatory on the public flow (only admin create-for-user may omit it).
                if (string.IsNullOrWhiteSpace(dto.ContactEmail))
                    return BadRequest(new { message = "Contact email is required." });

                // Find the user
                var user = await _context.Users.FindAsync(userId);
                if (user == null)
                    return Unauthorized();

                // Get service type to check base price
                var serviceType = await _context.ServiceTypes
                    .Include(st => st.Services)
                    .FirstOrDefaultAsync(st => st.Id == dto.ServiceTypeId);

                if (serviceType == null)
                    return BadRequest(new { message = "Invalid service type" });

                // Custom-pricing gate. user.Role comes from the row loaded above, so this costs no
                // extra query and reflects the CURRENT role rather than whatever the token claims.
                // Normalising the DTO here (not just filtering at pricing time) means the promo
                // check below, the order persisted by CreateOrderAsync and the PaymentIntent raised
                // from its Total all derive from one decision.
                var allowCustomPricing = MayUseCustomPricing(user.Role);
                if (OrderPricingInputBuilder.NormalizeCustomPricing(
                        serviceType, dto, allowCustomPricing, out var refusedCustomAmount))
                {
                    LogCustomPricingRefused("POST api/booking/create", refusedCustomAmount,
                        dto.ServiceTypeId, user.Role, userId);
                }

                // Resolve gift-card-vs-promo via the shared rule, then validate the promo
                // minimum against the RECOMPUTED subtotal (never the client-sent dto.SubTotal).
                var (promoCode, _, _) = _bookingCreationService.ResolveGiftCardAndPromo(dto);
                var createQuoteInput = await BuildQuoteInputAsync(serviceType, dto, allowCustomPricing);
                var createQuote = OrderPricingCalculator.CalculateQuote(createQuoteInput);
                var promoMinError = await ValidatePromoMinimumOrderAmountAsync(promoCode, createQuote.SubTotal);
                if (promoMinError != null)
                    return BadRequest(new { message = promoMinError });

                var subscription = await _context.Subscriptions.FindAsync(dto.SubscriptionId);

                // Create + persist the order through the shared creation service: pricing via
                // the shared calculator, loyalty stacking for the booking user, special-offer
                // consumption and gift-card application — all in one transaction.
                var order = await _bookingCreationService.CreateOrderAsync(dto, userId, allowCustomPricing);

                // Notify admins about new order
                await NotifyAdminsNewOrder(order.Id);

                // Handle subscription activation/renewal
                if (subscription != null && subscription.SubscriptionDays > 0)
                {
                    var userForSubscription = await _context.Users
                        .Include(u => u.Subscription)
                        .FirstOrDefaultAsync(u => u.Id == userId);

                    bool hasActiveSubscription = await _subscriptionService.CheckAndUpdateSubscriptionStatus(userId);

                    if (!hasActiveSubscription)
                    {
                        // FIXED: Changed variable name from 'subscription' to 'userSubscription'
                        var userSubscription = await _context.Subscriptions
                            .FirstOrDefaultAsync(s => s.SubscriptionDays == subscription.SubscriptionDays);

                        if (userSubscription != null)
                        {
                            await _subscriptionService.ActivateSubscription(userId, userSubscription.Id, dto.ServiceDate);
                        }
                    }
                    else if (userForSubscription.SubscriptionId.HasValue)
                    {
                        // Renew existing subscription
                        await _subscriptionService.RenewSubscription(userId, dto.ServiceDate);
                    }
                }

                // Store booking data including photos for later use
                var sessionId = $"booking_{order.Id}_{userId}";
                _bookingDataService.StoreBookingData(sessionId, dto);

                // Create Stripe payment intent
                var metadata = new Dictionary<string, string>
                {
                    { "orderId", order.Id.ToString() },
                    { "userId", userId.ToString() },
                    { "type", "booking" }
                };

                var paymentIntent = await _stripeService.CreatePaymentIntentAsync(order.Total, metadata,
                    receiptEmail: OrderReceiptEmail(order));

                // Update order with payment intent ID
                order.PaymentIntentId = paymentIntent.Id;
                await _context.SaveChangesAsync();

                return Ok(new BookingResponseDto
                {
                    OrderId = order.Id,
                    Status = order.Status,
                    Total = order.Total,
                    PaymentIntentId = paymentIntent.Id,
                    PaymentClientSecret = paymentIntent.ClientSecret
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = "Failed to create booking: " + ex.Message });
            }
        }

        [HttpPost("create-for-user")]
        [Authorize(Roles = "Admin,SuperAdmin,Moderator")]
        public async Task<ActionResult<BookingResponseDto>> CreateBookingForUser([FromBody] CreateBookingForUserDto dto)
        {
            try
            {
                var adminUserId = GetUserId();
                if (adminUserId == 0)
                    return Unauthorized();

                // Verify admin/moderator role
                var adminUser = await _context.Users.FindAsync(adminUserId);
                if (adminUser == null || (adminUser.Role != UserRole.Admin && adminUser.Role != UserRole.SuperAdmin && adminUser.Role != UserRole.Moderator))
                    return Unauthorized(new { message = "Only admins and moderators can create bookings for users" });

                // Verify target user exists
                var targetUser = await _context.Users.FindAsync(dto.TargetUserId);
                if (targetUser == null)
                    return NotFound(new { message = "Target user not found" });

                // Contact email may be omitted only for no-email (cash) customers.
                if (string.IsNullOrWhiteSpace(dto.BookingData.ContactEmail) && !targetUser.IsNoEmailUser)
                    return BadRequest(new { message = "Contact email is required." });

                // Parse manual payment method up front so order construction can choose the
                // initial Status (Pending for Stripe / Normal, Active for manual). Anything
                // unrecognised falls back to Normal — defensive default keeps the original flow.
                var paymentMethod = PaymentMethod.Normal;
                if (!string.IsNullOrWhiteSpace(dto.PaymentMethod) &&
                    !Enum.TryParse<PaymentMethod>(dto.PaymentMethod, ignoreCase: true, out paymentMethod))
                {
                    paymentMethod = PaymentMethod.Normal;
                }

                // Active only for a method whose money has ALREADY arrived. Stripe starts Pending
                // because nothing is paid yet, and so does Invoice (2026-09) — an invoice-billed
                // order stays Pending until a CommercialInvoice covering it is settled in full,
                // which is what activates it. Starting it Active would report an unpaid commercial
                // job as live work with money behind it.
                var initialStatus = PaymentMethodRules.IsSettledOnRecord(paymentMethod)
                    ? "Active"
                    : "Pending";

                // An Invoice order has to say who it is billed to, or it can never be picked up by
                // an invoice. Validated here rather than defaulted: guessing the client from the
                // customer's account would be a silent, wrong answer for any company with more
                // than one entity.
                if (paymentMethod == PaymentMethod.Invoice)
                {
                    if (dto.ContractClientId is not > 0)
                        return BadRequest(new { message = "Choose the commercial client this order is billed to." });

                    var clientExists = await _context.ContractClients
                        .AnyAsync(c => c.Id == dto.ContractClientId!.Value);
                    if (!clientExists)
                        return BadRequest(new { message = "That commercial client no longer exists." });
                }

                // The admin recreate flow may override the status — a job re-entered after the
                // fact is usually already Done. Unlike PaymentMethod above, an unrecognised value
                // is REJECTED rather than coerced: Order.Status is a free string, so a typo would
                // otherwise be persisted and quietly fall out of every status filter in the panel.
                if (!string.IsNullOrWhiteSpace(dto.InitialStatus))
                {
                    var requested = new[] { OrderStatuses.Pending, OrderStatuses.Active, OrderStatuses.Done }
                        .FirstOrDefault(s => OrderStatuses.Is(dto.InitialStatus, s));
                    if (requested == null)
                        return BadRequest(new { message = $"'{dto.InitialStatus}' is not a status a new order can start in. Use Pending, Active or Done." });
                    initialStatus = requested;
                }

                // Back-dating is the reason this flow exists (re-entering cash jobs that were
                // missed at the time), and an unpaid order whose service date has passed is
                // auto-cancelled on sight by OrderService. Exempt it, or the order the admin just
                // re-entered disappears the next time anyone opens that customer's list.
                //
                // Compared against NY's today, not UTC's: ServiceDate is NY wall-clock, and after
                // 8pm New York the UTC date is already tomorrow — which would have flagged a job
                // booked for this evening as back-dated.
                var isBackDated = dto.BookingData.ServiceDate.Date < NyTimeHelper.NowNy.Date;

                // Null = this endpoint's historic behaviour (the customer's loyalty and plan
                // discounts apply), which is what the booking page's Admin Mode relies on. Only an
                // explicit false suppresses them.
                var suppressAutomaticDiscounts = dto.ApplyCurrentDiscounts == false;

                // Get service type
                var serviceType = await _context.ServiceTypes
                    .Include(st => st.Services)
                    .FirstOrDefaultAsync(st => st.Id == dto.BookingData.ServiceTypeId);

                if (serviceType == null)
                    return BadRequest(new { message = "Invalid service type" });

                // Custom-pricing gate. THIS is the endpoint the admin panel's Pre-arranged flow
                // uses (booking.component -> createBookingForUser), so an Admin/SuperAdmin booking
                // the custom service type passes both halves and is completely unaffected.
                // adminUser was already loaded and role-checked above — no extra query.
                //
                // Moderator is excluded: it is View-only in PermissionService, and the booking UI
                // only offers the custom service type to Admin/SuperAdmin, so this removes nothing
                // a Moderator can do today while closing the API path.
                var allowCustomPricing = MayUseCustomPricing(adminUser.Role);
                if (OrderPricingInputBuilder.NormalizeCustomPricing(
                        serviceType, dto.BookingData, allowCustomPricing, out var refusedCustomAmount))
                {
                    LogCustomPricingRefused("POST api/booking/create-for-user", refusedCustomAmount,
                        dto.BookingData.ServiceTypeId, adminUser.Role, adminUserId);
                }

                // Resolve gift-card-vs-promo via the shared rule, then validate the promo
                // minimum against the RECOMPUTED subtotal (never the client-sent SubTotal).
                var (promoCode, _, _) = _bookingCreationService.ResolveGiftCardAndPromo(dto.BookingData);
                var forUserQuoteInput = await BuildQuoteInputAsync(serviceType, dto.BookingData, allowCustomPricing);
                var forUserQuote = OrderPricingCalculator.CalculateQuote(forUserQuoteInput);
                var promoMinError = await ValidatePromoMinimumOrderAmountAsync(promoCode, forUserQuote.SubTotal);
                if (promoMinError != null)
                    return BadRequest(new { message = promoMinError });

                // Create + persist the order through the shared creation service. Loyalty
                // stacking resolves against the TARGET customer (never the logged-in admin);
                // manual payment methods stamp the tracking fields and start the order Active.
                var order = await _bookingCreationService.CreateOrderAsync(dto.BookingData, dto.TargetUserId, allowCustomPricing, new BookingCreationOptions
                {
                    InitialStatus = initialStatus,
                    PaymentMethod = paymentMethod,
                    PaymentReference = dto.PaymentReference,
                    PaymentNotes = dto.PaymentNotes,
                    ManualPaymentRecordedByUserId = adminUserId,
                    BookedByAdminUserId = adminUserId,
                    IsAutoCancelExempt = isBackDated,
                    SuppressAutomaticDiscounts = suppressAutomaticDiscounts,
                    ContractClientId = dto.ContractClientId
                });

                // A back-dated re-entry usually describes a job that already happened, so the
                // admin can start it at Done. Run the same completion hook the Orders panel runs
                // on a Done transition — otherwise an order created Done never earns the customer
                // their bubble points, and nothing later would award them (the hook only fires on
                // a TRANSITION into Done, which this order will never make).
                if (OrderStatuses.Is(order.Status, OrderStatuses.Done))
                {
                    try
                    {
                        // Resolved from the request scope like every other bubble-points call in
                        // this controller, rather than injected — same pattern, no ctor change.
                        var bubbleSvc = HttpContext.RequestServices.GetService<IBubblePointsService>();
                        if (bubbleSvc != null)
                            await bubbleSvc.ProcessOrderCompletion(order.Id);
                    }
                    catch (Exception rewardsEx)
                    {
                        _logger.LogError(rewardsEx,
                            "[BubbleRewards] ProcessOrderCompletion failed for order {OrderId} created directly as Done", order.Id);
                    }
                }

                // Auto-assign the creating admin to the order (goes through AdminBonusService so
                // the assignment lands in OrderAdminAssignmentHistory with the current bonus rate).
                // Only active Admin-role users are assignable — a SuperAdmin/Moderator creator
                // leaves the order unassigned, matching the panel's dropdown which lists Admins only.
                try
                {
                    await _adminBonusService.AssignAdminAsync(order.Id, adminUserId, adminUserId);
                }
                catch (InvalidOperationException)
                {
                    // Creator isn't an assignable admin — order stays unassigned.
                }

                // Notify admins about new order
                await NotifyAdminsNewOrder(order.Id);

                // Handle subscription activation/renewal (but don't activate until paid)
                // We'll handle this in confirm-payment

                // Store booking data including photos for later use when payment is confirmed
                var sessionId = $"booking_{order.Id}_{dto.TargetUserId}";
                _bookingDataService.StoreBookingData(sessionId, dto.BookingData);

                // Customer notifications are OPT-OUT for the historic flow and OPT-IN for the
                // recreate flow, expressed as one rule: null means "as this endpoint always
                // behaved" (send), and only an explicit false suppresses. Re-entering a job that
                // already happened must not text the customer a confirmation for it, so the
                // recreate modal always sends explicit values.
                //
                // Only the CUSTOMER-facing sends are gated. NotifyAdminsNewOrder above is
                // internal and always runs: the order genuinely exists and staff must see it.
                var notifyCustomerByEmail = dto.SendCustomerEmail != false;
                var notifyCustomerBySms = dto.SendCustomerSms != false;

                // A commercial cleaning billed on an invoice is not in the residential flow at all:
                // the client is billed, chased and receipted from the invoice, so the residential
                // "your booking is confirmed" mail and its SMS must never go out for it — no matter
                // what the admin left ticked in the modal. Suppressed BEFORE the send rather than
                // retracted after; the rule itself lives in one place because every other creation
                // path has to answer it the same way.
                var residentialCommunicationAllowed =
                    ResidentialBookingCommunicationPolicy.ShouldSendResidentialBookingCommunication(
                        paymentMethod, dto.ContractClientId);
                if (!residentialCommunicationAllowed)
                {
                    _logger.LogInformation(
                        "Order {OrderId} is billed through a commercial invoice (client {ContractClientId}); "
                        + "residential booking confirmation email/SMS suppressed.",
                        order.Id, dto.ContractClientId);
                    notifyCustomerByEmail = false;
                    notifyCustomerBySms = false;
                }

                if (dto.RecreatedFromOrderId.HasValue)
                {
                    _logger.LogInformation(
                        "Order {OrderId} recreated from order {SourceOrderId} by admin {AdminId}: status {Status}, "
                        + "paymentMethod {PaymentMethod}, backDated {BackDated}, currentDiscountsApplied {DiscountsApplied}, "
                        + "notifyEmail {NotifyEmail}, notifySms {NotifySms}",
                        order.Id, dto.RecreatedFromOrderId.Value, adminUserId, order.Status,
                        paymentMethod, isBackDated, !suppressAutomaticDiscounts,
                        notifyCustomerByEmail, notifyCustomerBySms);
                }

                // Manual payment path: skip the Pay Now reminder entirely and send a real
                // booking confirmation (no payment link). Reuses the existing customer template
                // — same shape as a successful Stripe payment confirmation, just without the
                // "you need to pay" framing. The Stripe / Normal path below is unchanged.
                if (paymentMethod != PaymentMethod.Normal)
                {
                    // Load extras for the supply checklist that the confirmation email/SMS use.
                    await _context.Entry(order).Collection(o => o.OrderExtraServices).Query().Include(oes => oes.ExtraService).LoadAsync();

                    var manualContactEmail = order.ContactEmail;
                    var manualContactPhone = !string.IsNullOrWhiteSpace(order.ContactPhone) ? order.ContactPhone : targetUser?.Phone;
                    var manualCustomerName = CapitalizeName(order.ContactFirstName);
                    var manualAddressDisplay = $"{order.ServiceAddress}{(!string.IsNullOrEmpty(order.AptSuite) ? $", {order.AptSuite}" : "")}";
                    var manualServiceTimeStr = order.ServiceTime.ToString();

                    var manualExtraNames = (order.OrderExtraServices ?? new List<OrderExtraService>())
                        .Select(x => x.ExtraService?.Name ?? "")
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .ToList();
                    var manualIsCustomServiceType = order.ServiceType?.IsCustom ?? false;
                    var manualSupplyChecklist = CustomerSupplyChecklist.Resolve(manualExtraNames, manualIsCustomServiceType);

                    // Read off the tracked order HERE — detached work never touches the entity.
                    var manualOrderId = order.Id;
                    var manualServiceDate = order.ServiceDate;
                    var manualServiceTypeName = order.GetDisplayServiceTypeName();
                    var manualFloorTypes = order.FloorTypes;
                    var manualFloorTypeOther = order.FloorTypeOther;
                    var manualPropertyType = order.PropertyType;
                    var manualLevelsQuantity = order.LevelsQuantity;

                    // Fire-and-forget email (skip Apple hidden mail). Same isAppleHiddenMail
                    // check the Stripe path uses below — keep behavior aligned.
                    if (notifyCustomerByEmail)
                    {
                        BackgroundWork.Run(_scopeFactory, _logger, $"manual-payment booking confirmation email for order {manualOrderId}", async services =>
                        {
                            var isAppleHiddenMail = !string.IsNullOrEmpty(manualContactEmail) &&
                                manualContactEmail.EndsWith("@privaterelay.appleid.com", StringComparison.OrdinalIgnoreCase);
                            if (!isAppleHiddenMail && !string.IsNullOrWhiteSpace(manualContactEmail))
                            {
                                await services.GetRequiredService<IEmailService>().SendCustomerBookingConfirmationAsync(
                                    manualContactEmail, manualCustomerName, manualServiceDate, manualServiceTimeStr,
                                    manualServiceTypeName, manualAddressDisplay, manualOrderId,
                                    manualSupplyChecklist,
                                    manualFloorTypes, manualFloorTypeOther,
                                    // Manual payment path: customer pays cleaners on arrival, so drop
                                    // the "payment processed successfully" phrasing from the greeting.
                                    paymentAlreadyProcessed: false,
                                    propertyType: manualPropertyType, levelsQuantity: manualLevelsQuantity);
                                _logger.LogInformation($"Manual-payment booking confirmation email sent to {manualContactEmail} for order {manualOrderId}");
                            }
                        });
                    }
                    else
                    {
                        _logger.LogInformation(residentialCommunicationAllowed
                            ? $"Booking confirmation email suppressed by the creating admin for order {order.Id}"
                            : $"Booking confirmation email suppressed for order {order.Id} - commercial invoice-backed order");
                    }

                    if (notifyCustomerBySms && !string.IsNullOrWhiteSpace(manualContactPhone))
                    {
                        BackgroundWork.Run(_scopeFactory, _logger, $"manual-payment booking confirmation SMS for order {manualOrderId}", async services =>
                        {
                            try
                            {
                                await services.GetRequiredService<ISmsService>().SendBookingConfirmationSmsAsync(
                                    manualContactPhone, manualCustomerName, manualServiceDate, manualServiceTimeStr,
                                    manualSupplyChecklist);
                                _logger.LogInformation($"Manual-payment booking confirmation SMS sent to {manualContactPhone} for order {manualOrderId}");
                            }
                            catch (InvalidPhoneNumberException)
                            {
                                // Kept local: a number we cannot text is a data problem, not a
                                // failure worth an error-level line on every such order.
                                _logger.LogWarning($"Manual-payment SMS skipped for order {manualOrderId}: invalid phone");
                            }
                        });
                    }

                    // Manual-payment orders never reach confirm-payment, so persist any
                    // booking-uploaded photos to the cleaning photo library here instead.
                    await PersistBookingPhotosAsync(dto.TargetUserId, order.Id, dto.BookingData.UploadedPhotos);

                    _logger.LogInformation($"Admin booking created with manual payment {paymentMethod}: order {order.Id}, status {order.Status}, isPaid {order.IsPaid}");

                    // An admin created an order on somebody's behalf, already marked paid outside
                    // Stripe. Nothing else records that: BookingCreationService does not audit
                    // (it also serves the customer's own booking flow), and there is no payment
                    // intent or webhook behind a cash order.
                    await _auditService.LogCreateAsync(order);

                    return Ok(new BookingResponseDto
                    {
                        OrderId = order.Id,
                        Status = order.Status,
                        Total = order.Total,
                        PaymentIntentId = null,
                        PaymentClientSecret = null
                    });
                }

                // Send payment reminder notifications (SMS and Email)
                try
                {
                    // Reload target user to get latest data
                    targetUser = await _context.Users.FindAsync(dto.TargetUserId);
                    if (targetUser != null)
                    {
                        // Backfill missing profile info (Phone/FirstName/LastName) from the booking
                        // contact fields before notifying. Apple/Google sign-ins land with these
                        // empty, so without this the SMS check below skips and the user gets nothing.
                        var userInfoUpdated = false;
                        if (string.IsNullOrWhiteSpace(targetUser.Phone) && !string.IsNullOrWhiteSpace(dto.BookingData.ContactPhone))
                        {
                            targetUser.Phone = dto.BookingData.ContactPhone;
                            userInfoUpdated = true;
                        }
                        if (string.IsNullOrWhiteSpace(targetUser.FirstName) && !string.IsNullOrWhiteSpace(dto.BookingData.ContactFirstName))
                        {
                            targetUser.FirstName = dto.BookingData.ContactFirstName;
                            userInfoUpdated = true;
                        }
                        if (string.IsNullOrWhiteSpace(targetUser.LastName) && !string.IsNullOrWhiteSpace(dto.BookingData.ContactLastName))
                        {
                            targetUser.LastName = dto.BookingData.ContactLastName;
                            userInfoUpdated = true;
                        }
                        if (userInfoUpdated)
                        {
                            targetUser.UpdatedAt = DateTime.UtcNow;
                            await _context.SaveChangesAsync();
                        }

                        var frontendUrl = _configuration["Frontend:Url"] ?? "https://dreamcleaningnyc.com";
                        // Tokenized link: opens the payment page without login while unpaid.
                        var orderLink = await PaymentLinkHelper.BuildPaymentLinkAsync(_context, order, frontendUrl);
                        
                        // Capitalize first letter of names
                        var capitalizedFirstName = CapitalizeName(targetUser.FirstName);
                        var capitalizedLastName = CapitalizeName(targetUser.LastName);
                        var customerName = $"{capitalizedFirstName} {capitalizedLastName}".Trim();
                        if (string.IsNullOrWhiteSpace(customerName))
                            customerName = capitalizedFirstName ?? "Valued Customer";

                        // Check if email is not Apple hidden mail
                        var isAppleHiddenMail = !string.IsNullOrEmpty(targetUser.Email) &&
                                               targetUser.Email.EndsWith("@privaterelay.appleid.com", StringComparison.OrdinalIgnoreCase);

                        // Send email if not Apple hidden mail
                        if (!notifyCustomerByEmail)
                        {
                            _logger.LogInformation($"Payment reminder email suppressed by the creating admin for Order #{order.Id}");
                        }
                        else if (!isAppleHiddenMail && !string.IsNullOrWhiteSpace(targetUser.Email))
                        {
                            try
                            {
                                await _emailService.SendPaymentReminderEmailAsync(
                                    targetUser.Email,
                                    customerName,
                                    order.Total,
                                    order.Id,
                                    orderLink
                                );
                                _logger.LogInformation($"Payment reminder email sent to {targetUser.Email} for Order #{order.Id}");
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, $"Failed to send payment reminder email to {targetUser.Email} for Order #{order.Id}");
                                // Don't throw - continue with SMS if email fails
                            }
                        }
                        else if (isAppleHiddenMail)
                        {
                            _logger.LogInformation($"Skipping email payment reminder for Order #{order.Id} - user has Apple hidden mail");
                        }

                        // Send SMS if phone number exists
                        if (!notifyCustomerBySms)
                        {
                            _logger.LogInformation($"Payment reminder SMS suppressed by the creating admin for Order #{order.Id}");
                        }
                        else if (!string.IsNullOrWhiteSpace(targetUser.Phone))
                        {
                            try
                            {
                                await _smsService.SendPaymentReminderSmsAsync(
                                    targetUser.Phone,
                                    customerName,
                                    order.Total,
                                    order.Id,
                                    orderLink
                                );
                                _logger.LogInformation($"Payment reminder SMS sent to {targetUser.Phone} for Order #{order.Id}");
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, $"Failed to send payment reminder SMS to {targetUser.Phone} for Order #{order.Id}");
                                // Don't throw - SMS failures shouldn't break booking creation
                            }
                        }
                        else
                        {
                            _logger.LogInformation($"Skipping SMS payment reminder for Order #{order.Id} - user has no phone number");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error sending payment reminders for Order #{order.Id}");
                    // Don't throw - payment reminder failures shouldn't break booking creation
                }

                _logger.LogInformation($"Admin booking created: order {order.Id}, status {order.Status}, isPaid {order.IsPaid}");

                // The card branch of the same admin action. Audited here rather than inside
                // BookingCreationService because that service also serves the CUSTOMER's own
                // booking flow, and a customer booking their own cleaning is not an admin action
                // for the Audits tab to carry.
                await _auditService.LogCreateAsync(order);

                return Ok(new BookingResponseDto
                {
                    OrderId = order.Id,
                    Status = order.Status,
                    Total = order.Total,
                    PaymentIntentId = null, // No payment intent yet
                    PaymentClientSecret = null
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = "Failed to create booking for user: " + ex.Message });
            }
        }

        [HttpPost("prepare-payment")]
        [AllowAnonymous]
        public async Task<ActionResult<BookingResponseDto>> PreparePayment([FromBody] CreateBookingDto dto)
        {
            try
            {
                var userId = GetUserId();
                // Captured BEFORE the guest auto-create below overwrites userId with a freshly
                // minted Customer account — otherwise a genuinely anonymous caller would be logged
                // as that new user rather than as "anonymous".
                var authenticatedCallerId = userId;
                AuthResponseDto? guestAuth = null;

                if (userId == 0)
                {
                    // Guest flow: auto-create or find user from booking contact info
                    if (string.IsNullOrWhiteSpace(dto.ContactEmail))
                        return BadRequest(new { message = "Contact email is required." });

                    // Pass referral code so it's bound immediately on new account creation
                    guestAuth = await _authService.CreateOrGetGuestUserAsync(
                        dto.ContactFirstName,
                        dto.ContactLastName,
                        dto.ContactEmail,
                        dto.ContactPhone,
                        dto.ReferralCode);

                    userId = guestAuth.User.Id;
                }

                // For logged-in users: process referral (idempotent — skips if already referred)
                if (guestAuth == null && !string.IsNullOrWhiteSpace(dto.ReferralCode))
                {
                    try
                    {
                        await _referralService.ProcessReferralRegistration(userId, dto.ReferralCode);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to process referral for logged-in user {UserId}", userId);
                    }
                }

                // Find the user
                var user = await _context.Users.FindAsync(userId);
                if (user == null)
                    return Unauthorized();

                // Get service type to check base price
                var serviceType = await _context.ServiceTypes
                    .Include(st => st.Services)
                    .FirstOrDefaultAsync(st => st.Id == dto.ServiceTypeId);

                if (serviceType == null)
                    return BadRequest(new { message = "Invalid service type" });

                // Custom pricing is HARD-DISABLED on this endpoint — not merely role-gated.
                //
                // This action is [AllowAnonymous] and, by the time the price is computed, an
                // anonymous caller has already been turned into a freshly created Customer above,
                // so there is no admin identity here to trust. It is also the front half of a
                // two-phase flow: the DTO is parked in BookingDataService and confirm-payment
                // (also anonymous) later builds the real order from it. Deciding "allowed" here
                // would have to be re-decided there, and any disagreement charges the customer one
                // amount while recording another.
                //
                // An authenticated Admin/SuperAdmin gets told rather than silently mis-priced:
                // they reached here by choosing Pre-arranged WITHOUT switching on admin mode, and
                // silently re-pricing would floor their bespoke amount to MinimumPrice. Customers
                // and anonymous callers get the silent-ignore path, so a probe learns nothing.
                if (dto.IsCustomPricing)
                {
                    var callerRole = authenticatedCallerId == 0 ? (UserRole?)null : user.Role;
                    if (MayUseCustomPricing(callerRole))
                    {
                        return BadRequest(new
                        {
                            message = "Pre-arranged bookings must be created from the admin panel."
                        });
                    }

                    OrderPricingInputBuilder.NormalizeCustomPricing(
                        serviceType, dto, allowCustomPricing: false, out var refusedCustomAmount);
                    LogCustomPricingRefused("POST api/booking/prepare-payment", refusedCustomAmount,
                        dto.ServiceTypeId, callerRole, authenticatedCallerId);
                }

                // Resolve gift-card-vs-promo via the shared rule (single definition in BookingCreationService).
                var (promoCode, giftCardCode, giftCardAmountUsed) = _bookingCreationService.ResolveGiftCardAndPromo(dto);

                // Verify the gift card and clamp the claimed draw to its REAL balance before the
                // charge amount is computed — the reconciliation at order creation happens after
                // the Stripe charge, which is too late to protect the captured amount.
                if (!string.IsNullOrEmpty(giftCardCode) && giftCardAmountUsed > 0)
                {
                    var giftCardValidation = await _giftCardService.ValidateGiftCard(giftCardCode);
                    if (giftCardValidation == null || !giftCardValidation.IsValid)
                        return BadRequest(new { message = giftCardValidation?.Message ?? "Invalid gift card." });
                    giftCardAmountUsed = Math.Min(giftCardAmountUsed, giftCardValidation.AvailableBalance);
                    // Keep the session-stored booking data consistent with the verified draw so
                    // confirm-payment's order creation starts from the same number.
                    dto.GiftCardAmountToUse = giftCardAmountUsed;
                }

                // Price through the shared calculator (single source of truth — see OrderPricingCalculator).
                // allowCustomPricing: false always — see the hard-disable note above; confirm-payment
                // passes the same false when it builds the order from this DTO, so the amount charged
                // here and the amount persisted there come from one decision.
                var quoteInput = await BuildQuoteInputAsync(serviceType, dto, allowCustomPricing: false);
                var quote = OrderPricingCalculator.CalculateQuote(quoteInput);

                // Promo minimum is validated against the RECOMPUTED subtotal — never the
                // client-sent dto.SubTotal, which could be inflated to slip past the gate.
                var promoMinError = await ValidatePromoMinimumOrderAmountAsync(promoCode, quote.SubTotal);
                if (promoMinError != null)
                    return BadRequest(new { message = promoMinError });

                if (dto.TotalDuration is decimal estimatedDuration &&
                    Math.Abs(estimatedDuration - quote.TotalDuration) > 5)
                {
                    _logger.LogWarning(
                        "Booking duration estimate differs: received {EstimatedMinutes:0.##} min, calculated {CalculatedMinutes:0.##} min for service type {ServiceTypeId} during payment preparation.",
                        estimatedDuration, quote.TotalDuration, dto.ServiceTypeId);
                }

                // Server-derived promo/first-time/subscription discounts — this endpoint produces
                // the payment-intent amount, so the client's dollar figures must never reach it.
                // Overwrites the dto slots so the stacking below and the session-stored booking
                // data both carry the trusted values.
                decimal calculatedSubTotal = quote.SubTotal;
                var (resolvedDiscountAmount, resolvedSubscriptionDiscountAmount) =
                    await _bookingCreationService.ResolveDiscountsAsync(dto, userId, calculatedSubTotal);
                dto.DiscountAmount = resolvedDiscountAmount;
                dto.SubscriptionDiscountAmount = resolvedSubscriptionDiscountAmount;

                // Loyalty + stacking preview — must match what CreateBooking will persist
                // downstream. Guests (just auto-created above) have no loyalty, so stacking
                // is a no-op for them.
                decimal loyaltyAmount = 0m;
                decimal loyaltyPct = 0m;
                if (userId > 0)
                {
                    var (candidateAmount, candidatePct) =
                        await _loyaltyDiscountService.CalculateForOrderAsync(userId, calculatedSubTotal);
                    (loyaltyAmount, loyaltyPct, dto.SubscriptionDiscountAmount, dto.DiscountAmount) =
                        _loyaltyDiscountService.ResolveStacking(
                            candidateAmount, candidatePct,
                            dto.SubscriptionDiscountAmount, dto.DiscountAmount);
                }

                // First pass: tax + pre-gift-card total (points/credits need DB lookups below).
                var preTotals = OrderPricingCalculator.CalculateTotals(new OrderPricingCalculator.TotalsInput
                {
                    SubTotal = calculatedSubTotal,
                    TaxOverride = quote.TaxOverride,
                    DiscountAmount = dto.DiscountAmount,
                    SubscriptionDiscountAmount = dto.SubscriptionDiscountAmount,
                    LoyaltyDiscountAmount = loyaltyAmount,
                    Tips = dto.Tips
                });
                var totalBeforeGiftCard = preTotals.TotalBeforeGiftCard;

                decimal pointsCredit = 0;
                if (dto.PointsToRedeem > 0 && userId > 0)
                {
                    var bubbleSvc = HttpContext.RequestServices.GetService<IBubblePointsService>();
                    if (bubbleSvc != null)
                    {
                        var pointsUser = await _context.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => new { u.BubblePoints }).FirstOrDefaultAsync();
                        if (pointsUser != null && pointsUser.BubblePoints >= dto.PointsToRedeem)
                        {
                            var (credit, valid, _) = await bubbleSvc.GetPointsCreditForBooking(dto.PointsToRedeem);
                            if (valid) pointsCredit = credit;
                        }
                    }
                }
                // Apply bubble credits
                decimal creditsApplied = 0;
                if (dto.UseCredits && userId > 0)
                {
                    var creditUser = await _context.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => new { u.BubbleCredits }).FirstOrDefaultAsync();
                    if (creditUser != null && creditUser.BubbleCredits > 0)
                    {
                        creditsApplied = Math.Min(creditUser.BubbleCredits, totalBeforeGiftCard - giftCardAmountUsed - pointsCredit);
                        creditsApplied = OrderPricingCalculator.Round2(creditsApplied);
                    }
                }

                // Final pass with every deduction applied.
                var totals = OrderPricingCalculator.CalculateTotals(new OrderPricingCalculator.TotalsInput
                {
                    SubTotal = calculatedSubTotal,
                    TaxOverride = quote.TaxOverride,
                    DiscountAmount = dto.DiscountAmount,
                    SubscriptionDiscountAmount = dto.SubscriptionDiscountAmount,
                    LoyaltyDiscountAmount = loyaltyAmount,
                    Tips = dto.Tips,
                    GiftCardAmountUsed = giftCardAmountUsed,
                    PointsRedeemedDiscount = pointsCredit,
                    RewardBalanceUsed = creditsApplied
                });
                decimal total = totals.Total;

                // When a gift card (or credits/points) fully covers the order, the payable total
                // is below Stripe's minimum charge. Skip the PaymentIntent entirely — the frontend
                // sees RequiresPayment=false, bypasses the card step, and confirms directly. The
                // gift card is still drawn down server-side during order creation in confirm-payment.
                var requiresPayment = total >= StripeMinimumChargeAmount;

                // ── Duplicate-booking guard (2026-08-30) ──────────────────────────────────────
                // This endpoint used to mint a fresh Ticks-based session id and a fresh
                // PaymentIntent on EVERY call, so a customer who reached the payment step twice
                // for one booking got two intents and could be charged on both. Identify the
                // logical attempt and reuse it instead.
                //
                // The fingerprint is computed HERE, after the DTO has been server-normalised
                // (gift-card draw clamped to the real balance, discounts server-resolved), and it
                // includes the recomputed total — so an attempt that now prices differently is
                // correctly treated as NEW rather than reusing an intent for the old amount.
                //
                // Scoped to a live session rather than to the booking content: once the session
                // is consumed at confirm-payment, a genuine re-booking of the identical service
                // mints a new session and a new intent, and is charged properly.
                var fingerprint = BookingSessionFingerprint.Compute(dto, total);

                string sessionId;
                string paymentIntentId = null;
                string paymentClientSecret = null;
                // Whether the intent handed back can carry the pre-payment "save your card" choice.
                var canSaveCard = false;

                // Set when the intent this attempt already holds turns out to have been CHARGED
                // (see ResolveAlreadyChargedPrepareAsync). Either the booking is already on file,
                // or it is not and the existing charge is what must pay for it.
                var alreadyBookedOrderId = 0;
                string alreadyBookedStatus = null;
                string alreadyPaidPaymentIntentId = null;

                // Find-or-create under a per-user lock: two genuinely concurrent prepares would
                // otherwise both miss the lookup and both create an intent.
                using (await _bookingDataService.AcquirePrepareLockAsync(userId, HttpContext.RequestAborted))
                {
                    var outstanding = _bookingDataService.FindOutstandingPreparedSession(userId, fingerprint);
                    PreparedBookingSession session;

                    if (outstanding != null)
                    {
                        session = outstanding;
                        sessionId = outstanding.SessionId;
                        paymentIntentId = outstanding.PaymentIntentId;
                        paymentClientSecret = outstanding.PaymentClientSecret;
                        canSaveCard = outstanding.CanSaveCard;
                        _logger.LogInformation(
                            "Reusing outstanding prepare-payment session {SessionId} for user {UserId} (intent {PaymentIntentId})",
                            sessionId, userId, paymentIntentId ?? "none yet");

                        // A reused attempt may ALREADY have been paid for. Handing its client
                        // secret back unconditionally assumes the card was never charged on it —
                        // and on 2026-09-16 that assumption cost a customer $386.16 twice.
                        if (!string.IsNullOrEmpty(paymentIntentId))
                        {
                            var charged = await ResolveAlreadyChargedPrepareAsync(paymentIntentId);
                            if (charged.OrderId > 0)
                            {
                                alreadyBookedOrderId = charged.OrderId;
                                alreadyBookedStatus = charged.Status;
                                paymentClientSecret = null;
                            }
                            else if (charged.IsPaidWithNoOrder)
                            {
                                alreadyPaidPaymentIntentId = paymentIntentId;
                                paymentClientSecret = null;
                            }
                        }
                    }
                    else
                    {
                        sessionId = $"prepare_payment_{userId}_{DateTime.UtcNow.Ticks}";
                        session = new PreparedBookingSession
                        {
                            SessionId = sessionId,
                            UserId = userId,
                            BookingData = dto,
                            Fingerprint = fingerprint,
                            Total = total
                        };
                        // Stored BEFORE the Stripe call, so a create that fails or times out
                        // still leaves a session the retry can find and reuse the id of.
                        _bookingDataService.StorePreparedSession(session);
                    }

                    // Nothing left to create when the money is already in: both already-charged
                    // outcomes leave paymentIntentId set, so this is belt-and-braces on the one
                    // condition that actually matters.
                    if (requiresPayment && string.IsNullOrEmpty(paymentIntentId))
                    {
                        // Create Stripe payment intent with sessionId in metadata
                        var metadata = new Dictionary<string, string>
                        {
                            { "sessionId", sessionId },
                            { "userId", userId.ToString() },
                            { "type", "booking" }
                        };

                        // Card on file: attach the Stripe Customer whenever we can identify one, so
                        // the frontend MAY confirm this intent with an already-saved card ("Pay with
                        // card ending ####"), and so a card typed now CAN be saved.
                        //
                        // WHETHER it is saved is decided later, by the customer, in the pre-payment
                        // "Save your card?" modal (2026-09) — and applied by the browser as
                        // setup_future_usage=off_session when it CONFIRMS this same intent, before
                        // any money moves. The intent is therefore never created with that flag: the
                        // old booking-form checkbox (SaveCardForFutureUse) is no longer read, so one
                        // payment can never carry two save decisions. Signed-in customers only — a
                        // guest's account is being created by this very request. Best-effort: if the
                        // customer profile can't be set up, the booking proceeds without saving.
                        string stripeCustomerId = null;
                        var saveCard = guestAuth == null && SavedCardsEnabled;
                        if (saveCard || !string.IsNullOrEmpty(user.StripeCustomerId))
                        {
                            try
                            {
                                stripeCustomerId = await _stripeService.CreateOrGetCustomerAsync(user);
                                await _context.SaveChangesAsync(); // persist a freshly created StripeCustomerId
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Card on file: could not create Stripe customer for user {UserId}; booking proceeds without card saving", userId);
                                saveCard = false;
                                stripeCustomerId = null;
                            }
                        }

                        // The session id doubles as the Stripe idempotency key: a retry of this
                        // same attempt (including one where the response above was lost) returns
                        // the intent Stripe already created instead of a second chargeable one.
                        var paymentIntent = await _stripeService.CreatePaymentIntentAsync(total, metadata,
                            customerId: stripeCustomerId,
                            saveCardForOffSession: false,
                            idempotencyKey: sessionId);

                        paymentIntentId = paymentIntent?.Id;
                        paymentClientSecret = paymentIntent?.ClientSecret;

                        session.PaymentIntentId = paymentIntentId;
                        session.PaymentClientSecret = paymentClientSecret;
                        session.CanSaveCard = saveCard && stripeCustomerId != null
                                              && paymentIntent?.CustomerId == stripeCustomerId;
                        canSaveCard = session.CanSaveCard;
                        _bookingDataService.StorePreparedSession(session);
                    }
                }

                // Guest auto-registration: in cookie-auth mode (production) the frontend
                // interceptor authenticates via cookies and ignores any body token, so the
                // GuestToken alone wouldn't authenticate the guest after booking. Set the
                // session cookies here so the freshly created guest stays logged in (their
                // order history, profile, etc. work without re-login). In token mode the
                // frontend uses GuestToken from the body via applyGuestAuth(), unchanged.
                if (guestAuth != null && _configuration.GetValue<bool>("Authentication:UseCookieAuth", false))
                {
                    SetGuestAuthCookies(guestAuth.Token, guestAuth.RefreshToken);
                }

                return Ok(new BookingResponseDto
                {
                    // 0 unless this attempt already produced an order — see the reuse branch.
                    OrderId = alreadyBookedOrderId,
                    Status = alreadyBookedStatus ?? "Pending",
                    Total = total,
                    // Either already-charged outcome owes nothing more.
                    RequiresPayment = requiresPayment && alreadyBookedOrderId == 0
                        && string.IsNullOrEmpty(alreadyPaidPaymentIntentId),
                    PaymentIntentId = paymentIntentId,
                    PaymentClientSecret = paymentClientSecret,
                    AlreadyPaidPaymentIntentId = alreadyPaidPaymentIntentId,
                    CanSaveCard = canSaveCard && SavedCardsEnabled && guestAuth == null,
                    SessionId = sessionId, // Return sessionId so frontend can use it in confirm-payment
                    // Guest booking: include auth token so frontend can authenticate before calling confirm-payment
                    GuestToken = guestAuth?.Token,
                    GuestRefreshToken = guestAuth?.RefreshToken,
                    GuestUser = guestAuth?.User
                });
            }
            catch (GuestAccountRequiresLoginException ex)
            {
                // The contact email belongs to somebody who signs in properly — see
                // GuestAccountClaimPolicy. Answered ahead of the generic catch below so the
                // customer reads "sign in and book from there" rather than "Failed to prepare
                // payment: ...", which reads as our fault and says nothing about what to do next.
                // "code" is the stable contract; the wording is free to change.
                return BadRequest(new
                {
                    code = GuestAccountRequiresLoginException.Code,
                    message = ex.Message
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = "Failed to prepare payment: " + ex.Message });
            }
        }


        /// <summary>
        /// Records the customer's SMS / cancellation-fee / terms consents for an admin-created
        /// order, before its first payment. Same three agreements the /booking form requires —
        /// an admin booking on the phone ticks them on the customer's behalf, so the customer
        /// re-confirms them here when they open the payment link.
        ///
        /// Enforcement lives in CreatePaymentIntentForOrder: no consent, no PaymentIntent, no
        /// client secret, so the card physically cannot be charged. Idempotent — the first
        /// acceptance wins and re-posting returns it unchanged.
        /// </summary>
        [HttpPost("accept-payment-consent/{orderId}")]
        [AllowAnonymous]
        public async Task<ActionResult<PaymentConsentResultDto>> AcceptPaymentConsent(
            int orderId, [FromBody] AcceptPaymentConsentDto dto, [FromQuery] string? guestToken = null)
        {
            // Same access rule as create-payment-intent: the owner, or anyone holding the
            // secret payment-link token.
            var userId = GetUserId();
            var order = await _context.Orders.FirstOrDefaultAsync(o => o.Id == orderId);

            if (order == null)
                return NotFound(new { message = "Order not found" });
            if (order.UserId != userId && !PaymentLinkHelper.TokenMatches(order, guestToken))
                return NotFound(new { message = "Order not found" });

            if (order.IsPaid)
                return BadRequest(new { message = "Order is already paid" });
            if (order.PaymentMethod != PaymentMethod.Normal)
                return BadRequest(new { message = "This order was paid outside the website and has no payment due." });

            // Already accepted (page reloaded, link opened twice) — keep the original record.
            if (order.PaymentConsentAcceptedAt != null)
            {
                return Ok(new PaymentConsentResultDto
                {
                    OrderId = order.Id,
                    AcceptedAt = order.PaymentConsentAcceptedAt.Value
                });
            }

            if (dto == null || !dto.SmsConsent || !dto.CancellationConsent || !dto.TermsConsent)
                return BadRequest(new { message = PaymentConsentPolicy.ConsentRequiredMessage });

            order.PaymentConsentAcceptedAt = DateTime.UtcNow;
            order.PaymentConsentIpAddress = GetClientIpAddress();
            await _context.SaveChangesAsync();

            _logger.LogInformation(
                "Payment consents accepted for order {OrderId} from {Ip}", order.Id, order.PaymentConsentIpAddress);

            return Ok(new PaymentConsentResultDto
            {
                OrderId = order.Id,
                AcceptedAt = order.PaymentConsentAcceptedAt.Value
            });
        }

        /// <summary>Client IP for the consent record. Production sits behind Cloudflare + Apache,
        /// so the socket address is the proxy — prefer CF-Connecting-IP, then the first
        /// X-Forwarded-For hop. Truncated to the column width.</summary>
        private string? GetClientIpAddress()
        {
            var ip = Request.Headers["CF-Connecting-IP"].FirstOrDefault()
                ?? Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim()
                ?? HttpContext.Connection.RemoteIpAddress?.ToString();
            if (string.IsNullOrWhiteSpace(ip))
                return null;
            return ip.Length > 45 ? ip.Substring(0, 45) : ip;
        }

        [HttpPost("create-payment-intent/{orderId}")]
        [AllowAnonymous]
        public async Task<ActionResult<BookingResponseDto>> CreatePaymentIntentForOrder(int orderId, [FromQuery] string? guestToken = null)
        {
            try
            {
                // Access rule: the order's owner, OR anyone presenting the secret payment-link
                // token (from /order/{id}/pay?t=...) — logged out or logged in as another user.
                // Token holders act on the owner's behalf.
                var userId = GetUserId();
                var order = await _context.Orders
                    .Include(o => o.ServiceType)
                    .FirstOrDefaultAsync(o => o.Id == orderId);

                if (order == null)
                    return NotFound(new { message = "Order not found" });
                if (order.UserId != userId && !PaymentLinkHelper.TokenMatches(order, guestToken))
                    return NotFound(new { message = "Order not found" });
                // Card on file is owner-only: a payment-link guest (often a relative paying on
                // the owner's behalf) must never be able to use the owner's saved card.
                var callerIsOwner = order.UserId == userId;
                userId = order.UserId;

                // The customer's Users-row lock, for EVERY order now (it used to be recurring-only):
                // a saved-card charge (admin, AutoPay) takes the same lock to claim the order, so
                // the two can never both believe they are first. See SavedCardChargeService.
                //
                // A "Pay all upcoming" charge Stripe already took is recorded BEFORE the lock, in its
                // own transaction: recorded inside the lock's, it would be rolled back by the
                // "already paid" refusal below and the cleanings would read unpaid until the webhook.
                if (order.RecurringSeriesId.HasValue)
                    await HttpContext.RequestServices.GetRequiredService<IRecurringCustomerPaymentService>().SettleSucceededBatchesAsync(userId);
                using var recurringPaymentTransaction = await RecurringPaymentAttemptGuard.LockAsync(_context, userId);
                if (!order.RecurringSeriesId.HasValue && recurringPaymentTransaction != null)
                    await _context.Entry(order).ReloadAsync();
                if (order.RecurringSeriesId.HasValue)
                {
                    await _context.Entry(order).ReloadAsync();
                    await HttpContext.RequestServices.GetRequiredService<IRecurringCustomerPaymentService>().PrepareIndividualPaymentAsync(order.Id);
                    var today = NyTimeHelper.NowNy.Date;
                    var nextId = await _context.Orders.Where(o => o.UserId == userId && o.RecurringSeriesId != null
                        && o.ServiceDate >= today && !o.IsPaid && o.InvoicePaidAt == null && o.PaymentMethod == PaymentMethod.Normal
                        && o.Total > 0 && o.Status != OrderStatuses.Cancelled && o.Status != OrderStatuses.Refunded)
                        .OrderBy(o => o.ServiceDate).ThenBy(o => o.ServiceTime).ThenBy(o => o.Id)
                        .Select(o => (int?)o.Id).FirstOrDefaultAsync();
                    if (order.ServiceDate >= today && nextId != order.Id)
                        return BadRequest(new { message = "Pay the nearest unpaid recurring cleaning first, or use Pay all upcoming." });
                }

                if (order.IsPaid)
                    return BadRequest(new { message = "Order is already paid" });

                // Manual-paid orders (Cash/Zelle/Check/Other) were settled outside Stripe and keep
                // IsPaid=false by design — there is nothing to charge on the website.
                if (order.PaymentMethod != PaymentMethod.Normal)
                    return BadRequest(new { message = "This order was paid outside the website and has no payment due." });

                // Consent gate for admin-created orders. The customer never saw the /booking
                // form's SMS / cancellation-fee / terms checkboxes — the admin ticked them on
                // the phone — so they accept them on the payment page first. Enforced HERE on
                // purpose: no PaymentIntent means no client secret, so the card cannot be
                // charged. Placed before the fully-covered branch so a $0 gift-card order is
                // gated too. Additional payments on an already-paid order never reach this
                // method (the IsPaid check above returns first).
                if (PaymentConsentPolicy.RequiresConsent(order))
                    return BadRequest(new { message = PaymentConsentPolicy.ConsentRequiredMessage, requiresConsent = true });

                // A saved-card charge (admin / AutoPay) holds this order right now. Issuing a
                // client secret beside it is how one cleaning gets paid twice.
                var savedCardLockKey = Models.Billing.BillingPaymentAttempt.OrderObligationKey(order.Id);
                if (await _context.BillingPaymentAttempts.AnyAsync(a => a.ActiveLockKey == savedCardLockKey))
                    return BadRequest(new
                    {
                        message = "A payment for this order is already being processed. Please wait a moment and refresh the page — do not pay again.",
                        code = "payment_in_progress"
                    });

                // What is actually left to charge. Identical to order.Total on every ordinary
                // order — AmountPaid is zero unless an admin has collected part of the total
                // through part-payments, and charging the full total then would take a deposit
                // the customer has already handed over a second time.
                var amountDue = OrderBalance.AmountDue(order);

                // Fully covered (e.g. gift card, or part-payments have all but cleared it) —
                // payable amount below Stripe's minimum. Skip the PaymentIntent; the frontend
                // confirms directly and confirm-payment marks it paid.
                if (amountDue < StripeMinimumChargeAmount)
                {
                    return Ok(new BookingResponseDto
                    {
                        OrderId = order.Id,
                        Status = order.Status,
                        Total = amountDue,
                        RequiresPayment = false,
                        PaymentIntentId = null,
                        PaymentClientSecret = null
                    });
                }

                // Create Stripe payment intent
                var metadata = new Dictionary<string, string>
                {
                    { "orderId", order.Id.ToString() },
                    { "userId", userId.ToString() },
                    { "type", "booking" }
                };

                // Attach the owner's Stripe Customer (when they're the caller and have one) so
                // the frontend MAY confirm this intent with their saved card. Attachment alone
                // never charges anything — paying still takes an explicit click.
                string ownerStripeCustomerId = null;
                if (callerIsOwner)
                {
                    ownerStripeCustomerId = await _context.Users
                        .AsNoTracking()
                        .Where(u => u.Id == order.UserId)
                        .Select(u => u.StripeCustomerId)
                        .FirstOrDefaultAsync();

                    // Saved cards on: make sure the owner HAS a Stripe Customer, so the payment
                    // page can offer "save this card" (setup_future_usage is set in the browser
                    // only when they tick it). Best-effort — never blocks the payment.
                    if (ownerStripeCustomerId == null && SavedCardsEnabled)
                    {
                        try
                        {
                            var owner = await _context.Users.FirstAsync(u => u.Id == order.UserId);
                            ownerStripeCustomerId = await _stripeService.CreateOrGetCustomerAsync(owner);
                            await _context.SaveChangesAsync();
                        }
                        catch (Exception customerEx)
                        {
                            _logger.LogWarning(customerEx, "Could not create a Stripe customer for order {OrderId}'s owner; paying without card saving.", order.Id);
                            ownerStripeCustomerId = null;
                        }
                    }
                }

                // ── One open intent per order ──
                // A second tab, a refresh or a double-click used to mint a SECOND live intent each
                // time, and every one of them stayed payable — two tabs could each take the full
                // amount. The intent already on the order is reused when it still matches exactly,
                // replaced (cancelled at Stripe first) when it doesn't, and reported when it has
                // already been paid, so the page confirms THAT payment instead of taking another.
                if (!string.IsNullOrEmpty(order.PaymentIntentId) && order.PaymentIntentId.StartsWith("pi_"))
                {
                    Stripe.PaymentIntent? existingIntent = null;
                    try
                    {
                        existingIntent = await _stripeService.GetPaymentIntentAsync(order.PaymentIntentId);
                    }
                    catch (ApplicationException lookupEx) when (lookupEx.Message.Contains("No such payment_intent", StringComparison.OrdinalIgnoreCase))
                    {
                        existingIntent = null; // an intent from another Stripe account/mode: nothing to reuse
                    }
                    catch (ApplicationException lookupEx)
                    {
                        _logger.LogWarning(lookupEx, "Could not verify the open payment intent on order {OrderId}.", order.Id);
                        return BadRequest(new { message = "We couldn't verify an earlier payment attempt for this order. Please try again in a moment." });
                    }

                    if (existingIntent != null)
                    {
                        if (existingIntent.Status == "succeeded")
                        {
                            // Paid, but the confirmation never reached us. Confirm THIS payment.
                            return Ok(new BookingResponseDto
                            {
                                OrderId = order.Id,
                                Status = order.Status,
                                Total = amountDue,
                                RequiresPayment = false,
                                AlreadyPaidPaymentIntentId = existingIntent.Id,
                                PaymentIntentId = existingIntent.Id
                            });
                        }

                        if (RecurringPaymentAttemptGuard.IsSubmitted(existingIntent.Status))
                            return BadRequest(new
                            {
                                message = "Your payment for this order is already being processed. Please wait a moment and refresh — do not pay again.",
                                code = "payment_in_progress"
                            });

                        var reusable = existingIntent.Status is "requires_payment_method" or "requires_confirmation" or "requires_action"
                                       && existingIntent.Amount == Services.StripeService.ToCents(amountDue)
                                       && string.Equals(existingIntent.Currency, "usd", StringComparison.OrdinalIgnoreCase)
                                       && existingIntent.CustomerId == ownerStripeCustomerId
                                       && existingIntent.Metadata != null
                                       && existingIntent.Metadata.GetValueOrDefault("type") == "booking"
                                       && existingIntent.Metadata.GetValueOrDefault("orderId") == order.Id.ToString();

                        if (reusable)
                        {
                            if (recurringPaymentTransaction != null) await recurringPaymentTransaction.CommitAsync();
                            return Ok(new BookingResponseDto
                            {
                                OrderId = order.Id,
                                Status = order.Status,
                                Total = amountDue,
                                RequiresPayment = true,
                                PaymentIntentId = existingIntent.Id,
                                PaymentClientSecret = existingIntent.ClientSecret,
                                CanSaveCard = callerIsOwner && SavedCardsEnabled && !string.IsNullOrEmpty(ownerStripeCustomerId)
                            });
                        }

                        if (existingIntent.Status != "canceled")
                        {
                            try
                            {
                                await RecurringPaymentAttemptGuard.CancelOpenAsync(_stripeService, existingIntent.Id);
                            }
                            catch (CombinedPaymentException cancelEx)
                            {
                                return BadRequest(new { message = cancelEx.Message, code = "payment_in_progress" });
                            }
                        }
                    }
                }

                var paymentIntent = await _stripeService.CreatePaymentIntentAsync(amountDue, metadata,
                    receiptEmail: OrderReceiptEmail(order), customerId: ownerStripeCustomerId);

                // Update order with payment intent ID
                order.PaymentIntentId = paymentIntent.Id;
                await _context.SaveChangesAsync();
                if (recurringPaymentTransaction != null) await recurringPaymentTransaction.CommitAsync();

                return Ok(new BookingResponseDto
                {
                    OrderId = order.Id,
                    Status = order.Status,
                    // The amount still owed, not the order's headline total — they differ only
                    // when part-payments have already been taken.
                    Total = amountDue,
                    RequiresPayment = true,
                    PaymentIntentId = paymentIntent.Id,
                    PaymentClientSecret = paymentIntent.ClientSecret,
                    CanSaveCard = callerIsOwner && SavedCardsEnabled && !string.IsNullOrEmpty(ownerStripeCustomerId)
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = "Failed to create payment intent: " + ex.Message });
            }
        }

        /// <summary>
        /// The order OWNER's Stripe Customer for a payment intent, created when saved cards are on
        /// and they have none yet — so the pre-payment "Save your card?" choice can be honoured
        /// on this intent. Null for anybody else (a payment-link payer must never save a card onto
        /// the owner's account) and on any failure: attaching a Customer is best-effort and never
        /// blocks the payment itself.
        /// </summary>
        private async Task<string?> ResolveOwnerStripeCustomerAsync(Order order, bool callerIsOwner)
        {
            if (!callerIsOwner) return null;
            var existing = await _context.Users.AsNoTracking()
                .Where(u => u.Id == order.UserId).Select(u => u.StripeCustomerId).FirstOrDefaultAsync();
            if (!string.IsNullOrEmpty(existing) || !SavedCardsEnabled) return existing;
            try
            {
                var owner = await _context.Users.FirstAsync(u => u.Id == order.UserId);
                var created = await _stripeService.CreateOrGetCustomerAsync(owner);
                await _context.SaveChangesAsync();
                return created;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not create a Stripe customer for order {OrderId}'s owner; paying without card saving.", order.Id);
                return null;
            }
        }

        /// <summary>Stripe receipt address for an order: the order's frozen contact email —
        /// never a no-email placeholder, null when the order has no usable address.</summary>
        private static string? OrderReceiptEmail(Order order)
        {
            var email = order.ContactEmail;
            if (string.IsNullOrWhiteSpace(email) || NoEmailHelper.IsPlaceholder(email))
                return null;
            return email;
        }

        [HttpPost("confirm-payment/{orderId}")]
        [AllowAnonymous]
        public async Task<ActionResult> ConfirmPayment(int orderId, [FromBody] ConfirmPaymentDto dto, [FromQuery] string paymentIntentId = null)
        {
            // Money-safety tracking: in the new-booking (sessionId) flow the card is charged in the
            // browser BEFORE the order is created here. If order creation then throws, the catch
            // block below refunds this payment intent so the customer is never left paying for an
            // order that doesn't exist. Only set on the sessionId path; the existing-order path
            // already has a persisted order and must not be refunded on a post-charge hiccup.
            string chargedNewBookingPaymentIntentId = null;
            bool newBookingOrderPersisted = false;
            try
            {
                var userId = GetUserId();

                // Body may not bind in some proxies; allow query fallback for existing-order flow
                var effectivePaymentIntentIdEarly = !string.IsNullOrWhiteSpace(dto?.PaymentIntentId) ? dto.PaymentIntentId : paymentIntentId;
                var effectiveSessionIdEarly = dto?.SessionId;

                // For guest bookings (no JWT), resolve userId from sessionId or payment intent metadata
                if (userId == 0)
                {
                    if (!string.IsNullOrEmpty(effectiveSessionIdEarly))
                    {
                        // sessionId format: prepare_payment_{userId}_{ticks}
                        var parts = effectiveSessionIdEarly.Split('_');
                        if (parts.Length >= 3 && int.TryParse(parts[2], out var parsedId) && parsedId > 0)
                            userId = parsedId;
                    }

                    if (userId == 0 && !string.IsNullOrEmpty(effectivePaymentIntentIdEarly))
                    {
                        try
                        {
                            var pi = await _stripeService.GetPaymentIntentAsync(effectivePaymentIntentIdEarly);
                            if (pi.Metadata != null && pi.Metadata.TryGetValue("userId", out var uidStr))
                                int.TryParse(uidStr, out userId);
                        }
                        catch { /* ignore Stripe errors here; will fail below */ }
                    }
                }

                // Payment link (tokenized /order/{id}/pay?t=...): the secret token lets ANYONE —
                // logged out or logged in as a different user — confirm an existing order's
                // payment on the owner's behalf. Existing-order flow only; never the sessionId
                // (new booking) path.
                if (orderId > 0 && !string.IsNullOrWhiteSpace(dto?.GuestToken))
                {
                    var tokenOrder = await _context.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId);
                    if (tokenOrder != null && PaymentLinkHelper.TokenMatches(tokenOrder, dto.GuestToken))
                        userId = tokenOrder.UserId;
                }

                if (userId == 0)
                    return Unauthorized();

                // Body may not bind in some proxies; allow query fallback for existing-order flow
                var effectivePaymentIntentId = !string.IsNullOrWhiteSpace(dto?.PaymentIntentId) ? dto.PaymentIntentId : paymentIntentId;
                var effectiveSessionId = dto?.SessionId;

                // A payment intent is required UNLESS this is a gift-card-fully-covered booking,
                // which carries a sessionId (new booking) or an existing orderId but no intent.
                // Those zero-charge paths are validated server-side below against the authoritative
                // order Total before being marked paid.
                if (string.IsNullOrWhiteSpace(effectivePaymentIntentId) &&
                    string.IsNullOrWhiteSpace(effectiveSessionId) && orderId <= 0)
                    return BadRequest(new { message = "Payment intent ID is required." });

                Order order = null;
                CreateBookingDto bookingDataDto = null;
                string sessionId = null;

                // If sessionId is provided, this is a new booking - create order first
                if (!string.IsNullOrEmpty(effectiveSessionId))
                {
                    sessionId = effectiveSessionId;
                    bookingDataDto = _bookingDataService.GetBookingData(sessionId);

                    if (bookingDataDto == null)
                    {
                        return BadRequest(new { message = "Booking data not found. Please start over." });
                    }

                    // A gift-card-fully-covered booking arrives here with no payment intent (the
                    // card step was skipped because the payable total was below Stripe's minimum).
                    var hasPaymentIntent = !string.IsNullOrWhiteSpace(effectivePaymentIntentId);

                    if (hasPaymentIntent)
                    {
                        // Verify payment with Stripe first before creating order
                        var paymentIntent = await _stripeService.GetPaymentIntentAsync(effectivePaymentIntentId);
                        if (paymentIntent.Status != "succeeded" && paymentIntent.Status != "processing")
                        {
                            return BadRequest(new { message = "Payment not completed" });
                        }

                        // Card is already charged at this point (verified succeeded above). Mark it so the
                        // catch block can refund if order creation throws before the order is persisted.
                        chargedNewBookingPaymentIntentId = effectivePaymentIntentId;

                        // One Stripe intent, one order (duplicate-booking fix, 2026-08-30). A
                        // replayed or duplicated confirm for an intent that ALREADY produced an
                        // order must return that order, not build a second one — and must not
                        // fall through to the tail below, which would re-mark it paid and
                        // re-consume the customer's loyalty discount, bubble points and reward
                        // credits a second time. The unique index on Order.PaymentIntentId is
                        // the backstop for the concurrent case this lookup can still lose.
                        var existingOrderForIntent = await _context.Orders
                            .AsNoTracking()
                            .FirstOrDefaultAsync(o => o.PaymentIntentId == effectivePaymentIntentId);

                        if (existingOrderForIntent != null)
                        {
                            _logger.LogWarning(
                                "ConfirmPayment: payment intent {PaymentIntentId} already produced order {OrderId}; returning it instead of creating a duplicate.",
                                effectivePaymentIntentId, existingOrderForIntent.Id);

                            return Ok(new
                            {
                                success = true,
                                message = "Payment completed successfully",
                                orderId = existingOrderForIntent.Id,
                                status = existingOrderForIntent.Status
                            });
                        }
                    }

                    // Now create the order using the booking data (reuse logic from CreateBooking).
                    // The gift card is drawn down here against its REAL balance, so order.Total below
                    // is authoritative regardless of what the client claimed.
                    // The intent id is stamped at INSERT time so the unique index can reject a
                    // concurrent duplicate; the assignment further down then changes nothing.
                    order = await CreateOrderFromBookingData(
                        bookingDataDto, userId,
                        hasPaymentIntent ? effectivePaymentIntentId : null);
                    newBookingOrderPersisted = true; // order row committed — refund net no longer applies
                    orderId = order.Id; // Update orderId for later use

                    if (!hasPaymentIntent)
                    {
                        // No charge was taken — only allow this when nothing is actually owed.
                        // order.Total already has the gift card applied; bubble points + reward
                        // credits (consumed later in this method) reduce it further. We PROJECT
                        // those here read-only, clamped to the user's REAL balances exactly as the
                        // consumption below does, so the check can't be gamed by inflated client
                        // values. Anything still owing $0.50+ means a real payment was skipped —
                        // reject before consuming anything and leave the order unpaid.
                        var projectedDeductions = await ProjectPostCreationDeductionsAsync(bookingDataDto, userId);
                        var remaining = order.Total - projectedDeductions;
                        if (remaining >= StripeMinimumChargeAmount)
                        {
                            _logger.LogWarning("Confirm-payment without a payment intent for payable order {OrderId} (remaining {Remaining}). Leaving unpaid.", order.Id, remaining);
                            return BadRequest(new { message = "Payment is required to complete this booking." });
                        }
                        // Synthetic reference so downstream code (which expects a non-null PaymentIntentId) works.
                        effectivePaymentIntentId = $"giftcard_full_{Guid.NewGuid():N}";
                    }
                }
                else
                {
                    // Existing order flow (admin-scheduled or profile payment) - same as booking confirm, just order already exists
                    order = await _context.Orders
                        .Include(o => o.OrderServices)
                        .Include(o => o.OrderExtraServices)
                            .ThenInclude(oes => oes.ExtraService)
                        .Include(o => o.ServiceType)
                        .FirstOrDefaultAsync(o => o.Id == orderId && o.UserId == userId);

                    if (order == null)
                        return NotFound(new { message = "Order not found" });

                    if (order.IsPaid)
                    {
                        // Never answer a customer who has just paid with an error: "Order is already
                        // paid" read as a failed payment and invited the second attempt this whole
                        // codebase is built to prevent (2026-09).
                        //  • Same intent → a replayed confirm (network retry, second tab): success.
                        //  • A DIFFERENT intent that really took money → a duplicate collection. It is
                        //    refunded automatically, once (idempotency key), and the customer is told.
                        if (!string.IsNullOrWhiteSpace(effectivePaymentIntentId)
                            && effectivePaymentIntentId != order.PaymentIntentId
                            && effectivePaymentIntentId.StartsWith("pi_"))
                        {
                            var duplicateRefunded = await RefundDuplicateCollectionAsync(order.Id, effectivePaymentIntentId);
                            return Ok(new
                            {
                                success = true,
                                message = duplicateRefunded
                                    ? "This order was already paid, so this second payment has been refunded automatically. You were not charged twice."
                                    : "This order was already paid. If you see a second charge, contact us and we will refund it right away.",
                                orderId = order.Id,
                                status = order.Status,
                                alreadyPaid = true,
                                duplicateRefunded
                            });
                        }

                        return Ok(new
                        {
                            success = true,
                            message = "Payment completed successfully",
                            orderId = order.Id,
                            status = order.Status,
                            alreadyPaid = true
                        });
                    }

                    var hasPaymentIntent = !string.IsNullOrWhiteSpace(effectivePaymentIntentId);

                    if (!hasPaymentIntent)
                    {
                        // Fully-covered existing order (e.g. gift card) — no Stripe charge possible.
                        // Only allow it when what is genuinely still OWED is below Stripe's
                        // minimum. That is the persisted Total on every ordinary order, and less
                        // than it only when part-payments have already been collected.
                        if (OrderBalance.AmountDue(order) >= StripeMinimumChargeAmount)
                            return BadRequest(new { message = "Payment is required to complete this booking." });

                        // Same consent gate as create-payment-intent. Safe to reject here because
                        // this branch charges NOTHING (the order is already fully covered) — the
                        // card path below must never be rejected post-charge, so it isn't.
                        if (PaymentConsentPolicy.RequiresConsent(order))
                            return BadRequest(new { message = PaymentConsentPolicy.ConsentRequiredMessage, requiresConsent = true });

                        effectivePaymentIntentId = $"giftcard_full_{Guid.NewGuid():N}";
                    }
                    else
                    {
                        // Verify payment with Stripe - payment already succeeded in browser
                        Stripe.PaymentIntent paymentIntent;
                        try
                        {
                            paymentIntent = await _stripeService.GetPaymentIntentAsync(effectivePaymentIntentId);
                        }
                        catch (Exception stripeEx)
                        {
                            _logger.LogError(stripeEx, $"ConfirmPayment Stripe GetPaymentIntent failed for order {orderId}");
                            return BadRequest(new { message = "Could not verify payment with Stripe. Please ensure you're using the same Stripe account (test/live) as the payment page. " + stripeEx.Message });
                        }

                        var status = paymentIntent.Status ?? "";
                        var paid = status.Equals("succeeded", StringComparison.OrdinalIgnoreCase) || status.Equals("processing", StringComparison.OrdinalIgnoreCase);
                        if (!paid)
                        {
                            await Task.Delay(2000);
                            paymentIntent = await _stripeService.GetPaymentIntentAsync(effectivePaymentIntentId);
                            status = paymentIntent.Status ?? "";
                            paid = status.Equals("succeeded", StringComparison.OrdinalIgnoreCase) || status.Equals("processing", StringComparison.OrdinalIgnoreCase);
                            if (!paid)
                                return BadRequest(new { message = "Payment not completed. Status: " + status });
                        }
                    }

                    order.PaymentIntentId = effectivePaymentIntentId;

                    sessionId = $"booking_{orderId}_{userId}";
                    var bookingData = _bookingDataService.GetBookingData(sessionId);
                    bookingDataDto = bookingData;
                }

                // Get the user to update phone number and check apartments
                var user = await _context.Users
                    .Include(u => u.Apartments)
                    .FirstOrDefaultAsync(u => u.Id == userId);

                if (user != null)
                {
                    // Backfill missing profile info from the booking contact fields. Apple/Google
                    // sign-ins can leave Phone/FirstName/LastName empty; without this, downstream
                    // notifications that read from user.Phone (cleaner reminders, payment reminders,
                    // etc.) silently skip the SMS.
                    var userInfoUpdated = false;
                    if (string.IsNullOrWhiteSpace(user.Phone) && !string.IsNullOrWhiteSpace(order.ContactPhone))
                    {
                        user.Phone = order.ContactPhone;
                        userInfoUpdated = true;
                    }
                    if (string.IsNullOrWhiteSpace(user.FirstName) && !string.IsNullOrWhiteSpace(order.ContactFirstName))
                    {
                        user.FirstName = order.ContactFirstName;
                        userInfoUpdated = true;
                    }
                    if (string.IsNullOrWhiteSpace(user.LastName) && !string.IsNullOrWhiteSpace(order.ContactLastName))
                    {
                        user.LastName = order.ContactLastName;
                        userInfoUpdated = true;
                    }
                    if (userInfoUpdated)
                    {
                        user.UpdatedAt = DateTime.UtcNow;
                    }

                    // Handle first-time order completion
                    if (user.FirstTimeOrder)
                    {
                        user.FirstTimeOrder = false;
                        user.UpdatedAt = DateTime.UtcNow;
                    }

                    // Your existing apartment logic stays exactly the same
                    if (order.ApartmentId == null &&
                        !string.IsNullOrEmpty(order.ApartmentName) &&
                        !string.IsNullOrEmpty(order.ServiceAddress) &&
                        !string.IsNullOrEmpty(order.City) &&
                        !string.IsNullOrEmpty(order.State) &&
                        !string.IsNullOrEmpty(order.ZipCode))
                    {
                        if (user.Apartments.Count < 10)
                        {
                            var existingApartmentByAddress = user.Apartments.FirstOrDefault(a =>
                                a.IsActive &&
                                a.Address.ToLower() == order.ServiceAddress.ToLower() &&
                                a.City.ToLower() == order.City.ToLower() &&
                                a.State.ToLower() == order.State.ToLower() &&
                                a.PostalCode.ToLower() == order.ZipCode.ToLower()
                            );

                            if (existingApartmentByAddress != null)
                            {
                                order.ApartmentId = existingApartmentByAddress.Id;
                                order.ApartmentName = existingApartmentByAddress.Name;
                            }
                            else
                            {
                                var existingApartmentByName = user.Apartments.FirstOrDefault(a =>
                                    a.IsActive &&
                                    a.Name.ToLower() == order.ApartmentName.ToLower()
                                );

                                if (existingApartmentByName != null)
                                {
                                    order.ApartmentId = existingApartmentByName.Id;

                                    existingApartmentByName.Name = order.ApartmentName;
                                    existingApartmentByName.Address = order.ServiceAddress;
                                    existingApartmentByName.AptSuite = order.AptSuite;
                                    existingApartmentByName.City = order.City;
                                    existingApartmentByName.State = order.State;
                                    existingApartmentByName.PostalCode = order.ZipCode;
                                    existingApartmentByName.SpecialInstructions = order.SpecialInstructions;
                                    existingApartmentByName.UpdatedAt = DateTime.UtcNow;
                                }
                                else
                                {
                                    var newApartment = new Apartment
                                    {
                                        UserId = userId,
                                        Name = order.ApartmentName,
                                        Address = order.ServiceAddress,
                                        AptSuite = order.AptSuite,
                                        City = order.City,
                                        State = order.State,
                                        PostalCode = order.ZipCode,
                                        SpecialInstructions = order.SpecialInstructions,
                                        CreatedAt = DateTime.UtcNow,
                                        IsActive = true
                                    };

                                    _context.Apartments.Add(newApartment);
                                    await _context.SaveChangesAsync();
                                    order.ApartmentId = newApartment.Id;
                                }
                            }
                        }
                        else
                        {
                            _logger.LogInformation($"User {userId} has reached the maximum of 10 apartments — skipping apartment creation for order {order.Id}");
                        }
                    }
                }

                // Mark order as paid
                order.IsPaid = true;
                order.PaidAt = DateTime.UtcNow;
                order.Status = "Active";
                order.PaymentIntentId = effectivePaymentIntentId;

                // Loyalty Discount consumption — if this order actually consumed a loyalty
                // discount (snapshot persisted on the order at booking time), let the service
                // zero the user's percentage, stamp LastUsedAt, and clear the reminder logs so
                // the next cycle can re-evaluate from scratch. Failures are logged but don't
                // break payment confirmation (paid is paid; we can reconcile state separately).
                if (order.LoyaltyDiscountAmount > 0m && order.LoyaltyDiscountPercentage > 0m)
                {
                    try
                    {
                        await _loyaltyDiscountService.ApplyToOrderAsync(order.Id);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Loyalty discount apply failed for order {OrderId} — order is paid but user state may be stale", order.Id);
                    }
                }

                // Deduct bubble points if used in booking (inline, same DbContext, before SaveChanges below)
                if (bookingDataDto != null && bookingDataDto.PointsToRedeem > 0 && userId > 0)
                {
                    try
                    {
                        var bubbleSvc = HttpContext.RequestServices.GetService<IBubblePointsService>();
                        if (bubbleSvc != null) await bubbleSvc.DeductPointsForBooking(userId, bookingDataDto.PointsToRedeem, order.Id);
                    }
                    catch (Exception ex) { _logger.LogError(ex, $"[BubblePoints] Deduct failed for order {order.Id}"); }
                }

                // Deduct bubble reward balance (credits) if used in booking
                if (bookingDataDto != null && bookingDataDto.UseCredits && bookingDataDto.CreditsToApply > 0 && userId > 0)
                {
                    try
                    {
                        var creditUser = await _context.Users.FindAsync(userId);
                        if (creditUser != null && creditUser.BubbleCredits > 0)
                        {
                            var deducted = Math.Min(creditUser.BubbleCredits, bookingDataDto.CreditsToApply);
                            creditUser.BubbleCredits = Math.Max(0, creditUser.BubbleCredits - deducted);
                            order.RewardBalanceUsed = deducted;
                            await _context.SaveChangesAsync();
                        }
                    }
                    catch (Exception ex) { _logger.LogError(ex, $"[BubbleCredits] Deduct failed for order {order.Id}"); }
                }

                // Re-derive the persisted Total from its canonical components now that bubble
                // points and reward balance have been applied. Those credits are set AFTER the
                // order's Total was first computed (in CreateOrderFromBookingData / CreateBooking),
                // and that earlier computation omitted them — so without this the stored Total was
                // too high by (PointsRedeemedDiscount + RewardBalanceUsed). That inflated Total also
                // inflated bubble points earned on completion (earned = Total − Tax − Tips). This
                // recompute is authoritative and idempotent: orders without points/credit are
                // unchanged. NOTE: the customer was already charged the correct amount at
                // prepare-payment; this only fixes what we persist/display.
                {
                    // Confirmation settles the server-priced order; it does not re-price its tax.
                    // Preserve the saved split for regular admin edits and recurring orders too,
                    // even after the temporary booking session has expired. Points, gift cards
                    // and reward credits are post-tax deductions, so they cannot invalidate it.
                    // The base is the saved POST-discount subtotal, as in the admin Total editor.
                    // Nothing here trusts a custom amount supplied by the payment-confirmation caller.

                    var recomputedTotals = OrderPricingCalculator.CalculateTotals(new OrderPricingCalculator.TotalsInput
                    {
                        SubTotal = order.SubTotal,
                        TaxOverride = order.Tax,
                        TaxOverrideBase = Math.Max(0m, order.SubTotal - order.DiscountAmount
                            - order.SubscriptionDiscountAmount - order.LoyaltyDiscountAmount),
                        DiscountAmount = order.DiscountAmount,
                        SubscriptionDiscountAmount = order.SubscriptionDiscountAmount,
                        LoyaltyDiscountAmount = order.LoyaltyDiscountAmount,
                        Tips = order.Tips,
                        CompanyDevelopmentTips = order.CompanyDevelopmentTips,
                        GiftCardAmountUsed = order.GiftCardAmountUsed,
                        PointsRedeemedDiscount = order.PointsRedeemedDiscount,
                        RewardBalanceUsed = order.RewardBalanceUsed
                    });
                    order.Tax = recomputedTotals.Tax;
                    order.Total = recomputedTotals.Total;
                    await _context.SaveChangesAsync();
                }

                // ── PAST THIS LINE THE BOOKING IS DONE ───────────────────────────────────
                // The order row is committed, IsPaid is true and the card has been charged.
                // Everything that follows is notifications and bookkeeping, so NONE of it may
                // be reported to the customer as a failed payment.
                //
                // On 2026-09-16 it was. A DbContext collision in that tail (see
                // Helpers/BackgroundWork) turned a completed, paid booking into
                // "Failed to confirm payment: A second operation was started on this context
                // instance..." — the customer read it as a decline, clicked Pay again, and was
                // charged a second time for the same cleaning. An order that exists and is paid
                // must be REPORTED as such whatever happens after it, or the error itself is
                // what produces the duplicate charge.
                try
                {
                    await CompleteConfirmedBookingAsync(
                        order, user, userId, bookingDataDto, sessionId, chargedNewBookingPaymentIntentId);
                }
                catch (Exception tailEx)
                {
                    _logger.LogError(tailEx,
                        "Order {OrderId} is paid and committed, but the post-confirmation work failed. " +
                        "The booking stands — reconcile notifications / subscription state separately.",
                        order.Id);
                }

                return Ok(new
                {
                    success = true,
                    message = "Payment completed successfully",
                    orderId = order.Id,
                    status = order.Status
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in ConfirmPayment");

                // Money-safety net: the card was charged for a new booking but the order failed to
                // persist. Refund automatically so the customer isn't charged for a non-existent
                // order, and invite them to reach out so we can help complete the booking.
                if (chargedNewBookingPaymentIntentId != null && !newBookingOrderPersisted)
                {
                    // ...unless an order for this intent DOES exist. With the unique index on
                    // Order.PaymentIntentId in place, the way a concurrent duplicate confirm now
                    // fails is that its insert is rejected — and the winner's order is the very
                    // thing this charge paid for. Refunding here would hand the customer a free
                    // cleaning. Only refund when the charge really did buy nothing.
                    try
                    {
                        var orderForCharge = await _context.Orders
                            .AsNoTracking()
                            .FirstOrDefaultAsync(o => o.PaymentIntentId == chargedNewBookingPaymentIntentId);

                        if (orderForCharge != null)
                        {
                            _logger.LogWarning(ex,
                                "ConfirmPayment failed for payment intent {PaymentIntentId}, but order {OrderId} exists for it (concurrent duplicate confirm). Not refunding; returning the existing order.",
                                chargedNewBookingPaymentIntentId, orderForCharge.Id);

                            return Ok(new
                            {
                                success = true,
                                message = "Payment completed successfully",
                                orderId = orderForCharge.Id,
                                status = orderForCharge.Status
                            });
                        }
                    }
                    catch (Exception lookupEx)
                    {
                        // Can't prove an order exists — fall through to the refund, which is the
                        // safe direction: a wrongly refunded booking is recoverable, a silently
                        // kept charge with no order is not.
                        _logger.LogError(lookupEx, "ConfirmPayment: duplicate-order lookup failed for payment intent {PaymentIntentId}", chargedNewBookingPaymentIntentId);
                    }

                    try
                    {
                        await _stripeService.CreateRefundAsync(chargedNewBookingPaymentIntentId);
                        _logger.LogError(ex, "ConfirmPayment: order creation failed after charge; refunded payment intent {PaymentIntentId}", chargedNewBookingPaymentIntentId);
                        // "code" is the stable contract the frontend keys severity off — the human
                        // message can be reworded without breaking detection.
                        return BadRequest(new { code = "booking_refunded", message = "We ran into a technical issue on our end while finalizing your booking, so it wasn't completed - but don't worry, any charge has been automatically refunded. Please reach out to us at hello@dreamcleaningnyc.com or call/text (929) 930-1525, and we'll be happy to help you complete your booking right away." });
                    }
                    catch (Exception refundEx)
                    {
                        _logger.LogError(refundEx, "CRITICAL: ConfirmPayment refund FAILED for payment intent {PaymentIntentId} after order creation error", chargedNewBookingPaymentIntentId);
                        // "code" is the stable contract the frontend keys severity off — the human
                        // message can be reworded without breaking detection.
                        return BadRequest(new { code = "booking_refund_failed", message = "We couldn't complete your booking and the automatic refund did not go through. Please do NOT retry — contact support and we'll resolve the charge right away." });
                    }
                }

                return BadRequest(new { message = "Failed to confirm payment: " + ex.Message });
            }
        }

        // ─── Part-payments (2026-09) ──────────────────────────────────────────────────────────
        //
        // An admin can split an unpaid order's total into slices the customer pays one at a time
        // ("$1,000 now, the rest before the cleaning"). Each slice is an OrderPartialPayment row
        // created from the admin panel; these two endpoints are what the payment link then opens.
        //
        // They are SEPARATE endpoints rather than a mode on create-payment-intent/confirm-payment
        // on purpose. Those two carry the whole residential booking flow — the gift-card branch,
        // the recurring sequencing lock, the post-charge refund net — and teaching them to
        // sometimes charge a different number would put every ordinary booking at risk of a
        // regression for the sake of a case none of them share.
        //
        // The one thing that is NOT duplicated is what happens when an order becomes fully paid:
        // the final slice hands over to ConfirmPayment, so the loyalty discount, the subscription
        // activation and the customer's booking confirmation are run by exactly the same code a
        // single full payment runs.

        /// <summary>
        /// Creates the Stripe intent for the slice an admin has asked this order's customer for.
        /// With <paramref name="payFullBalance"/> the payer settles everything still owed instead —
        /// the amount is derived from the order either way and never accepted from the client.
        /// </summary>
        [HttpPost("create-partial-payment-intent/{orderId}")]
        [AllowAnonymous]
        public async Task<ActionResult<PartialPaymentIntentDto>> CreatePartialPaymentIntent(
            int orderId, [FromQuery] string? guestToken = null, [FromQuery] bool payFullBalance = false)
        {
            try
            {
                // Same access rule as every other payment endpoint: the owner, or anyone holding
                // the secret payment-link token.
                var userId = GetUserId();
                var order = await _context.Orders.FirstOrDefaultAsync(o => o.Id == orderId);

                if (order == null)
                    return NotFound(new { message = "Order not found" });
                if (order.UserId != userId && !PaymentLinkHelper.TokenMatches(order, guestToken))
                    return NotFound(new { message = "Order not found" });

                var callerIsOwner = order.UserId == userId;

                if (order.IsPaid)
                    return BadRequest(new { message = "Order is already paid" });
                if (order.PaymentMethod != PaymentMethod.Normal)
                    return BadRequest(new { message = "This order was paid outside the website and has no payment due." });

                // A saved-card charge holds this order — see create-payment-intent.
                var partialLockKey = Models.Billing.BillingPaymentAttempt.OrderObligationKey(order.Id);
                if (await _context.BillingPaymentAttempts.AnyAsync(a => a.ActiveLockKey == partialLockKey))
                    return BadRequest(new
                    {
                        message = "A payment for this order is already being processed. Please wait a moment and refresh the page — do not pay again.",
                        code = "payment_in_progress"
                    });

                // Identical consent gate to the full-payment path, and enforced for the same
                // reason: no PaymentIntent means no client secret, so an admin-created order
                // physically cannot be charged before the customer accepts. A deposit is still a
                // first payment.
                if (PaymentConsentPolicy.RequiresConsent(order))
                    return BadRequest(new { message = PaymentConsentPolicy.ConsentRequiredMessage, requiresConsent = true });

                var partialPayments = HttpContext.RequestServices.GetRequiredService<IOrderPartialPaymentService>();
                var request = await partialPayments.GetPendingRequestAsync(orderId);
                if (request == null)
                    return BadRequest(new { message = "There is no payment request open for this order.", noPartialRequest = true });

                var amountDue = OrderBalance.AmountDue(order);

                // The requested slice is clamped to what is actually still owed. An admin lowering
                // the price after asking for a deposit must not be able to charge the old figure.
                var amount = payFullBalance
                    ? amountDue
                    : Math.Min(request.RequestedAmount, amountDue);

                var remainingAfter = OrderPricingCalculator.Round2(Math.Max(0m, amountDue - amount));
                var isFinalPayment = OrderBalance.SettlesOrder(remainingAfter);

                var response = new PartialPaymentIntentDto
                {
                    OrderId = order.Id,
                    PartialPaymentId = request.Id,
                    Amount = amount,
                    RequestedAmount = request.RequestedAmount,
                    AmountDue = amountDue,
                    RemainingAfterPayment = isFinalPayment ? 0m : remainingAfter,
                    IsFinalPayment = isFinalPayment
                };

                // Nothing chargeable left (credits or an edit took the balance under Stripe's
                // minimum). Confirming settles the order without a charge, exactly as the
                // gift-card-covered branch of the full-payment flow does.
                if (amount < StripeMinimumChargeAmount)
                {
                    response.RequiresPayment = false;
                    return Ok(response);
                }

                // The previous client secret must stop working BEFORE a new one exists. The payer
                // switching between "pay the deposit" and "pay the full balance", or simply
                // reloading, would otherwise leave two live intents for one slice and both could
                // be confirmed. Same rule the combined recurring payment follows.
                if (!string.IsNullOrWhiteSpace(request.PaymentIntentId))
                {
                    try
                    {
                        await RecurringPaymentAttemptGuard.CancelOpenAsync(_stripeService, request.PaymentIntentId);
                    }
                    catch (CombinedPaymentException ex)
                    {
                        return BadRequest(new { message = ex.Message });
                    }
                }

                var metadata = new Dictionary<string, string>
                {
                    { "orderId", order.Id.ToString() },
                    { "userId", order.UserId.ToString() },
                    { "partialPaymentId", request.Id.ToString() },
                    // NOT "booking": the webhook's booking handler marks the whole order paid from
                    // that discriminator alone, which is exactly what must not happen for a slice.
                    { "type", OrderPartialPaymentService.StripeMetadataType }
                };

                // Card on file is owner-only — a payment-link guest is often a relative paying on
                // the owner's behalf and must never reach the owner's saved card, nor save theirs
                // onto the owner's account.
                var ownerStripeCustomerId = await ResolveOwnerStripeCustomerAsync(order, callerIsOwner);

                var paymentIntent = await _stripeService.CreatePaymentIntentAsync(amount, metadata,
                    receiptEmail: OrderReceiptEmail(order), customerId: ownerStripeCustomerId);
                response.CanSaveCard = callerIsOwner && SavedCardsEnabled && !string.IsNullOrEmpty(ownerStripeCustomerId);

                // Stamped before the customer can pay, so a webhook arriving ahead of the browser's
                // confirm still finds the row this charge belongs to.
                request.PaymentIntentId = paymentIntent.Id;
                request.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();

                response.PaymentIntentId = paymentIntent.Id;
                response.PaymentClientSecret = paymentIntent.ClientSecret;
                return Ok(response);
            }
            catch (PartialPaymentException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create partial payment intent for order {OrderId}", orderId);
                return BadRequest(new { message = "Failed to create payment intent: " + ex.Message });
            }
        }

        /// <summary>
        /// Records a slice Stripe has taken. Idempotent — the browser and the webhook both call
        /// into the same settlement, and only the first moves the balance.
        ///
        /// When the slice clears the balance this hands over to <see cref="ConfirmPayment"/>, so an
        /// order completed by instalments finishes exactly as one paid in a single charge: the
        /// loyalty discount consumed, the subscription activated, the confirmation email and SMS
        /// sent. Nothing about "the order is now paid" is re-implemented here.
        /// </summary>
        [HttpPost("confirm-partial-payment/{orderId}")]
        [AllowAnonymous]
        public async Task<ActionResult> ConfirmPartialPayment(int orderId, [FromBody] ConfirmPaymentDto dto)
        {
            try
            {
                var paymentIntentId = dto?.PaymentIntentId;
                if (string.IsNullOrWhiteSpace(paymentIntentId))
                    return BadRequest(new { message = "Payment intent ID is required." });

                var userId = GetUserId();
                var order = await _context.Orders.FirstOrDefaultAsync(o => o.Id == orderId);
                if (order == null)
                    return NotFound(new { message = "Order not found" });
                if (order.UserId != userId && !PaymentLinkHelper.TokenMatches(order, dto?.GuestToken))
                    return NotFound(new { message = "Order not found" });

                // Verify with Stripe before anything is written. The client tells us WHICH intent;
                // Stripe tells us whether it succeeded and for how much.
                Stripe.PaymentIntent paymentIntent;
                try
                {
                    paymentIntent = await _stripeService.GetPaymentIntentAsync(paymentIntentId);
                }
                catch (Exception stripeEx)
                {
                    _logger.LogError(stripeEx, "ConfirmPartialPayment: could not read intent {PaymentIntentId} for order {OrderId}", paymentIntentId, orderId);
                    return BadRequest(new { message = "Could not verify payment with Stripe. " + stripeEx.Message });
                }

                // The intent must be one WE created for THIS order. Without this check a caller
                // could present any succeeded intent of their own and have its amount credited here.
                var intentOrderId = paymentIntent.Metadata != null
                    && paymentIntent.Metadata.TryGetValue("orderId", out var metaOrderId)
                    && int.TryParse(metaOrderId, out var parsedOrderId)
                        ? parsedOrderId
                        : 0;
                var intentType = paymentIntent.Metadata?.GetValueOrDefault("type");
                if (intentOrderId != orderId || intentType != OrderPartialPaymentService.StripeMetadataType)
                    return BadRequest(new { message = "That payment does not belong to this order." });

                var status = paymentIntent.Status ?? "";
                var paid = status.Equals("succeeded", StringComparison.OrdinalIgnoreCase)
                    || status.Equals("processing", StringComparison.OrdinalIgnoreCase);
                if (!paid)
                {
                    await Task.Delay(2000);
                    paymentIntent = await _stripeService.GetPaymentIntentAsync(paymentIntentId);
                    status = paymentIntent.Status ?? "";
                    paid = status.Equals("succeeded", StringComparison.OrdinalIgnoreCase)
                        || status.Equals("processing", StringComparison.OrdinalIgnoreCase);
                    if (!paid)
                        return BadRequest(new { message = "Payment not completed. Status: " + status });
                }

                var partialPayments = HttpContext.RequestServices.GetRequiredService<IOrderPartialPaymentService>();
                var settlement = await partialPayments.SettleAsync(
                    orderId, paymentIntentId, ResolveAmountReceived(paymentIntent));

                if (settlement.OrderNowFullyPaid && !order.IsPaid)
                {
                    // The balance is clear. Hand the finishing work to the ordinary confirmation
                    // path — it marks the order paid, consumes the loyalty discount, activates the
                    // subscription and sends the confirmation, and an order completed in slices
                    // must end up in exactly the state a single payment leaves it in.
                    return await ConfirmPayment(orderId, dto);
                }

                await _context.Entry(order).ReloadAsync();
                return Ok(new PartialPaymentConfirmationDto
                {
                    Success = true,
                    OrderId = order.Id,
                    AmountPaid = order.AmountPaid,
                    AmountDue = OrderBalance.AmountDue(order),
                    OrderFullyPaid = order.IsPaid,
                    Status = order.Status,
                    Message = settlement.Applied
                        ? "Payment received"
                        : "This payment has already been recorded"
                });
            }
            catch (PartialPaymentException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error confirming partial payment for order {OrderId}", orderId);
                return BadRequest(new { message = "Failed to confirm payment: " + ex.Message });
            }
        }

        /// <summary>
        /// What Stripe says actually arrived, in dollars. <c>AmountReceived</c> is the truthful
        /// figure but reads 0 while an intent is still "processing", so the authorized amount is
        /// the fallback — never a number supplied by the caller.
        /// </summary>
        private static decimal ResolveAmountReceived(Stripe.PaymentIntent paymentIntent)
        {
            var cents = paymentIntent.AmountReceived > 0 ? paymentIntent.AmountReceived : paymentIntent.Amount;
            return OrderPricingCalculator.Round2(cents / 100m);
        }

        /// <param name="paymentIntentId">The Stripe intent this order is being created for, or
        /// null for a fully-covered booking that takes no charge. Passed so the row carries it
        /// from the INSERT and the unique index on Order.PaymentIntentId can reject a concurrent
        /// duplicate — stamping it afterwards would let both racers commit a row first.</param>
        /// <summary>
        /// Everything ConfirmPayment does AFTER the order is persisted and marked paid:
        /// the customer email and SMS, the company notification, the uploaded photos, the
        /// subscription, the card on file, the first-time flag, and finally consuming the
        /// prepare session.
        ///
        /// Split out so its failures can be caught in one place. Nothing in here is load-bearing
        /// for the payment: the money has moved and the order exists before the first line of it
        /// runs. Its caller logs a failure and still answers the customer with their order.
        ///
        /// The session removal is deliberately the LAST statement — see the comment there.
        /// </summary>
        private async Task CompleteConfirmedBookingAsync(
            Order order,
            User? user,
            int userId,
            CreateBookingDto? bookingDataDto,
            string? sessionId,
            string? chargedNewBookingPaymentIntentId)
        {
            // Ensure extra services are loaded for email/SMS templates (new-booking flow may not include navigation properties).
            await _context.Entry(order).Collection(o => o.OrderExtraServices).Query().Include(oes => oes.ExtraService).LoadAsync();

            // Send booking confirmation email and SMS to customer.
            //
            // Gated on the same shared rule every other creation path uses. A commercial
            // invoice-backed order cannot normally reach here at all — this is the Stripe
            // confirmation, and an Invoice order is never charged through it — but the rule is
            // applied rather than assumed, so that a future path that routes one through
            // confirm-payment does not silently start mailing residential templates to a
            // commercial client. The company/admin notification below is internal and unaffected.
            var sendResidentialConfirmation =
                ResidentialBookingCommunicationPolicy.ShouldSendResidentialBookingCommunication(order);
            if (!sendResidentialConfirmation)
            {
                _logger.LogInformation(
                    "Order {OrderId} is billed through a commercial invoice; residential booking confirmation email/SMS suppressed.",
                    order.Id);
            }

            // EVERY value the three detached sends need is read off the order HERE, on the
            // request thread. `order` is tracked by the request DbContext and its change
            // tracker is not thread-safe, so detached work must never touch the entity —
            // see Helpers/BackgroundWork for the incident that made this a rule.
            var notifiedOrderId = order.Id;
            var contactEmail = order.ContactEmail;
            var contactPhone = !string.IsNullOrWhiteSpace(order.ContactPhone) ? order.ContactPhone : user?.Phone;
            var customerName = CapitalizeName(order.ContactFirstName);
            var addressDisplay = $"{order.ServiceAddress}{(!string.IsNullOrEmpty(order.AptSuite) ? $", {order.AptSuite}" : "")}";
            var serviceTimeStr = order.ServiceTime.ToString();
            var serviceDate = order.ServiceDate;
            var displayServiceTypeName = order.GetDisplayServiceTypeName();
            var floorTypes = order.FloorTypes;
            var floorTypeOther = order.FloorTypeOther;
            var propertyType = order.PropertyType;
            var levelsQuantity = order.LevelsQuantity;
            var contactFirstName = order.ContactFirstName;
            var contactLastName = order.ContactLastName;
            var orderContactPhone = order.ContactPhone;
            var serviceAddress = order.ServiceAddress;
            var aptSuite = order.AptSuite;
            var city = order.City;
            var state = order.State;
            var zipCode = order.ZipCode;
            var specialInstructions = order.SpecialInstructions;
            var uploadedPhotos = bookingDataDto?.UploadedPhotos;

            var extraNames = (order.OrderExtraServices ?? new List<OrderExtraService>())
                .Select(x => x.ExtraService?.Name ?? "")
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();

            var isCustomServiceType = order.ServiceType.IsCustom;
            var supplyChecklist = CustomerSupplyChecklist.Resolve(extraNames, isCustomServiceType);

            BackgroundWork.Run(_scopeFactory, _logger, $"booking confirmation email for order {notifiedOrderId}", async services =>
            {
                // Skip email if Apple hidden mail or no email
                var isAppleHiddenMail = !string.IsNullOrEmpty(contactEmail) &&
                    contactEmail.EndsWith("@privaterelay.appleid.com", StringComparison.OrdinalIgnoreCase);

                if (sendResidentialConfirmation && !isAppleHiddenMail && !string.IsNullOrWhiteSpace(contactEmail))
                {
                    await services.GetRequiredService<IEmailService>().SendCustomerBookingConfirmationAsync(
                        contactEmail,
                        customerName,
                        serviceDate,
                        serviceTimeStr,
                        displayServiceTypeName,
                        addressDisplay,
                        notifiedOrderId,
                        supplyChecklist,
                        floorTypes,
                        floorTypeOther,
                        propertyType: propertyType,
                        levelsQuantity: levelsQuantity
                    );
                    _logger.LogInformation($"Booking confirmation email sent to {contactEmail} for order {notifiedOrderId}");
                }
                else if (isAppleHiddenMail)
                {
                    _logger.LogInformation($"Skipping booking confirmation email for order {notifiedOrderId} - Apple hidden mail");
                }
            });

            // Send booking confirmation SMS if phone exists
            if (sendResidentialConfirmation && !string.IsNullOrWhiteSpace(contactPhone))
            {
                BackgroundWork.Run(_scopeFactory, _logger, $"booking confirmation SMS for order {notifiedOrderId}", async services =>
                {
                    await services.GetRequiredService<ISmsService>().SendBookingConfirmationSmsAsync(
                        contactPhone,
                        customerName,
                        serviceDate,
                        serviceTimeStr,
                        supplyChecklist
                    );
                    _logger.LogInformation($"Booking confirmation SMS sent to {contactPhone} for order {notifiedOrderId}");
                });
            }
            else
            {
                _logger.LogInformation($"Skipping booking confirmation SMS for order {notifiedOrderId} - no phone number");
            }

            // Send booking notification to company email with photos
            BackgroundWork.Run(_scopeFactory, _logger, $"company booking notification for order {notifiedOrderId}", async services =>
            {
                await services.GetRequiredService<IEmailService>().SendCompanyBookingNotificationAsync(
                    contactFirstName,
                    contactLastName,
                    contactEmail,
                    orderContactPhone,
                    serviceDate,
                    serviceTimeStr,
                    displayServiceTypeName,
                    serviceAddress,
                    aptSuite,
                    city,
                    state,
                    zipCode,
                    notifiedOrderId,
                    isCustomServiceType,
                    specialInstructions,
                    uploadedPhotos
                );
                _logger.LogInformation($"Booking notification with photos sent to company email for order {notifiedOrderId}");
            });

            // Persist booking-uploaded photos to the per-user cleaning photo library
            // (same resize/webp pipeline as the admin upload), then prune so the user
            // keeps only their two most recent cleanings on disk.
            await PersistBookingPhotosAsync(userId, order.Id, uploadedPhotos);

            // Handle subscription activation for paid orders
            var subscription = await _context.Subscriptions.FindAsync(order.SubscriptionId);
            if (subscription != null && subscription.SubscriptionDays > 0)
            {
                var userForSubscription = await _context.Users
                    .Include(u => u.Subscription)
                    .FirstOrDefaultAsync(u => u.Id == userId);
                bool hasActiveSubscription = await _subscriptionService.CheckAndUpdateSubscriptionStatus(userId);
                if (!hasActiveSubscription)
                {
                    var userSubscription = await _context.Subscriptions
                        .FirstOrDefaultAsync(s => s.SubscriptionDays == subscription.SubscriptionDays);
                    if (userSubscription != null)
                    {
                        await _subscriptionService.ActivateSubscription(userId, userSubscription.Id, order.ServiceDate);
                    }
                }
                else if (userForSubscription.SubscriptionId.HasValue)
                {
                    await _subscriptionService.RenewSubscription(userId, order.ServiceDate);
                }
            }

            // Update first-time order status if this is their first order
            var wasFirstTimeOrder = user?.FirstTimeOrder ?? false;
            if (user != null && user.FirstTimeOrder)
            {
                user.FirstTimeOrder = false;
                user.UpdatedAt = DateTime.UtcNow;
            }

            await _context.SaveChangesAsync();

            // Saved cards: the card that just paid THIS booking is recorded to the customer's
            // Billing tab — IF they chose "Save Card & Pay" in the pre-payment modal (2026-09).
            // That choice is not read from our request at all: it is Stripe's own record on the
            // intent (setup_future_usage=off_session, which only the browser's confirm sets), and
            // SaveFromPaymentIntentAsync saves nothing without it — so "Pay Without Saving"
            // saves nothing by construction. DETACHED, with its own DI scope (BackgroundWork): it
            // talks to Stripe, and nothing about saving a card may slow, fail or otherwise touch
            // the confirmation the customer is waiting for. Only a real charge qualifies. It never
            // turns AutoPay on. A save lost here is recovered by the Billing tab's reconciliation
            // with Stripe (PaymentMethodService.ReconcileWithStripeAsync).
            if (chargedNewBookingPaymentIntentId != null && userId > 0 && SavedCardsEnabled)
            {
                var saveForUserId = userId;
                var saveIntentId = chargedNewBookingPaymentIntentId;
                BackgroundWork.Run(_scopeFactory, _logger, $"save card from {saveIntentId}", async services =>
                {
                    var cards = services.GetRequiredService<Services.Billing.IPaymentMethodService>();
                    await cards.SaveFromPaymentIntentAsync(saveForUserId, saveIntentId);
                });
            }

            // Bubble Rewards: safety net — welcome bonus is granted at registration; this covers legacy accounts created before that
            if (wasFirstTimeOrder && userId > 0)
            {
                BackgroundWork.Run(_scopeFactory, _logger, $"welcome bonus for user {userId}", async services =>
                {
                    var bubbleService = services.GetService<IBubblePointsService>();
                    if (bubbleService != null)
                        await bubbleService.GrantWelcomeBonus(userId);
                });
            }

            // Consume the prepare session LAST. Everything above it can fail; while the
            // session is still here, a retry of this booking finds the intent that has
            // already been charged instead of minting a second one (see PreparePayment).
            if (!string.IsNullOrEmpty(sessionId))
                _bookingDataService.RemoveBookingData(sessionId);
        }

        /// <summary>
        /// "Has the card on this prepare attempt already been charged?" — asked of the DATABASE
        /// first and of Stripe second, for a session prepare-payment is about to reuse.
        ///
        /// WHY (2026-09-16). Reuse existed to stop a second PaymentIntent being minted for one
        /// booking, and it worked — for an attempt whose card had not been charged yet. It had
        /// nothing to say about the case that actually happened: the browser charged the card,
        /// confirm-payment created the order and then threw in its notification tail, and the
        /// customer read the error as a decline and clicked Pay again. Handing the same client
        /// secret back is no answer either — Stripe refuses to confirm an intent that has already
        /// succeeded, so the customer would simply be stuck.
        ///
        /// The two answers are different and both matter:
        ///   * an ORDER exists for the intent — the booking is done; say so and charge nothing;
        ///   * the intent SUCCEEDED but there is no order — the money is ours and the booking is
        ///     owed; the frontend re-confirms against THIS intent rather than paying again.
        ///
        /// The DB half is deliberately first: it is authoritative, it needs no network, and it
        /// is the half that is still right when Stripe is unreachable. Either lookup failing is
        /// answered with "don't know", which falls back to today's behaviour — this is a guard
        /// on top of the pre-insert lookup and the unique index on Order.PaymentIntentId, never
        /// a replacement for them.
        /// </summary>
        private async Task<(int OrderId, string? Status, bool IsPaidWithNoOrder)>
            ResolveAlreadyChargedPrepareAsync(string paymentIntentId)
        {
            try
            {
                var existing = await _context.Orders
                    .AsNoTracking()
                    .Where(o => o.PaymentIntentId == paymentIntentId)
                    .Select(o => new { o.Id, o.Status })
                    .FirstOrDefaultAsync();

                if (existing != null)
                {
                    _logger.LogWarning(
                        "Prepare-payment: intent {PaymentIntentId} already produced order {OrderId}; returning it instead of charging again.",
                        paymentIntentId, existing.Id);
                    return (existing.Id, existing.Status, false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Prepare-payment: could not check whether intent {PaymentIntentId} already has an order",
                    paymentIntentId);
                return (0, null, false);
            }

            try
            {
                var intent = await _stripeService.GetPaymentIntentAsync(paymentIntentId);
                var status = intent?.Status ?? "";
                var paid = status.Equals("succeeded", StringComparison.OrdinalIgnoreCase)
                    || status.Equals("processing", StringComparison.OrdinalIgnoreCase);

                if (!paid)
                    return (0, null, false);

                _logger.LogWarning(
                    "Prepare-payment: intent {PaymentIntentId} is {Status} on Stripe but has no order. " +
                    "Handing it back for confirmation instead of creating a second chargeable intent.",
                    paymentIntentId, status);
                return (0, null, true);
            }
            catch (Exception ex)
            {
                // Unreachable Stripe means "don't know", which is the same as "not charged" for
                // this decision: the customer is offered the existing intent's card step, and
                // confirm-payment's own guards still stand behind it.
                _logger.LogError(ex,
                    "Prepare-payment: could not read intent {PaymentIntentId} from Stripe", paymentIntentId);
                return (0, null, false);
            }
        }

        private async Task<Order> CreateOrderFromBookingData(CreateBookingDto dto, int userId,
            string paymentIntentId = null)
        {
            // Create + persist the order through the shared creation service: pricing via
            // the shared calculator, loyalty stacking for the order owner, special-offer
            // consumption and gift-card application — all in one transaction.
            //
            // allowCustomPricing: false — the only caller is ConfirmPayment, and the DTO it hands
            // over came from PreparePayment, where custom pricing is hard-disabled and the DTO was
            // normalised before being stored. Passing false here keeps the persisted order derived
            // from the SAME decision that produced the Stripe charge; re-deciding it (by role, at
            // confirm time) could disagree with the amount already captured.
            var order = await _bookingCreationService.CreateOrderAsync(dto, userId, allowCustomPricing: false,
                new BookingCreationOptions { PaymentIntentId = paymentIntentId });

            // Notify admins about new order
            await NotifyAdminsNewOrder(order.Id);

            // Reload order with related data
            return await _context.Orders
                .Include(o => o.OrderServices)
                .Include(o => o.ServiceType)
                .FirstOrDefaultAsync(o => o.Id == order.Id);
        }

        /// <summary>
        /// Billing:SavedCardsEnabled. Resolved lazily so the reflection-built controller in the
        /// booking-guard tests (no billing services registered) simply reads it as off.
        /// </summary>
        /// <summary>
        /// A payment arrived for an order that was already paid by a different charge. Refunds it
        /// in full, exactly once (the idempotency key is the intent itself), only after Stripe
        /// confirms it actually succeeded and belongs to this order. True when refunded.
        /// </summary>
        private async Task<bool> RefundDuplicateCollectionAsync(int orderId, string paymentIntentId)
        {
            try
            {
                var intent = await _stripeService.GetPaymentIntentAsync(paymentIntentId);
                if (intent.Status != "succeeded") return false;
                if (intent.Metadata == null || intent.Metadata.GetValueOrDefault("orderId") != orderId.ToString())
                {
                    _logger.LogWarning("Confirm for paid order {OrderId} named intent {PaymentIntentId}, which is not this order's; not refunding.",
                        orderId, paymentIntentId);
                    return false;
                }

                await _stripeService.CreateRefundAsync(paymentIntentId, null,
                    idempotencyKey: $"dc-duplicate-order-payment-{paymentIntentId}",
                    metadata: new Dictionary<string, string> { ["orderId"] = orderId.ToString(), ["reason"] = "duplicate_payment" });

                _logger.LogWarning("Order {OrderId} was already paid; duplicate payment {PaymentIntentId} refunded automatically.",
                    orderId, paymentIntentId);
                try
                {
                    await _auditService.LogActionAsync(AuditEntityTypes.OrderPaymentAction, orderId, "DuplicatePaymentRefunded",
                        null, new { PaymentIntentId = paymentIntentId, AmountCents = intent.AmountReceived });
                }
                catch (Exception auditEx) { _logger.LogError(auditEx, "Could not audit duplicate refund on order {OrderId}.", orderId); }
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "DUPLICATE PAYMENT NOT REFUNDED: order {OrderId}, intent {PaymentIntentId} — refund it manually.",
                    orderId, paymentIntentId);
                return false;
            }
        }

        private bool SavedCardsEnabled =>
            HttpContext?.RequestServices?.GetService<Services.Billing.BillingFeatures>()?.SavedCardsEnabled ?? false;

        private int GetUserId()
        {
            // Some parts of the codebase use custom "UserId" claim; fall back to NameIdentifier.
            var userIdClaim = User.FindFirst("UserId")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return int.TryParse(userIdClaim, out var userId) ? userId : 0;
        }

        // Read-only projection of the bubble-points + reward-credit dollar deductions that
        // ConfirmPayment applies AFTER order creation (the order's Total only has the gift card
        // applied at that point). Clamped to the user's REAL balances exactly as the consumption
        // code does, so it can be used to validate that a no-charge confirmation owes nothing.
        // Does NOT consume anything.
        private async Task<decimal> ProjectPostCreationDeductionsAsync(CreateBookingDto bookingDataDto, int userId)
        {
            if (bookingDataDto == null || userId <= 0)
                return 0m;

            decimal deductions = 0m;

            // Bubble points → dollar credit (mirrors the points deduction in ConfirmPayment).
            if (bookingDataDto.PointsToRedeem > 0)
            {
                var bubbleSvc = HttpContext.RequestServices.GetService<IBubblePointsService>();
                if (bubbleSvc != null)
                {
                    var pointsUser = await _context.Users.AsNoTracking()
                        .Where(u => u.Id == userId).Select(u => new { u.BubblePoints }).FirstOrDefaultAsync();
                    if (pointsUser != null && pointsUser.BubblePoints >= bookingDataDto.PointsToRedeem)
                    {
                        var (credit, valid, _) = await bubbleSvc.GetPointsCreditForBooking(bookingDataDto.PointsToRedeem);
                        if (valid) deductions += credit;
                    }
                }
            }

            // Bubble reward balance / credits (mirrors the credits deduction in ConfirmPayment).
            if (bookingDataDto.UseCredits && bookingDataDto.CreditsToApply > 0)
            {
                var creditUser = await _context.Users.AsNoTracking()
                    .Where(u => u.Id == userId).Select(u => new { u.BubbleCredits }).FirstOrDefaultAsync();
                if (creditUser != null && creditUser.BubbleCredits > 0)
                    deductions += Math.Min(creditUser.BubbleCredits, bookingDataDto.CreditsToApply);
            }

            return OrderPricingCalculator.Round2(deductions);
        }

        // Returns an error message if the promo code's MinimumOrderAmount isn't met by subTotal,
        // or null on success / nothing-to-check. Frontend can be bypassed by an API caller, so the
        // booking-creation flows must enforce this server-side too.
        private async Task<string?> ValidatePromoMinimumOrderAmountAsync(string? promoCode, decimal subTotal)
        {
            if (string.IsNullOrWhiteSpace(promoCode))
                return null;

            // Skip SPECIAL_OFFER-prefixed codes used internally to attribute special offers to orders.
            if (promoCode.StartsWith("SPECIAL_OFFER:", StringComparison.OrdinalIgnoreCase))
                return null;

            var pc = await _context.PromoCodes
                .FirstOrDefaultAsync(p => p.Code.ToLower() == promoCode.ToLower() && p.IsActive);
            if (pc == null)
                return null;

            if (pc.MinimumOrderAmount.HasValue && subTotal < pc.MinimumOrderAmount.Value)
                return $"Minimum order amount of ${pc.MinimumOrderAmount.Value:0.##} required to use promo code '{pc.Code}'";

            return null;
        }

        /// <summary>Persists booking-uploaded photos into the per-user cleaning photo library
        /// (same resize/webp pipeline as the admin upload), then prunes so the user keeps
        /// only their two most recent cleanings on disk. Failures are logged, never thrown.</summary>
        private async Task PersistBookingPhotosAsync(int userId, int orderId, List<PhotoUploadDto>? photos)
        {
            if (photos == null || photos.Count == 0) return;

            var savedAny = false;
            foreach (var photo in photos)
            {
                if (string.IsNullOrWhiteSpace(photo?.Base64Data)) continue;
                try
                {
                    var bytes = Convert.FromBase64String(photo.Base64Data);
                    using var ms = new MemoryStream(bytes);
                    await _userCleaningPhotoService.SavePhotoFromStreamAsync(
                        userId,
                        orderId,
                        ms,
                        caption: null);
                    savedAny = true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to persist booking photo for order {OrderId}", orderId);
                }
            }

            if (savedAny)
            {
                try
                {
                    await _userCleaningPhotoService.PruneOldPhotosAsync(userId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to prune old cleaning photos for user {UserId}", userId);
                }
            }
        }

        private string CapitalizeName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return name ?? string.Empty;
            
            // Capitalize first letter, lowercase the rest
            return name.Length > 1 
                ? char.ToUpper(name[0]) + name.Substring(1).ToLower()
                : name.ToUpper();
        }

        /// <summary>
        /// The start times the CALLER may book on <paramref name="date"/>. Customers get
        /// 8:00 AM - 6:00 PM, no earlier than 9:30 AM on Saturday/Sunday; Admin/SuperAdmin get
        /// 8:00 AM - 8:00 PM on every day, matching what the booking page offers them. Mirrors
        /// the frontend's shared/booking/service-time-slots.ts — change both together.
        /// </summary>
        [HttpGet("available-times")]
        public ActionResult<List<string>> GetAvailableTimeSlots(DateTime date, int serviceTypeId)
        {
            var isAdmin = User.IsInRole(UserRole.Admin.ToString())
                || User.IsInRole(UserRole.SuperAdmin.ToString());

            return Ok(ServiceTimeSlots.BuildForDate(date, isAdmin));
        }

        /// <summary>
        /// Notify all admin/superadmin users about a new order via SignalR.
        /// </summary>
        private async Task NotifyAdminsNewOrder(int orderId)
        {
            try
            {
                var adminUserIds = await _context.Users
                    .Where(u => u.Role == UserRole.Admin || u.Role == UserRole.SuperAdmin)
                    .Select(u => u.Id)
                    .ToListAsync();

                foreach (var adminId in adminUserIds)
                {
                    await _hubContext.Clients.Group($"User_{adminId}")
                        .SendAsync("NewOrderCreated", new { orderId });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to notify admins about new order {OrderId}", orderId);
            }
        }

        /// <summary>
        /// Public endpoint: returns blocked time slots for a date range so the booking UI
        /// can prevent users from selecting unavailable dates/hours.
        /// </summary>
        [HttpGet("blocked-time-slots")]
        public async Task<ActionResult> GetBlockedTimeSlots([FromQuery] string? from, [FromQuery] string? to)
        {
            var fromDate = DateTime.Today;
            var toDate = DateTime.Today.AddMonths(3);

            if (!string.IsNullOrEmpty(from) && DateTime.TryParse(from, out var parsedFrom))
                fromDate = parsedFrom.Date;
            if (!string.IsNullOrEmpty(to) && DateTime.TryParse(to, out var parsedTo))
                toDate = parsedTo.Date;

            var blocked = await _context.BlockedTimeSlots
                .Where(b => b.Date >= fromDate && b.Date <= toDate)
                .OrderBy(b => b.Date)
                .Select(b => new
                {
                    b.Id,
                    date = b.Date.ToString("yyyy-MM-dd"),
                    b.IsFullDay,
                    b.BlockedHours,
                    b.Reason
                })
                .ToListAsync();

            return Ok(blocked);
        }
    }
}
