using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using DreamCleaningBackend.Services;
using Stripe;

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

        public BookingController(ApplicationDbContext context,
            IConfiguration configuration, 
            ISubscriptionService subscriptionService,
            IGiftCardService giftCardService, 
            IEmailService emailService, 
            IBookingDataService bookingDataService,
            IStripeService stripeService)
        {
            _context = context;
            _configuration = configuration;
            _subscriptionService = subscriptionService;
            _giftCardService = giftCardService;
            _emailService = emailService;
            _bookingDataService = bookingDataService;
            _stripeService = stripeService;
        }

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
            // This would contain the same calculation logic as the frontend
            // For now, we'll return a simple calculation
            var calculation = new BookingCalculationDto
            {
                SubTotal = 150,
                Tax = 13.20m,
                DiscountAmount = 0,
                Tips = dto.Tips,
                CompanyDevelopmentTips = dto.CompanyDevelopmentTips,
                Total = 163.20m + dto.Tips + dto.CompanyDevelopmentTips,
                TotalDuration = 120
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

                // LOG: Initial values
                Console.WriteLine($"=== BOOKING CREATION START ===");
                Console.WriteLine($"TotalDuration from Frontend: {dto.TotalDuration}");
                Console.WriteLine($"ServiceTypeId: {dto.ServiceTypeId}");

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

                // Determine gift card usage
                string? giftCardCode = dto.GiftCardCode; // Use the actual GiftCardCode field
                decimal giftCardAmountUsed = dto.GiftCardAmountToUse; // Use the amount from DTO
                string? promoCode = dto.PromoCode;

                // Check if the promo code is actually a gift card
                if (!string.IsNullOrEmpty(dto.PromoCode) &&
                    string.IsNullOrEmpty(dto.GiftCardCode) &&
                    System.Text.RegularExpressions.Regex.IsMatch(dto.PromoCode, @"^[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}$"))
                {
                    // This is a gift card
                    giftCardCode = dto.PromoCode;
                    promoCode = null;
                }

                // Create order
                var order = new Order
                {
                    UserId = userId,
                    ServiceTypeId = dto.ServiceTypeId,
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
                    ContactFirstName = dto.ContactFirstName,
                    ContactLastName = dto.ContactLastName,
                    ContactEmail = dto.ContactEmail,
                    ContactPhone = dto.ContactPhone,
                    PromoCode = promoCode,
                    GiftCardCode = giftCardCode,
                    GiftCardAmountUsed = 0,
                    Tips = dto.Tips,
                    CompanyDevelopmentTips = dto.CompanyDevelopmentTips,
                    Status = "Pending",
                    OrderDate = DateTime.UtcNow,
                    SubscriptionId = dto.SubscriptionId,
                    OrderServices = new List<Models.OrderService>(),
                    OrderExtraServices = new List<OrderExtraService>(),
                    CreatedAt = DateTime.UtcNow,
                    MaidsCount = dto.MaidsCount,
                    // ADD THESE THREE LINES:
                    SubTotal = dto.SubTotal,
                    Tax = dto.Tax,
                    Total = dto.Total,
                    DiscountAmount = dto.DiscountAmount,
                    SubscriptionDiscountAmount = dto.SubscriptionDiscountAmount
                };

                // Calculate subtotal
                decimal subTotal = 0;
                decimal totalDuration = 0;
                decimal priceMultiplier = 1;
                decimal deepCleaningFee = 0;

                if (dto.IsCustomPricing)
                {
                    Console.WriteLine($"=== CUSTOM PRICING ORDER ===");
                    Console.WriteLine($"Custom Amount: {dto.CustomAmount}");
                    Console.WriteLine($"Custom Cleaners: {dto.CustomCleaners}");
                    Console.WriteLine($"Custom Duration: {dto.CustomDuration}");

                    // For custom pricing, use the custom values directly
                    subTotal = dto.CustomAmount ?? serviceType.BasePrice;
                    totalDuration = dto.CustomDuration ?? serviceType.TimeDuration;
                    order.MaidsCount = dto.CustomCleaners ?? 1;
                    order.TotalDuration = dto.CustomDuration ?? 90;

                    Console.WriteLine($"Custom pricing applied - SubTotal: {subTotal}, Duration: {totalDuration} minutes, Cleaners: {order.MaidsCount}");
                }
                else
                {
                    // Check for deep cleaning multipliers FIRST
                    foreach (var extraServiceDto in dto.ExtraServices)
                    {
                        var extraService = await _context.ExtraServices.FindAsync(extraServiceDto.ExtraServiceId);
                        if (extraService != null && (extraService.IsDeepCleaning || extraService.IsSuperDeepCleaning))
                        {
                            if (extraService.IsSuperDeepCleaning)
                            {
                                priceMultiplier = extraService.PriceMultiplier;
                                deepCleaningFee = extraService.Price;
                                Console.WriteLine($"Super Deep Cleaning detected - Multiplier: {priceMultiplier}, Fee: {deepCleaningFee}");
                            }
                            else if (extraService.IsDeepCleaning)
                            {
                                priceMultiplier = extraService.PriceMultiplier;
                                deepCleaningFee = extraService.Price;
                                Console.WriteLine($"Deep Cleaning detected - Multiplier: {priceMultiplier}, Fee: {deepCleaningFee}");
                            }
                        }
                    }

                    // Add base price
                    subTotal += serviceType.BasePrice * priceMultiplier;
                    Console.WriteLine($"Base Price: {serviceType.BasePrice} x {priceMultiplier} = {serviceType.BasePrice * priceMultiplier}");
                    Console.WriteLine($"Running SubTotal after base price: ${subTotal}");

                    totalDuration += serviceType.TimeDuration;
                    Console.WriteLine($"Service Type Base Duration: {serviceType.TimeDuration} minutes");

                    // Add services
                    Console.WriteLine($"\n--- SERVICES CALCULATION ---");
                    foreach (var serviceDto in dto.Services)
                    {
                        var service = await _context.Services.FindAsync(serviceDto.ServiceId);
                        if (service != null)
                        {
                            Console.WriteLine($"\nService: {service.Name} (ID: {service.Id})");
                            Console.WriteLine($"  ServiceRelationType: {service.ServiceRelationType}");
                            Console.WriteLine($"  Quantity: {serviceDto.Quantity}");
                            Console.WriteLine($"  TimeDuration: {service.TimeDuration}");

                            decimal serviceCost = 0;
                            decimal serviceDuration = 0;
                            bool shouldAddToOrder = true;

                            // Special handling for cleaner-hours relationship
                            if (service.ServiceRelationType == "cleaner")
                            {
                                // Find the hours service in the same order
                                var hoursServiceDto = dto.Services.FirstOrDefault(s =>
                                {
                                    var svc = _context.Services.Find(s.ServiceId);
                                    return svc?.ServiceRelationType == "hours" && svc.ServiceTypeId == service.ServiceTypeId;
                                });

                                if (hoursServiceDto != null)
                                {
                                    var hours = hoursServiceDto.Quantity;
                                    var cleaners = serviceDto.Quantity;
                                    var costPerCleanerPerHour = service.Cost * priceMultiplier;
                                    serviceCost = costPerCleanerPerHour * cleaners * hours;
                                    serviceDuration = hours * 60; // Convert to minutes

                                    Console.WriteLine($"  Cleaner-Hours calculation:");
                                    Console.WriteLine($"    Hours: {hours}");
                                    Console.WriteLine($"    Cleaners: {cleaners}");
                                    Console.WriteLine($"    Cost: {costPerCleanerPerHour} x {cleaners} x {hours} = {serviceCost}");
                                    Console.WriteLine($"    Duration: {hours} hours x 60 = {serviceDuration} minutes");
                                }
                                else
                                {
                                    serviceCost = service.Cost * serviceDto.Quantity * priceMultiplier;
                                    serviceDuration = service.TimeDuration * serviceDto.Quantity;
                                    Console.WriteLine($"  No hours service found, using default calculation");
                                    Console.WriteLine($"    Duration: {service.TimeDuration} x {serviceDto.Quantity} = {serviceDuration} minutes");
                                }
                            }
                            else if (service.ServiceKey == "bedrooms" && serviceDto.Quantity == 0)
                            {
                                // Studio apartment
                                serviceCost = 10 * priceMultiplier;
                                serviceDuration = 20;
                                Console.WriteLine($"  Studio apartment - Fixed duration: 20 minutes");
                            }
                            else if (service.ServiceRelationType == "hours")
                            {
                                // Hours service - don't add to order or duration
                                shouldAddToOrder = false;
                                Console.WriteLine($"  Hours service - skipping (already counted in cleaner calculation)");
                            }
                            else
                            {
                                serviceCost = service.Cost * serviceDto.Quantity * priceMultiplier;
                                serviceDuration = service.TimeDuration * serviceDto.Quantity;
                                Console.WriteLine($"  Regular service calculation:");
                                Console.WriteLine($"    Duration: {service.TimeDuration} x {serviceDto.Quantity} = {serviceDuration} minutes");
                            }

                            // Add to order if it should be added
                            if (shouldAddToOrder)
                            {
                                var orderService = new Models.OrderService
                                {
                                    ServiceId = serviceDto.ServiceId,
                                    Quantity = serviceDto.Quantity,
                                    Cost = serviceCost,
                                    Duration = serviceDuration,
                                    PriceMultiplier = priceMultiplier,
                                    CreatedAt = DateTime.UtcNow
                                };
                                order.OrderServices.Add(orderService);
                                subTotal += serviceCost;
                                totalDuration += serviceDuration;
                                Console.WriteLine($"  Added service cost: ${serviceCost}");
                                Console.WriteLine($"  Running SubTotal: ${subTotal}");

                                Console.WriteLine($"  Added to total - Running total duration: {totalDuration} minutes");
                            }
                        }
                    }

                    // Add extra services
                    Console.WriteLine($"\n--- EXTRA SERVICES CALCULATION ---");
                    foreach (var extraServiceDto in dto.ExtraServices)
                    {
                        var extraService = await _context.ExtraServices.FindAsync(extraServiceDto.ExtraServiceId);
                        if (extraService != null)
                        {
                            Console.WriteLine($"\nExtra Service: {extraService.Name} (ID: {extraService.Id})");
                            Console.WriteLine($"  Quantity: {extraServiceDto.Quantity}");
                            Console.WriteLine($"  Hours: {extraServiceDto.Hours}");
                            Console.WriteLine($"  Duration per unit: {extraService.Duration}");
                            Console.WriteLine($"  HasHours: {extraService.HasHours}");
                            Console.WriteLine($"  HasQuantity: {extraService.HasQuantity}");

                            decimal cost = 0;
                            decimal duration = 0;

                            // For deep cleaning services, store their actual price
                            if (extraService.IsDeepCleaning || extraService.IsSuperDeepCleaning)
                            {
                                cost = extraService.Price; // Store the actual deep cleaning fee
                                duration = extraService.Duration;
                                Console.WriteLine($"  Deep cleaning service - Duration: {duration} minutes");
                            }
                            else
                            {
                                // Regular extra services - apply multiplier EXCEPT for Same Day Service
                                var currentMultiplier = extraService.IsSameDayService ? 1 : priceMultiplier;

                                if (extraService.HasHours)
                                {
                                    cost = extraService.Price * extraServiceDto.Hours * currentMultiplier;
                                    duration = (int)(extraService.Duration * extraServiceDto.Hours);
                                    Console.WriteLine($"  HasHours calculation - Duration: {extraService.Duration} x {extraServiceDto.Hours} = {duration} minutes");
                                }
                                else if (extraService.HasQuantity)
                                {
                                    if (extraService.Name == "Extra Cleaners")
                                    {
                                        // Extra Cleaners: fixed cost per cleaner (not based on hours)
                                        decimal baseCostPerCleaner = 40m; // Base cost per extra cleaner

                                        // Adjust cost based on cleaning type
                                        if (dto.ExtraServices.Any(es =>
                                        {
                                            var esService = _context.ExtraServices.Find(es.ExtraServiceId);
                                            return esService?.IsSuperDeepCleaning == true;
                                        }))
                                        {
                                            baseCostPerCleaner = 80m; // Super deep cleaning
                                        }
                                        else if (dto.ExtraServices.Any(es =>
                                        {
                                            var esService = _context.ExtraServices.Find(es.ExtraServiceId);
                                            return esService?.IsDeepCleaning == true;
                                        }))
                                        {
                                            baseCostPerCleaner = 60m; // Deep cleaning
                                        }

                                        cost = baseCostPerCleaner * extraServiceDto.Quantity;
                                        duration = 0; // Extra cleaners don't add duration - they reduce it

                                        Console.WriteLine($"  Extra Cleaners calculation:");
                                        Console.WriteLine($"    Quantity: {extraServiceDto.Quantity}");
                                        Console.WriteLine($"    Cost per cleaner: {baseCostPerCleaner}");
                                        Console.WriteLine($"    Total cost: {cost}");
                                    }
                                    else
                                    {
                                        // Regular quantity-based extra service
                                        cost = extraService.Price * extraServiceDto.Quantity * currentMultiplier;
                                        duration = extraService.Duration * extraServiceDto.Quantity;
                                        Console.WriteLine($"  HasQuantity calculation - Duration: {extraService.Duration} x {extraServiceDto.Quantity} = {duration} minutes");
                                    }
                                }
                                else
                                {
                                    cost = extraService.Price * currentMultiplier;
                                    duration = extraService.Duration;
                                    Console.WriteLine($"  Regular calculation - Duration: {duration} minutes");
                                }
                            }

                            var orderExtraService = new OrderExtraService
                            {
                                ExtraServiceId = extraServiceDto.ExtraServiceId,
                                Quantity = extraServiceDto.Quantity,
                                Hours = extraServiceDto.Hours,
                                Cost = cost,
                                Duration = duration,
                                CreatedAt = DateTime.UtcNow
                            };
                            order.OrderExtraServices.Add(orderExtraService);

                            if (!extraService.IsDeepCleaning && !extraService.IsSuperDeepCleaning)
                            {
                                subTotal += cost;
                                Console.WriteLine($"  Added extra service cost: ${cost}");
                                Console.WriteLine($"  Running SubTotal: ${subTotal}");
                            }
                            totalDuration += duration;

                            Console.WriteLine($"  Added to total - Running total duration: {totalDuration} minutes");
                        }
                    }

                    // Add deep cleaning fee after discount calculations
                    subTotal += deepCleaningFee;
                } // END OF ELSE BLOCK FOR REGULAR PRICING

                Console.WriteLine($"\n=== FINAL CALCULATIONS ===");
                Console.WriteLine($"Backend Calculated Total Duration: {totalDuration} minutes");
                Console.WriteLine($"Frontend Sent Total Duration: {dto.TotalDuration} minutes");
                Console.WriteLine($"Frontend Sent Maids Count: {dto.MaidsCount}");
                Console.WriteLine($"DIFFERENCE: {dto.TotalDuration - totalDuration} minutes");
                               
                // If there's a mismatch, use the frontend value but log it
                if (dto.TotalDuration != totalDuration)
                {
                    Console.WriteLine($"WARNING: Duration mismatch! Using frontend value: {dto.TotalDuration}");
                    // Uncomment the line below to use frontend duration instead of backend calculation
                    // totalDuration = dto.TotalDuration;
                }

                // If there's a significant mismatch, use the frontend value
                // The frontend has all the user selections and should be the source of truth
                if (Math.Abs(dto.TotalDuration - totalDuration) > 5) // Allow 5 minutes tolerance
                {
                    Console.WriteLine($"WARNING: Duration mismatch! Using frontend value: {dto.TotalDuration}");
                    totalDuration = dto.TotalDuration;
                }

                if (totalDuration < 60)
                {
                    Console.WriteLine($"WARNING: Backend calculated duration {totalDuration} is less than minimum 60 minutes. Setting to 60 minutes.");
                    totalDuration = 60;
                }

                //// Apply subscription discount
                var subscription = await _context.Subscriptions.FindAsync(dto.SubscriptionId);

                // Set MaidsCount from the frontend
                if (dto.IsCustomPricing)
                {
                    order.MaidsCount = dto.CustomCleaners ?? dto.MaidsCount;
                    Console.WriteLine($"Custom Pricing - Using CustomCleaners: {order.MaidsCount}");
                }
                else
                {
                    order.MaidsCount = dto.MaidsCount;
                }

                // If MaidsCount is 0 (not sent from frontend), calculate it
                if (order.MaidsCount == 0)
                {
                    // Check if cleaners are explicitly selected
                    var cleanerService = order.OrderServices.FirstOrDefault(os =>
                    {
                        var service = _context.Services.Find(os.ServiceId);
                        return service?.ServiceRelationType == "cleaner";
                    });

                    if (cleanerService != null)
                    {
                        // Use the cleaner count
                        order.MaidsCount = cleanerService.Quantity;
                    }
                    else
                    {
                        // Calculate based on duration (every 6 hours = 1 maid)
                        decimal totalHours = totalDuration / 60m;
                        order.MaidsCount = Math.Max(1, (int)Math.Ceiling(totalHours / 6m));
                    }
                }

                order.DiscountAmount = dto.DiscountAmount; // This is promo/first-time discount ONLY
                order.SubscriptionDiscountAmount = dto.SubscriptionDiscountAmount; // Add this line if property exists

                // Complete order calculations
                order.SubTotal = Math.Round(subTotal * 100) / 100;

                // Calculate discounted subtotal FIRST
                var discountedSubTotal = order.SubTotal - order.DiscountAmount - order.SubscriptionDiscountAmount;

                // Calculate tax on the DISCOUNTED amount (this is the fix)
                order.Tax = Math.Round(discountedSubTotal * 0.08875m * 100) / 100;

                // Calculate total
                var totalBeforeGiftCard = discountedSubTotal + order.Tax + order.Tips + order.CompanyDevelopmentTips;
                order.Total = Math.Round((totalBeforeGiftCard - giftCardAmountUsed) * 100) / 100;
                if (dto.IsCustomPricing)
                {
                    order.TotalDuration = dto.CustomDuration ?? totalDuration;
                    Console.WriteLine($"Custom Pricing - Using CustomDuration: {order.TotalDuration}");

                    if (order.TotalDuration < 60)
                    {
                        order.TotalDuration = 60;
                        Console.WriteLine($"Custom Pricing - Enforced minimum 60 minutes");
                    }
                }
                else
                {
                    order.TotalDuration = totalDuration;
                }

                Console.WriteLine($"Final values saved to DB:");
                Console.WriteLine($"- SubTotal: ${order.SubTotal}");
                Console.WriteLine($"- DiscountAmount (promo/first-time): ${order.DiscountAmount}");
                Console.WriteLine($"- SubscriptionDiscountAmount: ${order.SubscriptionDiscountAmount}");
                Console.WriteLine($"- Tax: ${order.Tax}");
                Console.WriteLine($"- Tips: ${order.Tips}");
                Console.WriteLine($"- CompanyDevelopmentTips: ${order.CompanyDevelopmentTips}");
                Console.WriteLine($"- GiftCardCode: {order.GiftCardCode}");
                Console.WriteLine($"- GiftCardAmountUsed: ${order.GiftCardAmountUsed}");
                Console.WriteLine($"- Total: ${order.Total}");
                Console.WriteLine($"- Total Duration: {order.TotalDuration} minutes");
                Console.WriteLine($"- Maids Count: {order.MaidsCount}");
                Console.WriteLine($"=== BOOKING CREATION END ===\n");

                // Use a transaction to ensure both order and gift card usage are saved together
                using (var transaction = await _context.Database.BeginTransactionAsync())
                {
                    try
                    {
                        // Add order to database
                        _context.Orders.Add(order);
                        await _context.SaveChangesAsync();

                        if (dto.UserSpecialOfferId.HasValue && dto.UserSpecialOfferId.Value > 0)
                        {
                            var userSpecialOffer = await _context.UserSpecialOffers
                                .FirstOrDefaultAsync(uso =>
                                    uso.Id == dto.UserSpecialOfferId.Value &&
                                    uso.UserId == userId &&
                                    !uso.IsUsed);

                            if (userSpecialOffer != null)
                            {
                                userSpecialOffer.IsUsed = true;
                                userSpecialOffer.UsedAt = DateTime.UtcNow;
                                userSpecialOffer.UsedOnOrderId = order.Id;

                                await _context.SaveChangesAsync();
                            }
                        }

                        // Store special offer details in the order
                        if (dto.UserSpecialOfferId.HasValue && dto.UserSpecialOfferId.Value > 0)
                        {
                            var specialOfferDetails = await _context.UserSpecialOffers
                                .Include(uso => uso.SpecialOffer)
                                .FirstOrDefaultAsync(uso => uso.Id == dto.UserSpecialOfferId.Value);

                            if (specialOfferDetails != null)
                            {
                                // Store the special offer name in PromoCode field with a prefix
                                // This way we can identify it's a special offer, not a regular promo code
                                order.PromoCode = $"SPECIAL_OFFER:{specialOfferDetails.SpecialOffer.Name}";
                                await _context.SaveChangesAsync();
                            }
                        }

                        // Apply gift card if one was provided
                        if (!string.IsNullOrEmpty(giftCardCode) && giftCardAmountUsed > 0)
                        {
                            Console.WriteLine($"=== APPLYING GIFT CARD ===");
                            Console.WriteLine($"Code: {giftCardCode}");
                            Console.WriteLine($"Amount to use: {giftCardAmountUsed}");
                            Console.WriteLine($"OrderId: {order.Id}");
                            Console.WriteLine($"UserId: {userId}");

                            // Apply the gift card and get the actual amount used
                            var actualAmountUsed = await _giftCardService.ApplyGiftCardToOrder(
                                giftCardCode,
                                totalBeforeGiftCard,
                                order.Id,
                                userId
                            );

                            // Update the order if the actual amount differs
                            if (actualAmountUsed != giftCardAmountUsed)
                            {
                                order.GiftCardAmountUsed = actualAmountUsed;
                                order.Total = totalBeforeGiftCard - actualAmountUsed;
                                await _context.SaveChangesAsync();
                            }
                            else
                            {
                                // Even if amounts match, update the order to set the actual amount used
                                order.GiftCardAmountUsed = actualAmountUsed;
                                await _context.SaveChangesAsync();
                            }

                            Console.WriteLine($"Gift card applied successfully! Actual amount used: {actualAmountUsed}");
                        }

                        // Commit the transaction
                        await transaction.CommitAsync();
                        Console.WriteLine("Transaction committed successfully!");
                    }
                    catch (Exception ex)
                    {
                        // Rollback on any error
                        await transaction.RollbackAsync();
                        Console.WriteLine($"Transaction rolled back due to error: {ex.Message}");
                        throw new InvalidOperationException($"Failed to create order: {ex.Message}");
                    }
                }

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

                Console.WriteLine($"=== ADMIN BOOKING CREATION START ===");
                Console.WriteLine($"Admin User ID: {adminUserId}");
                Console.WriteLine($"Target User ID: {dto.TargetUserId}");
                Console.WriteLine($"TotalDuration from Frontend: {dto.BookingData.TotalDuration}");

                // Get service type
                var serviceType = await _context.ServiceTypes
                    .Include(st => st.Services)
                    .FirstOrDefaultAsync(st => st.Id == dto.BookingData.ServiceTypeId);

                if (serviceType == null)
                    return BadRequest(new { message = "Invalid service type" });

                // Determine gift card usage
                string? giftCardCode = dto.BookingData.GiftCardCode;
                decimal giftCardAmountUsed = dto.BookingData.GiftCardAmountToUse;
                string? promoCode = dto.BookingData.PromoCode;

                if (!string.IsNullOrEmpty(dto.BookingData.PromoCode) &&
                    string.IsNullOrEmpty(dto.BookingData.GiftCardCode) &&
                    System.Text.RegularExpressions.Regex.IsMatch(dto.BookingData.PromoCode, @"^[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}$"))
                {
                    giftCardCode = dto.BookingData.PromoCode;
                    promoCode = null;
                }

                // Calculate subtotal and other values (reuse logic from CreateBooking)
                decimal subTotal = 0;
                decimal totalDuration = 0;
                decimal priceMultiplier = 1;
                decimal deepCleaningFee = 0;

                if (dto.BookingData.IsCustomPricing)
                {
                    subTotal = dto.BookingData.CustomAmount ?? serviceType.BasePrice;
                    totalDuration = dto.BookingData.CustomDuration ?? serviceType.TimeDuration;
                }
                else
                {
                    // Check for deep cleaning multipliers
                    foreach (var extraServiceDto in dto.BookingData.ExtraServices)
                    {
                        var extraService = await _context.ExtraServices.FindAsync(extraServiceDto.ExtraServiceId);
                        if (extraService != null && (extraService.IsDeepCleaning || extraService.IsSuperDeepCleaning))
                        {
                            if (extraService.IsSuperDeepCleaning)
                            {
                                priceMultiplier = extraService.PriceMultiplier;
                                deepCleaningFee = extraService.Price;
                            }
                            else if (extraService.IsDeepCleaning)
                            {
                                priceMultiplier = extraService.PriceMultiplier;
                                deepCleaningFee = extraService.Price;
                            }
                        }
                    }

                    subTotal += serviceType.BasePrice * priceMultiplier;
                    totalDuration += serviceType.TimeDuration;

                    // Add services
                    foreach (var serviceDto in dto.BookingData.Services)
                    {
                        var service = await _context.Services.FindAsync(serviceDto.ServiceId);
                        if (service != null)
                        {
                            decimal serviceCost = 0;
                            decimal serviceDuration = 0;
                            bool shouldAddToOrder = true;

                            if (service.ServiceRelationType == "cleaner")
                            {
                                var hoursServiceDto = dto.BookingData.Services.FirstOrDefault(s =>
                                {
                                    var svc = _context.Services.Find(s.ServiceId);
                                    return svc?.ServiceRelationType == "hours" && svc.ServiceTypeId == service.ServiceTypeId;
                                });

                                if (hoursServiceDto != null)
                                {
                                    var hours = hoursServiceDto.Quantity;
                                    var cleaners = serviceDto.Quantity;
                                    var costPerCleanerPerHour = service.Cost * priceMultiplier;
                                    serviceCost = costPerCleanerPerHour * cleaners * hours;
                                    serviceDuration = hours * 60;
                                }
                                else
                                {
                                    serviceCost = service.Cost * serviceDto.Quantity * priceMultiplier;
                                    serviceDuration = service.TimeDuration * serviceDto.Quantity;
                                }
                            }
                            else if (service.ServiceKey == "bedrooms" && serviceDto.Quantity == 0)
                            {
                                serviceCost = 10 * priceMultiplier;
                                serviceDuration = 20;
                            }
                            else if (service.ServiceRelationType == "hours")
                            {
                                shouldAddToOrder = false;
                            }
                            else
                            {
                                serviceCost = service.Cost * serviceDto.Quantity * priceMultiplier;
                                serviceDuration = service.TimeDuration * serviceDto.Quantity;
                            }

                            if (shouldAddToOrder)
                            {
                                subTotal += serviceCost;
                                totalDuration += serviceDuration;
                            }
                        }
                    }

                    // Add extra services
                    foreach (var extraServiceDto in dto.BookingData.ExtraServices)
                    {
                        var extraService = await _context.ExtraServices.FindAsync(extraServiceDto.ExtraServiceId);
                        if (extraService != null)
                        {
                            decimal cost = 0;
                            decimal duration = 0;

                            if (extraService.IsDeepCleaning || extraService.IsSuperDeepCleaning)
                            {
                                cost = extraService.Price;
                                duration = extraService.Duration;
                            }
                            else
                            {
                                var currentMultiplier = extraService.IsSameDayService ? 1 : priceMultiplier;

                                if (extraService.HasHours)
                                {
                                    cost = extraService.Price * extraServiceDto.Hours * currentMultiplier;
                                    duration = (int)(extraService.Duration * extraServiceDto.Hours);
                                }
                                else if (extraService.HasQuantity)
                                {
                                    if (extraService.Name == "Extra Cleaners")
                                    {
                                        decimal baseCostPerCleaner = 40m;
                                        if (dto.BookingData.ExtraServices.Any(es =>
                                        {
                                            var esService = _context.ExtraServices.Find(es.ExtraServiceId);
                                            return esService?.IsSuperDeepCleaning == true;
                                        }))
                                        {
                                            baseCostPerCleaner = 80m;
                                        }
                                        else if (dto.BookingData.ExtraServices.Any(es =>
                                        {
                                            var esService = _context.ExtraServices.Find(es.ExtraServiceId);
                                            return esService?.IsDeepCleaning == true;
                                        }))
                                        {
                                            baseCostPerCleaner = 60m;
                                        }
                                        cost = baseCostPerCleaner * extraServiceDto.Quantity;
                                        duration = 0;
                                    }
                                    else
                                    {
                                        cost = extraService.Price * extraServiceDto.Quantity * currentMultiplier;
                                        duration = extraService.Duration * extraServiceDto.Quantity;
                                    }
                                }
                                else
                                {
                                    cost = extraService.Price * currentMultiplier;
                                    duration = extraService.Duration;
                                }
                            }

                            if (!extraService.IsDeepCleaning && !extraService.IsSuperDeepCleaning)
                            {
                                subTotal += cost;
                            }
                            totalDuration += duration;
                        }
                    }

                    subTotal += deepCleaningFee;
                }

                if (Math.Abs(dto.BookingData.TotalDuration - totalDuration) > 5)
                {
                    totalDuration = dto.BookingData.TotalDuration;
                }

                if (totalDuration < 60)
                {
                    totalDuration = 60;
                }

                // Create order for target user (unpaid)
                var order = new Order
                {
                    UserId = dto.TargetUserId,
                    ServiceTypeId = dto.BookingData.ServiceTypeId,
                    ApartmentId = dto.BookingData.ApartmentId,
                    ApartmentName = dto.BookingData.ApartmentName,
                    ServiceAddress = dto.BookingData.ServiceAddress,
                    AptSuite = dto.BookingData.AptSuite,
                    City = dto.BookingData.City,
                    State = dto.BookingData.State,
                    ZipCode = dto.BookingData.ZipCode,
                    ServiceDate = dto.BookingData.ServiceDate,
                    ServiceTime = TimeSpan.Parse(dto.BookingData.ServiceTime),
                    EntryMethod = dto.BookingData.EntryMethod,
                    SpecialInstructions = dto.BookingData.SpecialInstructions,
                    ContactFirstName = dto.BookingData.ContactFirstName,
                    ContactLastName = dto.BookingData.ContactLastName,
                    ContactEmail = dto.BookingData.ContactEmail,
                    ContactPhone = dto.BookingData.ContactPhone,
                    PromoCode = promoCode,
                    GiftCardCode = giftCardCode,
                    GiftCardAmountUsed = 0,
                    Tips = dto.BookingData.Tips,
                    CompanyDevelopmentTips = dto.BookingData.CompanyDevelopmentTips,
                    Status = "Pending",
                    OrderDate = DateTime.UtcNow,
                    SubscriptionId = dto.BookingData.SubscriptionId,
                    OrderServices = new List<Models.OrderService>(),
                    OrderExtraServices = new List<OrderExtraService>(),
                    CreatedAt = DateTime.UtcNow,
                    MaidsCount = dto.BookingData.MaidsCount,
                    SubTotal = dto.BookingData.SubTotal,
                    Tax = dto.BookingData.Tax,
                    Total = dto.BookingData.Total,
                    DiscountAmount = dto.BookingData.DiscountAmount,
                    SubscriptionDiscountAmount = dto.BookingData.SubscriptionDiscountAmount,
                    IsPaid = false, // Mark as unpaid
                    TotalDuration = dto.BookingData.IsCustomPricing ? (dto.BookingData.CustomDuration ?? totalDuration) : totalDuration
                };

                // Add order services
                foreach (var serviceDto in dto.BookingData.Services)
                {
                    var service = await _context.Services.FindAsync(serviceDto.ServiceId);
                    if (service != null)
                    {
                        decimal serviceCost = 0;
                        decimal serviceDuration = 0;
                        bool shouldAddToOrder = true;

                        if (service.ServiceRelationType == "cleaner")
                        {
                            var hoursServiceDto = dto.BookingData.Services.FirstOrDefault(s =>
                            {
                                var svc = _context.Services.Find(s.ServiceId);
                                return svc?.ServiceRelationType == "hours" && svc.ServiceTypeId == service.ServiceTypeId;
                            });

                            if (hoursServiceDto != null)
                            {
                                var hours = hoursServiceDto.Quantity;
                                var cleaners = serviceDto.Quantity;
                                var costPerCleanerPerHour = service.Cost * priceMultiplier;
                                serviceCost = costPerCleanerPerHour * cleaners * hours;
                                serviceDuration = hours * 60;
                            }
                            else
                            {
                                serviceCost = service.Cost * serviceDto.Quantity * priceMultiplier;
                                serviceDuration = service.TimeDuration * serviceDto.Quantity;
                            }
                        }
                        else if (service.ServiceKey == "bedrooms" && serviceDto.Quantity == 0)
                        {
                            serviceCost = 10 * priceMultiplier;
                            serviceDuration = 20;
                        }
                        else if (service.ServiceRelationType == "hours")
                        {
                            shouldAddToOrder = false;
                        }
                        else
                        {
                            serviceCost = service.Cost * serviceDto.Quantity * priceMultiplier;
                            serviceDuration = service.TimeDuration * serviceDto.Quantity;
                        }

                        if (shouldAddToOrder)
                        {
                            order.OrderServices.Add(new Models.OrderService
                            {
                                ServiceId = serviceDto.ServiceId,
                                Quantity = serviceDto.Quantity,
                                Cost = serviceCost,
                                Duration = serviceDuration,
                                PriceMultiplier = priceMultiplier,
                                CreatedAt = DateTime.UtcNow
                            });
                        }
                    }
                }

                // Add order extra services
                foreach (var extraServiceDto in dto.BookingData.ExtraServices)
                {
                    var extraService = await _context.ExtraServices.FindAsync(extraServiceDto.ExtraServiceId);
                    if (extraService != null)
                    {
                        decimal cost = 0;
                        decimal duration = 0;
                        var currentMultiplier = extraService.IsSameDayService ? 1 : priceMultiplier;

                        if (extraService.IsDeepCleaning || extraService.IsSuperDeepCleaning)
                        {
                            cost = extraService.Price;
                            duration = extraService.Duration;
                        }
                        else if (extraService.HasHours)
                        {
                            cost = extraService.Price * extraServiceDto.Hours * currentMultiplier;
                            duration = (int)(extraService.Duration * extraServiceDto.Hours);
                        }
                        else if (extraService.HasQuantity)
                        {
                            if (extraService.Name == "Extra Cleaners")
                            {
                                decimal baseCostPerCleaner = 40m;
                                if (dto.BookingData.ExtraServices.Any(es =>
                                {
                                    var esService = _context.ExtraServices.Find(es.ExtraServiceId);
                                    return esService?.IsSuperDeepCleaning == true;
                                }))
                                {
                                    baseCostPerCleaner = 80m;
                                }
                                else if (dto.BookingData.ExtraServices.Any(es =>
                                {
                                    var esService = _context.ExtraServices.Find(es.ExtraServiceId);
                                    return esService?.IsDeepCleaning == true;
                                }))
                                {
                                    baseCostPerCleaner = 60m;
                                }
                                cost = baseCostPerCleaner * extraServiceDto.Quantity;
                                duration = 0;
                            }
                            else
                            {
                                cost = extraService.Price * extraServiceDto.Quantity * currentMultiplier;
                                duration = extraService.Duration * extraServiceDto.Quantity;
                            }
                        }
                        else
                        {
                            cost = extraService.Price * currentMultiplier;
                            duration = extraService.Duration;
                        }

                        order.OrderExtraServices.Add(new OrderExtraService
                        {
                            ExtraServiceId = extraServiceDto.ExtraServiceId,
                            Quantity = extraServiceDto.Quantity,
                            Hours = extraServiceDto.Hours,
                            Cost = cost,
                            Duration = duration,
                            CreatedAt = DateTime.UtcNow
                        });
                    }
                }

                // Apply subscription discount
                var subscription = await _context.Subscriptions.FindAsync(dto.BookingData.SubscriptionId);

                // Use transaction for order creation
                using (var transaction = await _context.Database.BeginTransactionAsync())
                {
                    try
                    {
                        _context.Orders.Add(order);
                        await _context.SaveChangesAsync();

                        // Handle special offers
                        if (dto.BookingData.UserSpecialOfferId.HasValue && dto.BookingData.UserSpecialOfferId.Value > 0)
                        {
                            var userSpecialOffer = await _context.UserSpecialOffers
                                .FirstOrDefaultAsync(uso =>
                                    uso.Id == dto.BookingData.UserSpecialOfferId.Value &&
                                    uso.UserId == dto.TargetUserId &&
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
                                .FirstOrDefaultAsync(uso => uso.Id == dto.BookingData.UserSpecialOfferId.Value);

                            if (specialOfferDetails != null)
                            {
                                order.PromoCode = $"SPECIAL_OFFER:{specialOfferDetails.SpecialOffer.Name}";
                                await _context.SaveChangesAsync();
                            }
                        }

                        // Apply gift card if provided
                        if (!string.IsNullOrEmpty(giftCardCode) && giftCardAmountUsed > 0)
                        {
                            var totalBeforeGiftCard = order.SubTotal - order.DiscountAmount - order.SubscriptionDiscountAmount + order.Tax + order.Tips + order.CompanyDevelopmentTips;
                            var actualAmountUsed = await _giftCardService.ApplyGiftCardToOrder(
                                giftCardCode,
                                totalBeforeGiftCard,
                                order.Id,
                                dto.TargetUserId
                            );

                            if (actualAmountUsed != giftCardAmountUsed)
                            {
                                order.GiftCardAmountUsed = actualAmountUsed;
                                order.Total = totalBeforeGiftCard - actualAmountUsed;
                                await _context.SaveChangesAsync();
                            }
                            else
                            {
                                order.GiftCardAmountUsed = actualAmountUsed;
                                await _context.SaveChangesAsync();
                            }
                        }

                        await transaction.CommitAsync();
                    }
                    catch (Exception ex)
                    {
                        await transaction.RollbackAsync();
                        throw new InvalidOperationException($"Failed to create order: {ex.Message}");
                    }
                }

                // Handle subscription activation/renewal (but don't activate until paid)
                // We'll handle this in confirm-payment

                // Store booking data including photos for later use when payment is confirmed
                var sessionId = $"booking_{order.Id}_{dto.TargetUserId}";
                _bookingDataService.StoreBookingData(sessionId, dto.BookingData);

                Console.WriteLine($"=== ADMIN BOOKING CREATION END ===");
                Console.WriteLine($"Order ID: {order.Id}");
                Console.WriteLine($"Status: {order.Status}");
                Console.WriteLine($"IsPaid: {order.IsPaid}");

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
                return BadRequest(new { message = "Failed to create payment intent: " + ex.Message });
            }
        }

        [HttpPost("confirm-payment/{orderId}")]
        [Authorize]
        public async Task<ActionResult> ConfirmPayment(int orderId, [FromBody] ConfirmPaymentDto dto)
        {
            try
            {
                var userId = GetUserId();
                if (userId == 0)
                    return Unauthorized();

                var order = await _context.Orders
                    .Include(o => o.OrderServices)
                    .Include(o => o.ServiceType)
                    .FirstOrDefaultAsync(o => o.Id == orderId && o.UserId == userId);

                if (order == null)
                    return NotFound(new { message = "Order not found" });

                if (order.IsPaid)
                    return BadRequest(new { message = "Order is already paid" });

                // Verify payment with Stripe
                var paymentIntent = await _stripeService.GetPaymentIntentAsync(dto.PaymentIntentId);

                if (paymentIntent.Status != "succeeded")
                {
                    return BadRequest(new { message = "Payment not completed" });
                }

                var sessionId = $"booking_{orderId}_{userId}";
                var bookingData = _bookingDataService.GetBookingData(sessionId);

                // Get the user to update phone number and check apartments
                var user = await _context.Users
                    .Include(u => u.Apartments)
                    .FirstOrDefaultAsync(u => u.Id == userId);

                if (user != null)
                {
                    // Update phone number if needed
                    if (string.IsNullOrEmpty(user.Phone) && !string.IsNullOrEmpty(order.ContactPhone))
                    {
                        user.Phone = order.ContactPhone;
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
                                Console.WriteLine($"Found existing apartment '{existingApartmentByAddress.Name}' with same address at {existingApartmentByAddress.Address}, linking to it without updating");
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

                                    Console.WriteLine($"Found existing apartment with name '{existingApartmentByName.Name}', updating all fields");
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

                                    Console.WriteLine($"Created new apartment '{order.ApartmentName}' at {order.ServiceAddress} for user {userId}");
                                }
                            }
                        }
                        else
                        {
                            Console.WriteLine($"User {userId} has reached the maximum of 10 apartments");
                        }
                    }
                }

                // Mark order as paid
                order.IsPaid = true;
                order.PaidAt = DateTime.UtcNow;
                order.Status = "Active";
                order.PaymentIntentId = dto.PaymentIntentId;

                // Send booking confirmation email to customer
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _emailService.SendCustomerBookingConfirmationAsync(
                            order.ContactEmail,
                            order.ContactFirstName,
                            order.ServiceDate,
                            order.ServiceTime.ToString(),
                            order.ServiceType.Name,
                            $"{order.ServiceAddress}{(!string.IsNullOrEmpty(order.AptSuite) ? $", {order.AptSuite}" : "")}",
                            order.Id
                        );
                        Console.WriteLine($"Booking confirmation email sent to {order.ContactEmail} for order {order.Id}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to send booking confirmation email: {ex.Message}");
                    }
                });

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
                            order.ServiceType.Name,
                            order.ServiceAddress,
                            order.AptSuite,
                            order.City,
                            order.State,
                            order.ZipCode,
                            order.Id,
                            bookingData?.UploadedPhotos
                        );
                        Console.WriteLine($"Booking notification with photos sent to company email for order {order.Id}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to send company notification email: {ex.Message}");
                    }
                });

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
                if (user.FirstTimeOrder)
                {
                    user.FirstTimeOrder = false;
                    user.UpdatedAt = DateTime.UtcNow;
                }

                await _context.SaveChangesAsync();

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
                Console.WriteLine($"Error in ConfirmPayment: {ex.Message}");
                return BadRequest(new { message = "Failed to confirm payment: " + ex.Message });
            }
        }

        private int GetUserId()
        {
            // Some parts of the codebase use custom "UserId" claim; fall back to NameIdentifier.
            var userIdClaim = User.FindFirst("UserId")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return int.TryParse(userIdClaim, out var userId) ? userId : 0;
        }

        [HttpGet("available-times")]
        public ActionResult<List<string>> GetAvailableTimeSlots(DateTime date, int serviceTypeId)
        {
            // Time slots from 8:00 AM to 6:00 PM (30-minute intervals) for all days
            var timeSlots = new List<string>
                {
                    "08:00", "08:30", "09:00", "09:30", "10:00", "10:30", "11:00", "11:30",
                    "12:00", "12:30", "13:00", "13:30", "14:00", "14:30", "15:00", "15:30",
                    "16:00", "16:30", "17:00", "17:30", "18:00"
                };

            return Ok(timeSlots);
        }
    }
}