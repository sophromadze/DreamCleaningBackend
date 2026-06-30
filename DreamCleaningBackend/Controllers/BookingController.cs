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
            IAdminBonusService adminBonusService)
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
        }

        // Mirrors AuthController.SetAuthCookies — used by the guest auto-registration path in
        // cookie-auth mode so a freshly created guest gets the same httpOnly session cookies a
        // normal login would. Kept in sync with AuthController (HttpOnly, Secure off only when
        // Development:UseHttp, SameSite=Strict, 7-day expiry to match the refresh token).
        private void SetGuestAuthCookies(string token, string refreshToken)
        {
            var secure = !_configuration.GetValue<bool>("Development:UseHttp", false);
            var cookieOptions = new CookieOptions
            {
                HttpOnly = true,
                Secure = secure,
                SameSite = SameSiteMode.Strict,
                Expires = DateTime.UtcNow.AddDays(7)
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
        private Task<OrderPricingCalculator.QuoteInput> BuildQuoteInputAsync(ServiceType serviceType, CreateBookingDto dto)
            => OrderPricingInputBuilder.FromBookingDtoAsync(_context, serviceType, dto);

        [HttpGet("service-types")]
        public async Task<ActionResult<List<ServiceTypeDto>>> GetServiceTypes()
        {
            var serviceTypes = await _context.ServiceTypes
                .Include(st => st.Services.Where(s => s.IsActive))
                .Where(st => st.IsActive)
                .OrderBy(st => st.DisplayOrder)
                .ToListAsync();

            // Custom service type visibility is enforced on the frontend (Admin/SuperAdmin only).
            // API returns all types so the client can show/hide based on role.

            // Get all extra services that are available for all
            var universalExtraServices = await _context.ExtraServices
                .Where(es => es.IsActive && es.IsAvailableForAll && es.ServiceTypeId == null)
                .OrderBy(es => es.DisplayOrder)
                .ToListAsync();

            var result = new List<ServiceTypeDto>();

            foreach (var st in serviceTypes)
            {
                // Get extra services specific to this service type
                var specificExtraServices = await _context.ExtraServices
                    .Where(es => es.IsActive && es.ServiceTypeId == st.Id && !es.IsAvailableForAll)
                    .OrderBy(es => es.DisplayOrder)
                    .ToListAsync();

                var serviceTypeDto = new ServiceTypeDto
                {
                    Id = st.Id,
                    Name = st.Name,
                    BasePrice = st.BasePrice,
                    Description = st.Description,
                    IsActive = st.IsActive,
                    DisplayOrder = st.DisplayOrder,
                    HasPoll = st.HasPoll,
                    IsCustom = st.IsCustom,
                    TimeDuration = st.TimeDuration,
                    Services = st.Services
                        .OrderBy(s => s.DisplayOrder)
                        .Select(s => new ServiceDto
                        {
                            Id = s.Id,
                            Name = s.Name,
                            ServiceKey = s.ServiceKey,
                            Cost = s.Cost,
                            TimeDuration = s.TimeDuration,
                            ServiceTypeId = s.ServiceTypeId,
                            InputType = s.InputType,
                            MinValue = s.MinValue,
                            MaxValue = s.MaxValue,
                            StepValue = s.StepValue,
                            IsRangeInput = s.IsRangeInput,
                            Unit = s.Unit,
                            ServiceRelationType = s.ServiceRelationType,
                            IsActive = s.IsActive,
                            DisplayOrder = s.DisplayOrder
                        }).ToList(),
                    ExtraServices = new List<ExtraServiceDto>()
                };

                // Add specific extra services first
                serviceTypeDto.ExtraServices.AddRange(specificExtraServices.Select(es => new ExtraServiceDto
                {
                    Id = es.Id,
                    Name = es.Name,
                    Description = es.Description,
                    Price = es.Price,
                    Duration = es.Duration,
                    Icon = es.Icon,
                    HasQuantity = es.HasQuantity,
                    HasHours = es.HasHours,
                    IsDeepCleaning = es.IsDeepCleaning,
                    IsSuperDeepCleaning = es.IsSuperDeepCleaning,
                    IsSameDayService = es.IsSameDayService,
                    PriceMultiplier = es.PriceMultiplier,
                    IsAvailableForAll = es.IsAvailableForAll,
                    IsActive = es.IsActive,
                    DisplayOrder = es.DisplayOrder
                }));

                // Add universal extra services
                serviceTypeDto.ExtraServices.AddRange(universalExtraServices.Select(es => new ExtraServiceDto
                {
                    Id = es.Id,
                    Name = es.Name,
                    Description = es.Description,
                    Price = es.Price,
                    Duration = es.Duration,
                    Icon = es.Icon,
                    HasQuantity = es.HasQuantity,
                    HasHours = es.HasHours,
                    IsDeepCleaning = es.IsDeepCleaning,
                    IsSuperDeepCleaning = es.IsSuperDeepCleaning,
                    IsSameDayService = es.IsSameDayService,
                    PriceMultiplier = es.PriceMultiplier,
                    IsAvailableForAll = es.IsAvailableForAll,
                    IsActive = es.IsActive,
                    DisplayOrder = es.DisplayOrder
                }));

                serviceTypeDto.ExtraServices = serviceTypeDto.ExtraServices
                    .OrderBy(es => es.DisplayOrder)
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

            if (user.SubscriptionId == null)
            {
                return Ok(new { hasSubscription = false });
            }

            return Ok(new
            {
                hasSubscription = true,
                subscriptionId = user.SubscriptionId,
                subscriptionName = user.Subscription.Name,
                discountPercentage = user.Subscription.DiscountPercentage,
                expiryDate = user.SubscriptionExpiryDate,
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

            var quoteInput = await BuildQuoteInputAsync(serviceType, dto);
            var quote = OrderPricingCalculator.CalculateQuote(quoteInput);

            var totals = OrderPricingCalculator.CalculateTotals(new OrderPricingCalculator.TotalsInput
            {
                SubTotal = quote.SubTotal,
                DiscountAmount = dto.DiscountAmount,
                SubscriptionDiscountAmount = dto.SubscriptionDiscountAmount,
                LoyaltyDiscountAmount = dto.LoyaltyDiscountAmount,
                Tips = dto.Tips,
                CompanyDevelopmentTips = dto.CompanyDevelopmentTips,
                GiftCardAmountUsed = dto.GiftCardAmountToUse
            });

            var calculation = new BookingCalculationDto
            {
                SubTotal = quote.SubTotal,
                Tax = totals.Tax,
                DiscountAmount = dto.DiscountAmount + dto.SubscriptionDiscountAmount + dto.LoyaltyDiscountAmount,
                Tips = dto.Tips,
                CompanyDevelopmentTips = dto.CompanyDevelopmentTips,
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

                // Resolve gift-card-vs-promo via the shared rule, then validate promo minimum.
                var (promoCode, _, _) = _bookingCreationService.ResolveGiftCardAndPromo(dto);
                var promoMinError = await ValidatePromoMinimumOrderAmountAsync(promoCode, dto.SubTotal);
                if (promoMinError != null)
                    return BadRequest(new { message = promoMinError });

                var subscription = await _context.Subscriptions.FindAsync(dto.SubscriptionId);

                // Create + persist the order through the shared creation service: pricing via
                // the shared calculator, loyalty stacking for the booking user, special-offer
                // consumption and gift-card application — all in one transaction.
                var order = await _bookingCreationService.CreateOrderAsync(dto, userId);

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

                var paymentIntent = await _stripeService.CreatePaymentIntentAsync(order.Total, metadata);

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

                // Parse manual payment method up front so order construction can choose the
                // initial Status (Pending for Stripe / Normal, Active for manual). Anything
                // unrecognised falls back to Normal — defensive default keeps the original flow.
                var paymentMethod = PaymentMethod.Normal;
                if (!string.IsNullOrWhiteSpace(dto.PaymentMethod) &&
                    !Enum.TryParse<PaymentMethod>(dto.PaymentMethod, ignoreCase: true, out paymentMethod))
                {
                    paymentMethod = PaymentMethod.Normal;
                }
                var initialStatus = paymentMethod == PaymentMethod.Normal ? "Pending" : "Active";

                // Get service type
                var serviceType = await _context.ServiceTypes
                    .Include(st => st.Services)
                    .FirstOrDefaultAsync(st => st.Id == dto.BookingData.ServiceTypeId);

                if (serviceType == null)
                    return BadRequest(new { message = "Invalid service type" });

                // Resolve gift-card-vs-promo via the shared rule, then validate promo minimum.
                var (promoCode, _, _) = _bookingCreationService.ResolveGiftCardAndPromo(dto.BookingData);
                var promoMinError = await ValidatePromoMinimumOrderAmountAsync(promoCode, dto.BookingData.SubTotal);
                if (promoMinError != null)
                    return BadRequest(new { message = promoMinError });

                // Create + persist the order through the shared creation service. Loyalty
                // stacking resolves against the TARGET customer (never the logged-in admin);
                // manual payment methods stamp the tracking fields and start the order Active.
                var order = await _bookingCreationService.CreateOrderAsync(dto.BookingData, dto.TargetUserId, new BookingCreationOptions
                {
                    InitialStatus = initialStatus,
                    PaymentMethod = paymentMethod,
                    PaymentReference = dto.PaymentReference,
                    PaymentNotes = dto.PaymentNotes,
                    ManualPaymentRecordedByUserId = adminUserId
                });

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
                        .Select(n => n.ToLowerInvariant())
                        .ToList();
                    var manualHasCleaningSupplies = manualExtraNames.Any(n => n.Contains("cleaning supplies"));
                    var manualIsDeepCleaning = manualExtraNames.Any(n => n.Contains("super deep cleaning")) ||
                                               manualExtraNames.Any(n => n.Contains("deep cleaning") && !n.Contains("super"));
                    var manualIsCustomServiceType = order.ServiceType?.IsCustom ?? false;

                    // Fire-and-forget email (skip Apple hidden mail). Same isAppleHiddenMail
                    // check the Stripe path uses below — keep behavior aligned.
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var isAppleHiddenMail = !string.IsNullOrEmpty(manualContactEmail) &&
                                manualContactEmail.EndsWith("@privaterelay.appleid.com", StringComparison.OrdinalIgnoreCase);
                            if (!isAppleHiddenMail && !string.IsNullOrWhiteSpace(manualContactEmail))
                            {
                                await _emailService.SendCustomerBookingConfirmationAsync(
                                    manualContactEmail, manualCustomerName, order.ServiceDate, manualServiceTimeStr,
                                    order.GetDisplayServiceTypeName(), manualAddressDisplay, order.Id,
                                    manualHasCleaningSupplies, manualIsDeepCleaning, manualIsCustomServiceType,
                                    order.FloorTypes, order.FloorTypeOther,
                                    // Manual payment path: customer pays cleaners on arrival, so drop
                                    // the "payment processed successfully" phrasing from the greeting.
                                    paymentAlreadyProcessed: false);
                                _logger.LogInformation($"Manual-payment booking confirmation email sent to {manualContactEmail} for order {order.Id}");
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"Failed to send manual-payment booking confirmation email for order {order.Id}");
                        }
                    });

                    if (!string.IsNullOrWhiteSpace(manualContactPhone))
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await _smsService.SendBookingConfirmationSmsAsync(
                                    manualContactPhone, manualCustomerName, order.ServiceDate, manualServiceTimeStr,
                                    manualHasCleaningSupplies, manualIsDeepCleaning, manualIsCustomServiceType);
                                _logger.LogInformation($"Manual-payment booking confirmation SMS sent to {manualContactPhone} for order {order.Id}");
                            }
                            catch (InvalidPhoneNumberException)
                            {
                                _logger.LogWarning($"Manual-payment SMS skipped for order {order.Id}: invalid phone");
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, $"Failed to send manual-payment booking confirmation SMS for order {order.Id}");
                            }
                        });
                    }

                    // Manual-payment orders never reach confirm-payment, so persist any
                    // booking-uploaded photos to the cleaning photo library here instead.
                    await PersistBookingPhotosAsync(dto.TargetUserId, order.Id, dto.BookingData.UploadedPhotos);

                    _logger.LogInformation($"Admin booking created with manual payment {paymentMethod}: order {order.Id}, status {order.Status}, isPaid {order.IsPaid}");

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
                        var orderLink = $"{frontendUrl}/order/{order.Id}/pay";
                        
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
                        if (!isAppleHiddenMail && !string.IsNullOrWhiteSpace(targetUser.Email))
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
                        if (!string.IsNullOrWhiteSpace(targetUser.Phone))
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

                // Resolve gift-card-vs-promo via the shared rule (single definition in BookingCreationService).
                var (promoCode, giftCardCode, giftCardAmountUsed) = _bookingCreationService.ResolveGiftCardAndPromo(dto);

                var promoMinError = await ValidatePromoMinimumOrderAmountAsync(promoCode, dto.SubTotal);
                if (promoMinError != null)
                    return BadRequest(new { message = promoMinError });

                // Price through the shared calculator (single source of truth — see OrderPricingCalculator).
                var quoteInput = await BuildQuoteInputAsync(serviceType, dto);
                var quote = OrderPricingCalculator.CalculateQuote(quoteInput);

                if (Math.Abs(dto.TotalDuration - quote.TotalDuration) > 5)
                {
                    _logger.LogWarning($"Duration mismatch — frontend sent {dto.TotalDuration}, backend calculated {quote.TotalDuration}. Backend value wins.");
                }

                // Loyalty + stacking preview — this endpoint produces the payment-intent amount,
                // so the numbers must match what CreateBooking will persist downstream. Guests
                // (just auto-created above) have no loyalty, so stacking is a no-op for them.
                decimal calculatedSubTotal = quote.SubTotal;
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
                    DiscountAmount = dto.DiscountAmount,
                    SubscriptionDiscountAmount = dto.SubscriptionDiscountAmount,
                    LoyaltyDiscountAmount = loyaltyAmount,
                    Tips = dto.Tips,
                    CompanyDevelopmentTips = dto.CompanyDevelopmentTips
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
                    DiscountAmount = dto.DiscountAmount,
                    SubscriptionDiscountAmount = dto.SubscriptionDiscountAmount,
                    LoyaltyDiscountAmount = loyaltyAmount,
                    Tips = dto.Tips,
                    CompanyDevelopmentTips = dto.CompanyDevelopmentTips,
                    GiftCardAmountUsed = giftCardAmountUsed,
                    PointsRedeemedDiscount = pointsCredit,
                    RewardBalanceUsed = creditsApplied
                });
                decimal total = totals.Total;

                // Store booking data temporarily (will be used to create order after payment succeeds)
                var sessionId = $"prepare_payment_{userId}_{DateTime.UtcNow.Ticks}";
                _bookingDataService.StoreBookingData(sessionId, dto);

                // When a gift card (or credits/points) fully covers the order, the payable total
                // is below Stripe's minimum charge. Skip the PaymentIntent entirely — the frontend
                // sees RequiresPayment=false, bypasses the card step, and confirms directly. The
                // gift card is still drawn down server-side during order creation in confirm-payment.
                var requiresPayment = total >= StripeMinimumChargeAmount;
                Stripe.PaymentIntent paymentIntent = null;

                if (requiresPayment)
                {
                    // Create Stripe payment intent with sessionId in metadata
                    var metadata = new Dictionary<string, string>
                    {
                        { "sessionId", sessionId },
                        { "userId", userId.ToString() },
                        { "type", "booking" }
                    };

                    paymentIntent = await _stripeService.CreatePaymentIntentAsync(total, metadata);
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
                    OrderId = 0, // No order created yet
                    Status = "Pending",
                    Total = total,
                    RequiresPayment = requiresPayment,
                    PaymentIntentId = paymentIntent?.Id,
                    PaymentClientSecret = paymentIntent?.ClientSecret,
                    SessionId = sessionId, // Return sessionId so frontend can use it in confirm-payment
                    // Guest booking: include auth token so frontend can authenticate before calling confirm-payment
                    GuestToken = guestAuth?.Token,
                    GuestRefreshToken = guestAuth?.RefreshToken,
                    GuestUser = guestAuth?.User
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = "Failed to prepare payment: " + ex.Message });
            }
        }

        [HttpPost("create-payment-intent/{orderId}")]
        [Authorize]
        public async Task<ActionResult<BookingResponseDto>> CreatePaymentIntentForOrder(int orderId)
        {
            try
            {
                var userId = GetUserId();
                if (userId == 0)
                    return Unauthorized();

                var order = await _context.Orders
                    .Include(o => o.ServiceType)
                    .FirstOrDefaultAsync(o => o.Id == orderId && o.UserId == userId);

                if (order == null)
                    return NotFound(new { message = "Order not found" });

                if (order.IsPaid)
                    return BadRequest(new { message = "Order is already paid" });

                // Manual-paid orders (Cash/Zelle/Check/Other) were settled outside Stripe and keep
                // IsPaid=false by design — there is nothing to charge on the website.
                if (order.PaymentMethod != PaymentMethod.Normal)
                    return BadRequest(new { message = "This order was paid outside the website and has no payment due." });

                // Fully covered (e.g. gift card) — payable total below Stripe's minimum. Skip the
                // PaymentIntent; the frontend confirms directly and confirm-payment marks it paid.
                if (order.Total < StripeMinimumChargeAmount)
                {
                    return Ok(new BookingResponseDto
                    {
                        OrderId = order.Id,
                        Status = order.Status,
                        Total = order.Total,
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

                var paymentIntent = await _stripeService.CreatePaymentIntentAsync(order.Total, metadata);

                // Update order with payment intent ID
                order.PaymentIntentId = paymentIntent.Id;
                await _context.SaveChangesAsync();

                return Ok(new BookingResponseDto
                {
                    OrderId = order.Id,
                    Status = order.Status,
                    Total = order.Total,
                    RequiresPayment = true,
                    PaymentIntentId = paymentIntent.Id,
                    PaymentClientSecret = paymentIntent.ClientSecret
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = "Failed to create payment intent: " + ex.Message });
            }
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
                    }

                    // Now create the order using the booking data (reuse logic from CreateBooking).
                    // The gift card is drawn down here against its REAL balance, so order.Total below
                    // is authoritative regardless of what the client claimed.
                    order = await CreateOrderFromBookingData(bookingDataDto, userId);
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
                        return BadRequest(new { message = "Order is already paid" });

                    var hasPaymentIntent = !string.IsNullOrWhiteSpace(effectivePaymentIntentId);

                    if (!hasPaymentIntent)
                    {
                        // Fully-covered existing order (e.g. gift card) — no Stripe charge possible.
                        // Only allow it when the persisted Total is genuinely below Stripe's minimum.
                        if (order.Total >= StripeMinimumChargeAmount)
                            return BadRequest(new { message = "Payment is required to complete this booking." });

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
                        var paid = status.Equals("succeeded", StringComparison.OrdinalIgnoreCase) || status.Equals("processing", StringComparison.OrdinalIgnoreCase) || status.Equals("requires_capture", StringComparison.OrdinalIgnoreCase);
                        if (!paid)
                        {
                            await Task.Delay(2000);
                            paymentIntent = await _stripeService.GetPaymentIntentAsync(effectivePaymentIntentId);
                            status = paymentIntent.Status ?? "";
                            paid = status.Equals("succeeded", StringComparison.OrdinalIgnoreCase) || status.Equals("processing", StringComparison.OrdinalIgnoreCase) || status.Equals("requires_capture", StringComparison.OrdinalIgnoreCase);
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
                    var recomputedTotals = OrderPricingCalculator.CalculateTotals(new OrderPricingCalculator.TotalsInput
                    {
                        SubTotal = order.SubTotal,
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

                // Ensure extra services are loaded for email/SMS templates (new-booking flow may not include navigation properties).
                await _context.Entry(order).Collection(o => o.OrderExtraServices).Query().Include(oes => oes.ExtraService).LoadAsync();

                // Send booking confirmation email and SMS to customer
                var contactEmail = order.ContactEmail;
                var contactPhone = !string.IsNullOrWhiteSpace(order.ContactPhone) ? order.ContactPhone : user?.Phone;
                var customerName = CapitalizeName(order.ContactFirstName);
                var addressDisplay = $"{order.ServiceAddress}{(!string.IsNullOrEmpty(order.AptSuite) ? $", {order.AptSuite}" : "")}";
                var serviceTimeStr = order.ServiceTime.ToString();

                var extraNames = (order.OrderExtraServices ?? new List<OrderExtraService>())
                    .Select(x => x.ExtraService?.Name ?? "")
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Select(n => n.ToLowerInvariant())
                    .ToList();

                var hasCleaningSupplies = extraNames.Any(n => n.Contains("cleaning supplies"));
                var isDeepCleaning = extraNames.Any(n => n.Contains("super deep cleaning")) ||
                                    extraNames.Any(n => n.Contains("deep cleaning") && !n.Contains("super"));
                var isCustomServiceType = order.ServiceType.IsCustom;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        // Skip email if Apple hidden mail or no email
                        var isAppleHiddenMail = !string.IsNullOrEmpty(contactEmail) &&
                            contactEmail.EndsWith("@privaterelay.appleid.com", StringComparison.OrdinalIgnoreCase);

                        if (!isAppleHiddenMail && !string.IsNullOrWhiteSpace(contactEmail))
                        {
                            await _emailService.SendCustomerBookingConfirmationAsync(
                                contactEmail,
                                customerName,
                                order.ServiceDate,
                                serviceTimeStr,
                                order.GetDisplayServiceTypeName(),
                                addressDisplay,
                                order.Id,
                                hasCleaningSupplies,
                                isDeepCleaning,
                                isCustomServiceType,
                                order.FloorTypes,
                                order.FloorTypeOther
                            );
                            _logger.LogInformation($"Booking confirmation email sent to {contactEmail} for order {order.Id}");
                        }
                        else if (isAppleHiddenMail)
                        {
                            _logger.LogInformation($"Skipping booking confirmation email for order {order.Id} - Apple hidden mail");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Failed to send booking confirmation email for order {order.Id}");
                    }
                });

                // Send booking confirmation SMS if phone exists
                if (!string.IsNullOrWhiteSpace(contactPhone))
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _smsService.SendBookingConfirmationSmsAsync(
                                contactPhone,
                                customerName,
                                order.ServiceDate,
                                serviceTimeStr,
                                hasCleaningSupplies,
                                isDeepCleaning,
                                isCustomServiceType
                            );
                            _logger.LogInformation($"Booking confirmation SMS sent to {contactPhone} for order {order.Id}");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"Failed to send booking confirmation SMS for order {order.Id}");
                        }
                    });
                }
                else
                {
                    _logger.LogInformation($"Skipping booking confirmation SMS for order {order.Id} - no phone number");
                }

                // Send booking notification to company email with photos
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _emailService.SendCompanyBookingNotificationAsync(
                            order.ContactFirstName,
                            order.ContactLastName,
                            order.ContactEmail,
                            order.ContactPhone,
                            order.ServiceDate,
                            order.ServiceTime.ToString(),
                            order.GetDisplayServiceTypeName(),
                            order.ServiceAddress,
                            order.AptSuite,
                            order.City,
                            order.State,
                            order.ZipCode,
                            order.Id,
                            order.ServiceType.IsCustom,
                            order.SpecialInstructions,
                            bookingDataDto?.UploadedPhotos
                        );
                        _logger.LogInformation($"Booking notification with photos sent to company email for order {order.Id}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Failed to send company notification email for order {order.Id}");
                    }
                });

                // Persist booking-uploaded photos to the per-user cleaning photo library
                // (same resize/webp pipeline as the admin upload), then prune so the user
                // keeps only their two most recent cleanings on disk.
                await PersistBookingPhotosAsync(userId, order.Id, bookingDataDto?.UploadedPhotos);

                // Clean up booking data after successful payment
                _bookingDataService.RemoveBookingData(sessionId);

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
                var wasFirstTimeOrder = user.FirstTimeOrder;
                if (user.FirstTimeOrder)
                {
                    user.FirstTimeOrder = false;
                    user.UpdatedAt = DateTime.UtcNow;
                }

                await _context.SaveChangesAsync();

                // Bubble Rewards: safety net — welcome bonus is granted at registration; this covers legacy accounts created before that
                if (wasFirstTimeOrder && userId > 0)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var bubbleService = HttpContext.RequestServices.GetService<IBubblePointsService>();
                            if (bubbleService != null)
                                await bubbleService.GrantWelcomeBonus(userId);
                        }
                        catch (Exception rewardsEx)
                        {
                            _logger.LogError(rewardsEx, $"[BubbleRewards] GrantWelcomeBonus failed for user {userId}");
                        }
                    });
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

        private async Task<Order> CreateOrderFromBookingData(CreateBookingDto dto, int userId)
        {
            // Create + persist the order through the shared creation service: pricing via
            // the shared calculator, loyalty stacking for the order owner, special-offer
            // consumption and gift-card application — all in one transaction.
            var order = await _bookingCreationService.CreateOrderAsync(dto, userId);

            // Notify admins about new order
            await NotifyAdminsNewOrder(order.Id);

            // Reload order with related data
            return await _context.Orders
                .Include(o => o.OrderServices)
                .Include(o => o.ServiceType)
                .FirstOrDefaultAsync(o => o.Id == order.Id);
        }

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

        [HttpGet("available-times")]
        public ActionResult<List<string>> GetAvailableTimeSlots(DateTime date, int serviceTypeId)
        {
            // Time slots from 8:00 AM to 6:00 PM (30-minute intervals) for all days.
            // Weekend rule: earliest start is 9:30 AM on Saturdays and Sundays.
            var timeSlots = new List<string>
                {
                    "08:00", "08:30", "09:00", "09:30", "10:00", "10:30", "11:00", "11:30",
                    "12:00", "12:30", "13:00", "13:30", "14:00", "14:30", "15:00", "15:30",
                    "16:00", "16:30", "17:00", "17:30", "18:00"
                };

            var isWeekend = date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday;
            var minStartTime = isWeekend ? "09:30" : "08:00";
            timeSlots = timeSlots.Where(t => String.Compare(t, minStartTime, StringComparison.Ordinal) >= 0).ToList();

            return Ok(timeSlots);
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