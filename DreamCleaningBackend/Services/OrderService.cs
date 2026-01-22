using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using DreamCleaningBackend.Repositories.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DreamCleaningBackend.Services
{
    public class OrderService : IOrderService
    {
        private readonly IOrderRepository _orderRepository;
        private readonly ApplicationDbContext _context;
        private readonly IStripeService _stripeService;
        private readonly IEmailService _emailService;
        private readonly ILogger<OrderService> _logger;

        public OrderService(IOrderRepository orderRepository, ApplicationDbContext context, IStripeService stripeService, IEmailService emailService, ILogger<OrderService> logger)
        {
            _orderRepository = orderRepository;
            _context = context;
            _stripeService = stripeService;
            _emailService = emailService;
            _logger = logger;
        }

        public async Task<List<OrderListDto>> GetAllOrdersForAdmin()
        {
            // Get ALL orders from the database without filtering by userId
            var orders = await _context.Orders
                .Include(o => o.ServiceType)
                .Include(o => o.User)
                .OrderByDescending(o => o.OrderDate)
                .ToListAsync();

            await AutoCancelExpiredUnpaidOrdersIfNeeded(orders);

            return orders.Select(o => new OrderListDto
            {
                Id = o.Id,
                UserId = o.UserId,
                ContactEmail = o.ContactEmail,
                ContactFirstName = o.ContactFirstName,
                ContactLastName = o.ContactLastName,
                ServiceTypeName = o.ServiceType?.Name ?? "",
                IsCustomServiceType = o.ServiceType?.IsCustom ?? false,
                ServiceDate = o.ServiceDate,
                ServiceTime = o.ServiceTime,
                Status = o.Status,
                Total = o.Total,
                ServiceAddress = o.ServiceAddress + (string.IsNullOrEmpty(o.AptSuite) ? "" : $", {o.AptSuite}"),
                OrderDate = o.OrderDate,
                TotalDuration = o.TotalDuration,
                Tips = o.Tips,
                CompanyDevelopmentTips = o.CompanyDevelopmentTips,
                IsPaid = o.IsPaid,
                PaidAt = o.PaidAt
            }).ToList();
        }

        public async Task<List<OrderListDto>> GetUserOrders(int userId)
        {
            var orders = await _orderRepository.GetUserOrdersAsync(userId);

            await AutoCancelExpiredUnpaidOrdersIfNeeded(orders);

            return orders.Select(o => new OrderListDto
            {
                Id = o.Id,
                UserId = o.UserId,  
                ContactEmail = o.ContactEmail,  
                ContactFirstName = o.ContactFirstName,  
                ContactLastName = o.ContactLastName,  
                ServiceTypeName = o.ServiceType?.Name ?? "",
                IsCustomServiceType = o.ServiceType?.IsCustom ?? false,
                ServiceDate = o.ServiceDate,
                ServiceTime = o.ServiceTime,
                Status = o.Status,
                Total = o.Total,
                ServiceAddress = o.ServiceAddress + (string.IsNullOrEmpty(o.AptSuite) ? "" : $", {o.AptSuite}"),
                OrderDate = o.OrderDate,
                TotalDuration = o.TotalDuration,
                Tips = o.Tips,
                CompanyDevelopmentTips = o.CompanyDevelopmentTips,
                IsPaid = o.IsPaid,
                PaidAt = o.PaidAt
            }).ToList();
        }

        public async Task<OrderDto> GetOrderById(int orderId, int userId)
        {
            var order = await _orderRepository.GetByIdWithDetailsAsync(orderId);

            if (order == null || order.UserId != userId)
                throw new Exception("Order not found");

            await AutoCancelExpiredUnpaidOrderIfNeeded(order);

            return MapOrderToDto(order);
        }

        private static DateTime GetServiceDateTimeUtc(Order order)
        {
            // ServiceDate is stored as a DateTime (usually date portion); ServiceTime is stored separately.
            // Combine them to determine if the service datetime has passed.
            var combined = order.ServiceDate.Date.Add(order.ServiceTime);
            return combined.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(combined, DateTimeKind.Utc)
                : combined.ToUniversalTime();
        }

        private async Task AutoCancelExpiredUnpaidOrdersIfNeeded(IReadOnlyCollection<Order> orders)
        {
            if (orders == null || orders.Count == 0) return;

            var nowUtc = DateTime.UtcNow;
            var changed = false;

            foreach (var order in orders)
            {
                if (order == null) continue;
                if (order.IsPaid) continue;
                if (string.Equals(order.Status, "Cancelled", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(order.Status, "Done", StringComparison.OrdinalIgnoreCase)) continue;

                var serviceUtc = GetServiceDateTimeUtc(order);
                if (serviceUtc < nowUtc)
                {
                    order.Status = "Cancelled";
                    order.CancellationReason ??= "Order expired (unpaid)";
                    order.UpdatedAt = nowUtc;
                    changed = true;
                }
            }

            if (changed)
            {
                await _context.SaveChangesAsync();
            }
        }

        private async Task AutoCancelExpiredUnpaidOrderIfNeeded(Order order)
        {
            if (order == null) return;
            if (order.IsPaid) return;
            if (string.Equals(order.Status, "Cancelled", StringComparison.OrdinalIgnoreCase)) return;
            if (string.Equals(order.Status, "Done", StringComparison.OrdinalIgnoreCase)) return;

            var nowUtc = DateTime.UtcNow;
            var serviceUtc = GetServiceDateTimeUtc(order);
            if (serviceUtc < nowUtc)
            {
                order.Status = "Cancelled";
                order.CancellationReason ??= "Order expired (unpaid)";
                order.UpdatedAt = nowUtc;
                await _context.SaveChangesAsync();
            }
        }

        public async Task<OrderDto> UpdateOrder(int orderId, int userId, UpdateOrderDto updateOrderDto)
        {
            var order = await _orderRepository.GetByIdWithDetailsAsync(orderId);

            if (order == null || order.UserId != userId)
                throw new Exception("Order not found");

            if (order.Status == "Cancelled")
                throw new Exception("Cannot update a cancelled order");

            if (order.Status == "Done")
                throw new Exception("Cannot update a completed order");

            // 48-HOUR VALIDATION CHECK
            var hoursUntilService = (order.ServiceDate - DateTime.UtcNow).TotalHours;
            if (hoursUntilService <= 48)
            {
                throw new Exception("Orders can only be edited at least 48 hours before the scheduled service time");
            }

            // Define tolerance for floating-point comparisons
            const decimal tolerance = 0.01m; // 1 cent tolerance

            // TOTALDURATION LOGGING - START
            Console.WriteLine("\n========== TOTALDURATION TRACKING ==========");
            Console.WriteLine($"Frontend sent TotalDuration: {updateOrderDto.TotalDuration} minutes");
            Console.WriteLine($"Current DB TotalDuration: {order.TotalDuration} minutes");

            // Store the original values
            var originalTotal = order.Total;
            var originalGiftCardAmountUsed = order.GiftCardAmountUsed;

            // store original values before they're modified:
            var originalSubTotal = order.SubTotal;
            var originalTax = order.Tax;
            var originalTips = order.Tips;
            var originalCompanyDevelopmentTips = order.CompanyDevelopmentTips;

            // Log the original total
            Console.WriteLine($"Original total: ${originalTotal:F2}");

            // Calculate the additional amount before updating
            var additionalAmount = await CalculateAdditionalAmount(orderId, updateOrderDto);

            // Log the additional amount
            Console.WriteLine($"Additional amount: ${additionalAmount:F2}");

            // Check if the new total would be less than the original
            // Use the tolerance to handle floating-point precision issues
            if (additionalAmount < -tolerance)
            {
                var newTotal = originalTotal + additionalAmount;
                throw new Exception($"Cannot reduce order total. Original: ${originalTotal:F2}, New: ${newTotal:F2}, Difference: ${Math.Abs(additionalAmount):F2}");
            }

            // Update basic order information
            order.ServiceDate = updateOrderDto.ServiceDate;
            order.ServiceTime = TimeSpan.Parse(updateOrderDto.ServiceTime);
            order.EntryMethod = updateOrderDto.EntryMethod;
            order.SpecialInstructions = updateOrderDto.SpecialInstructions;
            order.ContactFirstName = updateOrderDto.ContactFirstName;
            order.ContactLastName = updateOrderDto.ContactLastName;
            order.ContactEmail = updateOrderDto.ContactEmail;
            order.ContactPhone = updateOrderDto.ContactPhone;
            order.ServiceAddress = updateOrderDto.ServiceAddress;
            order.AptSuite = updateOrderDto.AptSuite;
            order.City = updateOrderDto.City;
            order.State = updateOrderDto.State;
            order.ZipCode = updateOrderDto.ZipCode;
            order.Tips = updateOrderDto.Tips;
            order.CompanyDevelopmentTips = updateOrderDto.CompanyDevelopmentTips;
            order.UpdatedAt = DateTime.UtcNow;

            // Update user's phone number if they don't have one
            var user = await _context.Users.FindAsync(userId);
            if (user != null && string.IsNullOrEmpty(user.Phone) && !string.IsNullOrEmpty(updateOrderDto.ContactPhone))
            {
                user.Phone = updateOrderDto.ContactPhone;
                user.UpdatedAt = DateTime.UtcNow;
                Console.WriteLine($"Updated user's phone number to: {updateOrderDto.ContactPhone}");
            }

            // Calculate price multiplier from extra services FIRST
            decimal priceMultiplier = 1.0m;
            decimal deepCleaningFee = 0;

            foreach (var extraServiceDto in updateOrderDto.ExtraServices)
            {
                var extraService = await _context.ExtraServices.FindAsync(extraServiceDto.ExtraServiceId);
                if (extraService != null)
                {
                    if (extraService.IsSuperDeepCleaning)
                    {
                        priceMultiplier = extraService.PriceMultiplier;
                        deepCleaningFee = extraService.Price;
                        break; // Super deep cleaning takes precedence
                    }
                    else if (extraService.IsDeepCleaning && priceMultiplier == 1.0m)
                    {
                        priceMultiplier = extraService.PriceMultiplier;
                        deepCleaningFee = extraService.Price;
                    }
                }
            }

            // Update services
            _context.OrderServices.RemoveRange(order.OrderServices);

            decimal newSubTotal = order.ServiceType.BasePrice * priceMultiplier;
            decimal newTotalDuration = 0;

            Console.WriteLine($"\nStarting backend duration calculation...");

            if (order.ServiceType != null && order.ServiceType.TimeDuration > 0)
            {
                newTotalDuration += order.ServiceType.TimeDuration;
            }

            // Get the original hours for office cleaning
            var originalCleanerService = order.OrderServices.FirstOrDefault(os =>
            {
                var svc = _context.Services.Find(os.ServiceId);
                return svc?.ServiceRelationType == "cleaner";
            });
            decimal originalHours = originalCleanerService != null ? originalCleanerService.Duration / 60 : 0;

            foreach (var serviceDto in updateOrderDto.Services)
            {
                var service = await _context.Services.FindAsync(serviceDto.ServiceId);
                if (service != null)
                {
                    decimal serviceCost = 0;
                    decimal serviceDuration = 0;
                    bool shouldAddToOrder = true;

                    // Special handling for studio apartments (0 bedrooms)
                    if (service.ServiceKey == "bedrooms" && serviceDto.Quantity == 0)
                    {
                        Console.WriteLine($"  Studio apartment detected for service: {service.Name}");
                        serviceCost = 10 * priceMultiplier; // Flat $10 for studio
                        serviceDuration = 20; // 20 minutes for studio
                        Console.WriteLine($"    Studio pricing: $10 * {priceMultiplier} = ${serviceCost:F2}");
                    }
                    // Special handling for cleaner-hours relationship
                    else if (service.ServiceRelationType == "cleaner")
                    {
                        // Find the hours service in the update
                        var hoursServiceDto = updateOrderDto.Services.FirstOrDefault(s =>
                        {
                            var svc = _context.Services.Find(s.ServiceId);
                            return svc?.ServiceRelationType == "hours" && svc.ServiceTypeId == service.ServiceTypeId;
                        });

                        decimal hours = 0;

                        if (hoursServiceDto != null)
                        {
                            hours = hoursServiceDto.Quantity;
                        }
                        else
                        {
                            // Use original hours if not in update
                            hours = originalHours;
                        }

                        if (hours > 0)
                        {
                            var cleaners = serviceDto.Quantity;
                            var costPerCleanerPerHour = service.Cost * priceMultiplier;
                            serviceCost = costPerCleanerPerHour * cleaners * hours;
                            serviceDuration = hours * 60; // Convert to minutes
                        }
                        else
                        {
                            // Fallback
                            serviceCost = service.Cost * serviceDto.Quantity * priceMultiplier;
                            serviceDuration = service.TimeDuration * serviceDto.Quantity;
                        }
                    }
                    else if (service.ServiceRelationType == "hours")
                    {
                        // Hours service - don't add separately when used with cleaners
                        var hasCleanerServiceInUpdate = updateOrderDto.Services.Any(s =>
                        {
                            var svc = _context.Services.Find(s.ServiceId);
                            return svc?.ServiceRelationType == "cleaner" && svc.ServiceTypeId == service.ServiceTypeId;
                        });

                        if (hasCleanerServiceInUpdate)
                        {
                            shouldAddToOrder = false; // Skip adding hours separately
                        }
                        else
                        {
                            serviceCost = service.Cost * serviceDto.Quantity * priceMultiplier;
                            serviceDuration = service.TimeDuration * serviceDto.Quantity;
                        }
                    }
                    else
                    {
                        // Regular service calculation
                        serviceCost = service.Cost * serviceDto.Quantity * priceMultiplier;
                        serviceDuration = service.TimeDuration * serviceDto.Quantity;
                    }

                    // Add to order if it should be added
                    if (shouldAddToOrder)
                    {
                        var orderService = new Models.OrderService
                        {
                            Order = order,
                            ServiceId = serviceDto.ServiceId,
                            Quantity = serviceDto.Quantity,
                            Cost = serviceCost,
                            Duration = serviceDuration,
                            PriceMultiplier = priceMultiplier,
                            CreatedAt = DateTime.UtcNow
                        };
                        order.OrderServices.Add(orderService);
                        newSubTotal += serviceCost;
                        newTotalDuration += serviceDuration;

                        Console.WriteLine($"  Added service duration: {serviceDuration} min, Running total: {newTotalDuration} min");
                    }
                }
            }

            // Update extra services
            _context.OrderExtraServices.RemoveRange(order.OrderExtraServices);

            foreach (var extraServiceDto in updateOrderDto.ExtraServices)
            {
                var extraService = await _context.ExtraServices.FindAsync(extraServiceDto.ExtraServiceId);
                if (extraService != null)
                {
                    decimal cost = 0;
                    decimal duration = extraService.Duration; // Base duration

                    // For deep cleaning services, store their actual price
                    if (extraService.IsDeepCleaning || extraService.IsSuperDeepCleaning)
                    {
                        cost = extraService.Price;
                        // Deep cleaning services use base duration only
                    }
                    else
                    {
                        // Regular extra services - apply deep cleaning multiplier to non-same-day services
                        var currentMultiplier = extraService.IsSameDayService ? 1.0m : priceMultiplier;

                        if (extraService.HasHours && extraServiceDto.Hours > 0)
                        {
                            cost = extraService.Price * extraServiceDto.Hours * currentMultiplier;
                            // MULTIPLY DURATION BY HOURS
                            duration = (int)(extraService.Duration * extraServiceDto.Hours);
                        }
                        else if (extraService.HasQuantity && extraServiceDto.Quantity > 0)
                        {
                            cost = extraService.Price * extraServiceDto.Quantity * currentMultiplier;
                            // MULTIPLY DURATION BY QUANTITY
                            duration = extraService.Duration * extraServiceDto.Quantity;
                        }
                        else if (!extraService.HasHours && !extraService.HasQuantity)
                        {
                            cost = extraService.Price * currentMultiplier;
                            // Use base duration for flat services
                            duration = extraService.Duration;
                        }
                    }

                    var orderExtraService = new OrderExtraService
                    {
                        Order = order,
                        ExtraServiceId = extraServiceDto.ExtraServiceId,
                        Quantity = extraServiceDto.Quantity,
                        Hours = extraServiceDto.Hours,
                        Cost = cost,
                        Duration = duration, // Now this is properly calculated
                        CreatedAt = DateTime.UtcNow
                    };
                    order.OrderExtraServices.Add(orderExtraService);

                    // Only add non-deep-cleaning costs to subtotal
                    if (!extraService.IsDeepCleaning && !extraService.IsSuperDeepCleaning)
                    {
                        newSubTotal += cost;
                    }

                    // Always add duration to total
                    newTotalDuration += duration;
                }
            }

            // Add deep cleaning fee AFTER all other calculations
            newSubTotal += deepCleaningFee;

            Console.WriteLine($"\nBackend calculated total duration: {newTotalDuration} minutes");
            Console.WriteLine($"Frontend sent total duration: {updateOrderDto.TotalDuration} minutes");
            Console.WriteLine($"DIFFERENCE: {updateOrderDto.TotalDuration - newTotalDuration} minutes");

            // ADD: Check for significant mismatch and use frontend value if needed
            if (Math.Abs(updateOrderDto.TotalDuration - newTotalDuration) > 5) // Allow 5 minutes tolerance
            {
                Console.WriteLine($"WARNING: Significant duration mismatch! Using frontend value: {updateOrderDto.TotalDuration}");
                newTotalDuration = updateOrderDto.TotalDuration;
            }

            // Enforce minimum 1 hour duration
            if (newTotalDuration < 60)
            {
                Console.WriteLine($"WARNING: Backend calculated duration {newTotalDuration} is less than minimum 60 minutes. Setting to 60 minutes.");
                newTotalDuration = 60;
            }

            // IMPORTANT: This is where the issue is - backend uses its calculation instead of frontend value
            order.MaidsCount = updateOrderDto.MaidsCount;
            order.TotalDuration = newTotalDuration; // Now this will always be at least 60 minutes

            Console.WriteLine($"\nSAVING TO DB:");
            Console.WriteLine($"  TotalDuration: {order.TotalDuration} minutes (backend calculation with 60 min minimum)");
            Console.WriteLine($"  MaidsCount: {order.MaidsCount}");
            Console.WriteLine("========================================\n");

            // Recalculate totals
            order.SubTotal = newSubTotal;

            // Reapply original discount
            var totalDiscounts = order.DiscountAmount + (order.SubscriptionDiscountAmount == 0 ? 0 : order.SubscriptionDiscountAmount);
            var discountedSubTotal = newSubTotal - totalDiscounts;
            order.Tax = discountedSubTotal * 0.08875m; // 8.875% tax

            // Calculate total BEFORE gift card
            var totalBeforeGiftCard = discountedSubTotal + order.Tax + order.Tips + order.CompanyDevelopmentTips;

            // Handle gift card adjustment if there was a gift card applied
            if (!string.IsNullOrEmpty(order.GiftCardCode) && originalGiftCardAmountUsed > 0)
            {
                Console.WriteLine($"\n=== GIFT CARD UPDATE ===");
                Console.WriteLine($"Gift Card Code: {order.GiftCardCode}");
                Console.WriteLine($"Original gift card amount used: ${originalGiftCardAmountUsed}");

                // Get current gift card
                var giftCard = await _context.GiftCards
                    .FirstOrDefaultAsync(g => g.Code == order.GiftCardCode);

                if (giftCard != null && giftCard.IsActive)
                {
                    // Calculate available balance (current balance + what was originally used)
                    var availableBalance = giftCard.CurrentBalance + originalGiftCardAmountUsed;
                    Console.WriteLine($"Available balance (including original): ${availableBalance}");

                    // Calculate new gift card usage amount
                    var newGiftCardAmountToUse = Math.Min(availableBalance, totalBeforeGiftCard);
                    Console.WriteLine($"New gift card amount to use: ${newGiftCardAmountToUse}");

                    // Calculate the difference
                    var giftCardDifference = newGiftCardAmountToUse - originalGiftCardAmountUsed;
                    Console.WriteLine($"Difference: ${giftCardDifference}");

                    if (Math.Abs(giftCardDifference) > 0.01m) // Only update if there's a meaningful difference
                    {
                        // Update gift card balance
                        giftCard.CurrentBalance = availableBalance - newGiftCardAmountToUse;
                        Console.WriteLine($"New gift card balance: ${giftCard.CurrentBalance}");

                        // Update order
                        order.GiftCardAmountUsed = newGiftCardAmountToUse;
                        order.Total = totalBeforeGiftCard - newGiftCardAmountToUse;

                        // Find and update the existing gift card usage record
                        var existingUsage = await _context.GiftCardUsages
                            .FirstOrDefaultAsync(u => u.GiftCardId == giftCard.Id && u.OrderId == order.Id);

                        if (existingUsage != null)
                        {
                            Console.WriteLine($"Updating existing GiftCardUsage record");
                            // Update existing usage record
                            existingUsage.AmountUsed = newGiftCardAmountToUse;
                            existingUsage.BalanceAfterUsage = giftCard.CurrentBalance;
                            existingUsage.UsedAt = DateTime.UtcNow; // Update timestamp
                        }
                        else
                        {
                            Console.WriteLine($"Creating new GiftCardUsage record (this shouldn't normally happen)");
                            // This shouldn't happen, but create a new usage record if needed
                            var newUsage = new GiftCardUsage
                            {
                                GiftCardId = giftCard.Id,
                                OrderId = order.Id,
                                UserId = userId,
                                AmountUsed = newGiftCardAmountToUse,
                                BalanceAfterUsage = giftCard.CurrentBalance,
                                UsedAt = DateTime.UtcNow
                            };
                            _context.GiftCardUsages.Add(newUsage);
                        }
                    }
                    else
                    {
                        Console.WriteLine($"No significant gift card change, keeping original amount");
                        order.Total = totalBeforeGiftCard - originalGiftCardAmountUsed;
                    }
                }
                else
                {
                    Console.WriteLine($"Gift card not found or inactive, keeping original calculation");
                    order.Total = totalBeforeGiftCard - originalGiftCardAmountUsed;
                }

                Console.WriteLine($"=== END GIFT CARD UPDATE ===\n");
            }
            else
            {
                // No gift card applied
                order.Total = totalBeforeGiftCard;
            }

            // Final check to ensure the new total is not less than the original
            // Use the tolerance to handle floating-point precision issues
            if (order.Total < originalTotal - tolerance)
            {
                throw new Exception($"Cannot save changes. The new total (${order.Total:F2}) is less than the original amount paid (${originalTotal:F2}). Please add more services or keep the current selection.");
            }

            try
            {
                await _emailService.SendOrderUpdateNotificationAsync(
                    orderId: order.Id,
                    customerEmail: order.ContactEmail,
                    additionalAmount: additionalAmount
                );
            }
            catch (Exception ex)
            {
                // Log the error but don't fail the order update
                _logger.LogError(ex, $"Failed to send order update notification for Order #{order.Id}");
            }

            // Create update history record for ALL changes (not just when there's additional amount)
            // This ensures audit logs show changes even when there's no monetary difference
            var updateHistory = new OrderUpdateHistory
            {
                OrderId = order.Id,
                UpdatedByUserId = userId,
                UpdatedAt = DateTime.UtcNow,
                // Use the stored original values
                OriginalSubTotal = originalSubTotal,
                OriginalTax = originalTax,
                OriginalTips = originalTips,
                OriginalCompanyDevelopmentTips = originalCompanyDevelopmentTips,
                OriginalTotal = originalTotal,
                // New values after update
                NewSubTotal = order.SubTotal,
                NewTax = order.Tax,
                NewTips = order.Tips,
                NewCompanyDevelopmentTips = order.CompanyDevelopmentTips,
                NewTotal = order.Total,
                AdditionalAmount = additionalAmount,
                IsPaid = additionalAmount <= 0.01m // Mark as paid if no additional amount required
            };

            _context.OrderUpdateHistories.Add(updateHistory);

            await _orderRepository.UpdateAsync(order);
            await _orderRepository.SaveChangesAsync();

            return await GetOrderById(orderId, userId);
        }

        public async Task<OrderUpdatePaymentDto> CreateUpdatePaymentIntent(int orderId, int userId, UpdateOrderDto updateOrderDto)
        {
            var order = await _orderRepository.GetByIdWithDetailsAsync(orderId);

            if (order == null || order.UserId != userId)
                throw new Exception("Order not found");

            if (order.Status == "Cancelled" || order.Status == "Done")
                throw new Exception($"Cannot update a {order.Status.ToLower()} order");

            // Calculate additional amount
            var additionalAmount = await CalculateAdditionalAmount(orderId, updateOrderDto);

            if (additionalAmount <= 0)
                throw new Exception("No additional payment required");

            // Create payment intent for the additional amount
            var metadata = new Dictionary<string, string>
    {
        { "orderId", order.Id.ToString() },
        { "userId", userId.ToString() },
        { "type", "order_update" },
        { "additionalAmount", additionalAmount.ToString("F2") }
    };

            var paymentIntent = await _stripeService.CreatePaymentIntentAsync(additionalAmount, metadata);

            return new OrderUpdatePaymentDto
            {
                OrderId = order.Id,
                AdditionalAmount = additionalAmount,
                PaymentIntentId = paymentIntent.Id,
                PaymentClientSecret = paymentIntent.ClientSecret
            };
        }

        public async Task<bool> CancelOrder(int orderId, int userId, CancelOrderDto cancelOrderDto)
        {
            var order = await _orderRepository.GetByIdAsync(orderId);
            if (order == null || order.UserId != userId)
                throw new Exception("Order not found");
            if (order.Status == "Cancelled")
                throw new Exception("Order is already cancelled");
            if (order.Status == "Done")
                throw new Exception("Cannot cancel a completed order");
            // Check if service date is not too close (e.g., within 48 hours)
            // Only apply this restriction for paid orders - unpaid orders can be cancelled anytime
            if (order.IsPaid && order.ServiceDate <= DateTime.UtcNow.AddHours(48))
                throw new Exception("Cannot cancel order within 48 hours of service date");
            order.Status = "Cancelled";
            order.CancellationReason = cancelOrderDto.Reason;
            order.UpdatedAt = DateTime.UtcNow;
            // In a real system, you would initiate a refund process here
            // For now, we'll just mark it as cancelled
            await _orderRepository.UpdateAsync(order);
            await _orderRepository.SaveChangesAsync();

            // Restore special offer if one was used
            var userSpecialOffer = await _context.UserSpecialOffers
                .FirstOrDefaultAsync(uso => uso.UsedOnOrderId == orderId);

            if (userSpecialOffer != null)
            {
                userSpecialOffer.IsUsed = false;
                userSpecialOffer.UsedAt = null;
                userSpecialOffer.UsedOnOrderId = null;
                await _context.SaveChangesAsync();
            }

            return true;
        }

        public async Task<decimal> CalculateAdditionalAmount(int orderId, UpdateOrderDto updateOrderDto)
        {
            var order = await _orderRepository.GetByIdWithDetailsAsync(orderId);

            if (order == null)
                throw new Exception("Order not found");

            // Store original values for comparison - DO NOT MODIFY THE ORDER OBJECT!
            var originalTotal = order.Total;

            Console.WriteLine("\n========== CALCULATE ADDITIONAL AMOUNT DEBUG ==========");
            Console.WriteLine($"Order ID: {orderId}");
            Console.WriteLine($"ORIGINAL VALUES:");
            Console.WriteLine($"  Original SubTotal: ${order.SubTotal:F2}");
            Console.WriteLine($"  Original Tax: ${order.Tax:F2}");
            Console.WriteLine($"  Original Tips: ${order.Tips:F2}");
            Console.WriteLine($"  Original Company Tips: ${order.CompanyDevelopmentTips:F2}");
            Console.WriteLine($"  Original Total: ${originalTotal:F2}");
            Console.WriteLine($"  Original Discount: ${order.DiscountAmount:F2}");
            Console.WriteLine($"  Original Subscription Discount: ${order.SubscriptionDiscountAmount:F2}");

            // Log what services are being sent from frontend
            Console.WriteLine($"\nFRONTEND SENT SERVICES:");
            foreach (var serviceDto in updateOrderDto.Services)
            {
                Console.WriteLine($"  Service ID: {serviceDto.ServiceId}, Quantity: {serviceDto.Quantity}");
            }

            Console.WriteLine($"\nFRONTEND SENT EXTRA SERVICES:");
            foreach (var extraServiceDto in updateOrderDto.ExtraServices)
            {
                Console.WriteLine($"  Extra Service ID: {extraServiceDto.ExtraServiceId}, Quantity: {extraServiceDto.Quantity}, Hours: {extraServiceDto.Hours}");
            }

            // Calculate price multiplier from extra services FIRST
            decimal priceMultiplier = 1.0m;
            decimal deepCleaningFee = 0;

            Console.WriteLine($"\nCALCULATING PRICE MULTIPLIER:");
            foreach (var extraServiceDto in updateOrderDto.ExtraServices)
            {
                var extraService = await _context.ExtraServices.FindAsync(extraServiceDto.ExtraServiceId);
                if (extraService != null)
                {
                    Console.WriteLine($"  Extra Service: {extraService.Name} (ID: {extraService.Id})");
                    Console.WriteLine($"    IsDeepCleaning: {extraService.IsDeepCleaning}");
                    Console.WriteLine($"    IsSuperDeepCleaning: {extraService.IsSuperDeepCleaning}");
                    Console.WriteLine($"    PriceMultiplier: {extraService.PriceMultiplier}");
                    Console.WriteLine($"    Price: ${extraService.Price:F2}");

                    if (extraService.IsSuperDeepCleaning)
                    {
                        priceMultiplier = extraService.PriceMultiplier;
                        deepCleaningFee = extraService.Price;
                        Console.WriteLine($"    >> Setting Super Deep Cleaning - Multiplier: {priceMultiplier}, Fee: ${deepCleaningFee:F2}");
                        break; // Super deep cleaning takes precedence
                    }
                    else if (extraService.IsDeepCleaning && priceMultiplier == 1.0m)
                    {
                        priceMultiplier = extraService.PriceMultiplier;
                        deepCleaningFee = extraService.Price;
                        Console.WriteLine($"    >> Setting Deep Cleaning - Multiplier: {priceMultiplier}, Fee: ${deepCleaningFee:F2}");
                    }
                }
            }

            // Calculate new subtotal
            Console.WriteLine($"\nBASE CALCULATION:");
            Console.WriteLine($"  ServiceType: {order.ServiceType?.Name ?? "Unknown"} (ID: {order.ServiceType?.Id ?? 0})");
            Console.WriteLine($"  ServiceType BasePrice: ${order.ServiceType?.BasePrice ?? 0:F2}");
            Console.WriteLine($"  Price Multiplier: {priceMultiplier}");

            decimal newSubTotal = (order.ServiceType?.BasePrice ?? 0) * priceMultiplier;
            Console.WriteLine($"  Initial SubTotal (BasePrice * Multiplier): ${newSubTotal:F2}");

            decimal newTotalDuration = 0;

            if (order.ServiceType != null && order.ServiceType.TimeDuration > 0)
            {
                newTotalDuration += order.ServiceType.TimeDuration;
                Console.WriteLine($"  ServiceType Duration: {order.ServiceType.TimeDuration} minutes");
            }

            // Process services
            Console.WriteLine($"\nPROCESSING SERVICES:");
            var hasCleanerService = updateOrderDto.Services.Any(s =>
            {
                var svc = _context.Services.Find(s.ServiceId);
                return svc?.ServiceRelationType == "cleaner";
            });

            var hasHoursService = updateOrderDto.Services.Any(s =>
            {
                var svc = _context.Services.Find(s.ServiceId);
                return svc?.ServiceRelationType == "hours";
            });

            bool cleanerHoursCostCalculated = false;

            foreach (var serviceDto in updateOrderDto.Services)
            {
                var service = await _context.Services.FindAsync(serviceDto.ServiceId);
                if (service != null)
                {
                    decimal cost = 0;
                    decimal duration = 0;

                    Console.WriteLine($"\n  Service: {service.Name} (ID: {service.Id})");
                    Console.WriteLine($"    ServiceRelationType: {service.ServiceRelationType}");
                    Console.WriteLine($"    ServiceKey: {service.ServiceKey}");
                    Console.WriteLine($"    Cost: ${service.Cost:F2}");
                    Console.WriteLine($"    TimeDuration: {service.TimeDuration:F2} minutes");
                    Console.WriteLine($"    Quantity from Frontend: {serviceDto.Quantity}");

                    // Handle cleaner service
                    if (service.ServiceRelationType == "cleaner" && hasHoursService && !cleanerHoursCostCalculated)
                    {
                        // Find the hours service
                        var hoursServiceDto = updateOrderDto.Services.FirstOrDefault(s =>
                        {
                            var svc = _context.Services.Find(s.ServiceId);
                            return svc?.ServiceRelationType == "hours" && svc.ServiceTypeId == service.ServiceTypeId;
                        });

                        if (hoursServiceDto != null)
                        {
                            // Calculate combined cleaner-hours cost
                            var hours = hoursServiceDto.Quantity;
                            var cleaners = serviceDto.Quantity;
                            var costPerCleanerPerHour = service.Cost * priceMultiplier;
                            cost = costPerCleanerPerHour * cleaners * hours;
                            duration = (int)(hours * 60); // Duration is just hours * 60

                            Console.WriteLine($"    >> Cleaner-hours calculation:");
                            Console.WriteLine($"    >> ${costPerCleanerPerHour:F2}/hour * {cleaners} cleaners * {hours} hours = ${cost:F2}");

                            cleanerHoursCostCalculated = true;
                        }
                    }
                    // Handle hours service (skip if already calculated with cleaners)
                    else if (service.ServiceRelationType == "hours" && hasCleanerService)
                    {
                        Console.WriteLine($"    >> This is an HOURS service");
                        if (cleanerHoursCostCalculated)
                        {
                            Console.WriteLine($"    >> Cost already calculated with cleaner service, skipping");
                            // Don't add cost, but still track duration
                            var hours = serviceDto.Quantity;
                            duration = (int)(hours * 60);
                        }
                        else
                        {
                            Console.WriteLine($"    >> WARNING: Hours service without cleaner calculation!");
                            // This shouldn't happen if cleaner service exists
                            duration = serviceDto.Quantity * 60;
                        }
                    }
                    // Handle cleaner service without hours
                    else if (service.ServiceRelationType == "cleaner" && !hasHoursService)
                    {
                        cost = service.Cost * serviceDto.Quantity * priceMultiplier;
                        duration = service.TimeDuration * serviceDto.Quantity;
                        Console.WriteLine($"    >> Regular cleaner calculation: ${service.Cost:F2} * {serviceDto.Quantity} * {priceMultiplier} = ${cost:F2}");
                    }
                    // Handle studio apartment (0 bedrooms)
                    else if (service.ServiceKey == "bedrooms" && serviceDto.Quantity == 0)
                    {
                        cost = 10 * priceMultiplier; // $10 base for studio
                        duration = 20; // 20 minutes for studio
                        Console.WriteLine($"    >> Studio apartment - Fixed price: ${cost:F2}");
                    }
                    // Handle all other services
                    else if (service.ServiceRelationType != "cleaner" && service.ServiceRelationType != "hours")
                    {
                        cost = service.Cost * serviceDto.Quantity * priceMultiplier;
                        duration = service.TimeDuration * serviceDto.Quantity;
                        Console.WriteLine($"    >> Regular service calculation: ${service.Cost:F2} * {serviceDto.Quantity} * {priceMultiplier} = ${cost:F2}");
                    }

                    newSubTotal += cost;
                    newTotalDuration += duration;
                    Console.WriteLine($"    >> Added ${cost:F2} to subtotal. Running SubTotal: ${newSubTotal:F2}");
                }
            }

            // Process extra services
            Console.WriteLine($"\nPROCESSING REGULAR EXTRA SERVICES:");
            foreach (var extraServiceDto in updateOrderDto.ExtraServices)
            {
                var extraService = await _context.ExtraServices.FindAsync(extraServiceDto.ExtraServiceId);
                if (extraService != null)
                {
                    decimal cost = 0;

                    if (!extraService.IsDeepCleaning && !extraService.IsSuperDeepCleaning)
                    {
                        Console.WriteLine($"\n  Extra Service: {extraService.Name} (ID: {extraService.Id})");
                        Console.WriteLine($"    Price: ${extraService.Price:F2}");
                        Console.WriteLine($"    HasHours: {extraService.HasHours}");
                        Console.WriteLine($"    HasQuantity: {extraService.HasQuantity}");
                        Console.WriteLine($"    IsSameDayService: {extraService.IsSameDayService}");

                        // Apply deep cleaning multiplier to regular extra services
                        var currentMultiplier = extraService.IsSameDayService ? extraService.PriceMultiplier : priceMultiplier;
                        Console.WriteLine($"    Multiplier: {currentMultiplier}");

                        if (extraService.HasHours && extraServiceDto.Hours > 0)
                        {
                            cost = extraService.Price * extraServiceDto.Hours * currentMultiplier;
                            Console.WriteLine($"    >> Hours-based: ${extraService.Price} * {extraServiceDto.Hours} hours * {currentMultiplier} = ${cost:F2}");
                        }
                        else if (extraService.HasQuantity && extraServiceDto.Quantity > 0)
                        {
                            cost = extraService.Price * extraServiceDto.Quantity * currentMultiplier;
                            Console.WriteLine($"    >> Quantity-based: ${extraService.Price} * {extraServiceDto.Quantity} * {currentMultiplier} = ${cost:F2}");
                        }
                        else if (!extraService.HasHours && !extraService.HasQuantity)
                        {
                            cost = extraService.Price * currentMultiplier;
                            Console.WriteLine($"    >> Fixed price: ${extraService.Price} * {currentMultiplier} = ${cost:F2}");
                        }

                        newSubTotal += cost;
                        Console.WriteLine($"    >> Added ${cost:F2} to subtotal. Running SubTotal: ${newSubTotal:F2}");
                    }
                    else
                    {
                        Console.WriteLine($"\n  Skipping {extraService.Name} (deep cleaning already applied to multiplier)");
                    }
                    newTotalDuration += extraService.Duration;
                }
            }

            // Add deep cleaning fee AFTER all other calculations
            Console.WriteLine($"\nADDING DEEP CLEANING FEE: ${deepCleaningFee:F2}");
            newSubTotal += deepCleaningFee;
            Console.WriteLine($"FINAL SUBTOTAL: ${newSubTotal:F2}");

            // Compare with original
            Console.WriteLine($"\nSUBTOTAL COMPARISON:");
            Console.WriteLine($"  Original SubTotal: ${order.SubTotal:F2}");
            Console.WriteLine($"  Calculated SubTotal: ${newSubTotal:F2}");
            Console.WriteLine($"  Difference: ${newSubTotal - order.SubTotal:F2}");

            // If there's a significant mismatch, use the frontend value
            if (Math.Abs(updateOrderDto.TotalDuration - newTotalDuration) > 5) // Allow 5 minutes tolerance
            {
                Console.WriteLine($"\nWARNING: Duration mismatch exceeds tolerance!");
                Console.WriteLine($"  Frontend Duration: {updateOrderDto.TotalDuration} minutes");
                Console.WriteLine($"  Backend Duration: {newTotalDuration} minutes");
                Console.WriteLine($"  Using frontend value: {updateOrderDto.TotalDuration}");
                newTotalDuration = updateOrderDto.TotalDuration;
            }

            // Calculate new totals - DO NOT MODIFY THE ORDER OBJECT
            var totalDiscounts = order.DiscountAmount + order.SubscriptionDiscountAmount;
            var discountedSubTotal = newSubTotal - totalDiscounts;
            var newTax = Math.Round(discountedSubTotal * 0.08875m, 2); // 8.875% tax
            var newTotal = Math.Round(discountedSubTotal + newTax + updateOrderDto.Tips + updateOrderDto.CompanyDevelopmentTips, 2);

            Console.WriteLine($"\nNEW VALUES:");
            Console.WriteLine($"  New SubTotal: ${newSubTotal:F2}");
            Console.WriteLine($"  Price Multiplier: {priceMultiplier}");
            Console.WriteLine($"  Deep Cleaning Fee: ${deepCleaningFee:F2}");
            Console.WriteLine($"  Total Discounts: ${totalDiscounts:F2}");
            Console.WriteLine($"  Discounted SubTotal: ${discountedSubTotal:F2}");
            Console.WriteLine($"  New Tax: ${newTax:F2}");
            Console.WriteLine($"  New Tips: ${updateOrderDto.Tips:F2}");
            Console.WriteLine($"  New Company Tips: ${updateOrderDto.CompanyDevelopmentTips:F2}");
            Console.WriteLine($"  New Total: ${newTotal:F2}");

            var finalAdditionalAmount = newTotal - originalTotal;

            // If the difference is within tolerance (1 cent), consider it zero
            if (Math.Abs(finalAdditionalAmount) < 0.01m)
            {
                finalAdditionalAmount = 0;
            }

            Console.WriteLine($"\nFINAL CALCULATION:");
            Console.WriteLine($"  New Total: ${newTotal:F2}");
            Console.WriteLine($"  Original Total: ${originalTotal:F2}");
            Console.WriteLine($"  Additional Amount: ${finalAdditionalAmount:F2}");
            Console.WriteLine("======================================================\n");

            return finalAdditionalAmount;
        }

        public async Task<bool> MarkOrderAsDone(int orderId)
        {
            var order = await _orderRepository.GetByIdAsync(orderId);

            if (order == null)
                throw new Exception("Order not found");

            if (order.Status == "Cancelled")
                throw new Exception("Cannot complete a cancelled order");

            order.Status = "Done";
            order.UpdatedAt = DateTime.UtcNow;

            await _orderRepository.UpdateAsync(order);
            await _orderRepository.SaveChangesAsync();

            return true;
        }

        public async Task<List<OrderListDto>> GetUserOrdersForAdmin(int userId)
        {
            var orders = await _context.Orders
                .Include(o => o.ServiceType)
                .Include(o => o.User)
                .Where(o => o.UserId == userId)
                .OrderByDescending(o => o.OrderDate)
                .ToListAsync();

            return orders.Select(o => new OrderListDto
            {
                Id = o.Id,
                UserId = o.UserId,
                ContactEmail = o.ContactEmail,
                ContactFirstName = o.ContactFirstName,
                ContactLastName = o.ContactLastName,
                ServiceTypeName = o.ServiceType?.Name ?? "",
                IsCustomServiceType = o.ServiceType?.IsCustom ?? false,
                ServiceDate = o.ServiceDate,
                ServiceTime = o.ServiceTime,
                Status = o.Status,
                Total = o.Total,
                ServiceAddress = o.ServiceAddress + (string.IsNullOrEmpty(o.AptSuite) ? "" : $", {o.AptSuite}"),
                OrderDate = o.OrderDate,
                TotalDuration = o.TotalDuration,
                Tips = o.Tips,
                CompanyDevelopmentTips = o.CompanyDevelopmentTips,
                IsPaid = o.IsPaid,
                PaidAt = o.PaidAt
            }).ToList();
        }

        public async Task<OrderDto> GetOrderByIdForAdmin(int orderId)
        {
            var order = await _context.Orders
                .Include(o => o.ServiceType)
                .Include(o => o.Subscription)
                .Include(o => o.OrderServices)
                    .ThenInclude(os => os.Service)
                .Include(o => o.OrderExtraServices)
                    .ThenInclude(oes => oes.ExtraService)
                .Include(o => o.User)
                .FirstOrDefaultAsync(o => o.Id == orderId);

            if (order == null)
                throw new Exception("Order not found");

            return new OrderDto
            {
                Id = order.Id,
                UserId = order.UserId,
                ServiceTypeId = order.ServiceTypeId,
                ServiceTypeName = order.ServiceType?.Name ?? "",
                ServiceDate = order.ServiceDate,
                ServiceTime = order.ServiceTime,
                ServiceAddress = order.ServiceAddress,
                AptSuite = order.AptSuite,
                City = order.City,
                State = order.State,
                ZipCode = order.ZipCode,
                EntryMethod = order.EntryMethod,
                ContactFirstName = order.ContactFirstName,
                ContactLastName = order.ContactLastName,
                ContactEmail = order.ContactEmail,
                ContactPhone = order.ContactPhone,
                SpecialInstructions = order.SpecialInstructions,
                MaidsCount = order.MaidsCount,
                TotalDuration = order.TotalDuration,
                SubTotal = order.SubTotal,
                Tax = order.Tax,
                DiscountAmount = order.DiscountAmount,
                SubscriptionDiscountAmount = order.SubscriptionDiscountAmount,
                Tips = order.Tips,
                CompanyDevelopmentTips = order.CompanyDevelopmentTips,
                Total = order.Total,
                Status = order.Status,
                OrderDate = order.OrderDate,
                SubscriptionId = order.SubscriptionId,
                SubscriptionName = order.Subscription?.Name,
                Services = order.OrderServices?.Select(os => new OrderServiceDto
                {
                    Id = os.Id,
                    ServiceId = os.ServiceId,
                    ServiceName = os.Service?.Name ?? "",
                    Quantity = os.Quantity,
                    Cost = os.Cost,
                    Duration = os.Duration,
                    PriceMultiplier = os.PriceMultiplier
                }).ToList() ?? new List<OrderServiceDto>(),
                ExtraServices = order.OrderExtraServices?.Select(oes => new OrderExtraServiceDto
                {
                    Id = oes.Id,
                    ExtraServiceId = oes.ExtraServiceId,
                    ExtraServiceName = oes.ExtraService?.Name ?? "",
                    Quantity = oes.Quantity,
                    Hours = oes.Hours,
                    Cost = oes.Cost,
                    Duration = oes.Duration
                }).ToList() ?? new List<OrderExtraServiceDto>()
            };
        }

        private string? GetSpecialOfferName(string? promoCode)
        {
            if (string.IsNullOrEmpty(promoCode)) return null;

            // Check if it's a special offer
            if (promoCode.StartsWith("SPECIAL_OFFER:"))
            {
                return promoCode.Substring("SPECIAL_OFFER:".Length);
            }

            return null;
        }

        private string? GetPromoCodeDetails(string? promoCode)
        {
            if (string.IsNullOrEmpty(promoCode)) return null;

            // If it's a special offer, return null (handled by SpecialOfferName)
            if (promoCode.StartsWith("SPECIAL_OFFER:"))
            {
                return null;
            }

            // For legacy first-time discount
            if (promoCode == "firstUse")
            {
                return "First-Time Customer Discount";
            }

            // For regular promo codes, return the code
            return promoCode;
        }

        private string MaskGiftCardCode(string code)
        {
            if (code.Length >= 4)
            {
                return $"****-****-{code.Substring(code.Length - 4)}";
            }
            return "****";
        }

        private OrderDto MapOrderToDto(Order order)
        {
            return new OrderDto
            {
                Id = order.Id,
                UserId = order.UserId,
                ServiceTypeId = order.ServiceTypeId,
                ServiceTypeName = order.ServiceType?.Name ?? "",
                OrderDate = order.OrderDate,
                ServiceDate = order.ServiceDate,
                ServiceTime = order.ServiceTime,
                Status = order.Status,
                SubTotal = order.SubTotal,
                Tax = order.Tax,
                Tips = order.Tips,
                CompanyDevelopmentTips = order.CompanyDevelopmentTips,
                Total = order.Total,
                DiscountAmount = order.DiscountAmount,
                SubscriptionDiscountAmount = order.SubscriptionDiscountAmount,
                PromoCode = order.PromoCode,
                SpecialOfferName = GetSpecialOfferName(order.PromoCode),
                PromoCodeDetails = GetPromoCodeDetails(order.PromoCode),
                GiftCardDetails = order.GiftCardCode != null ?
                $"{MaskGiftCardCode(order.GiftCardCode)} (${order.GiftCardAmountUsed:F2})" : null,
                SubscriptionId = order.SubscriptionId,
                SubscriptionName = order.Subscription?.Name ?? "",
                GiftCardCode = order.GiftCardCode,
                GiftCardAmountUsed = order.GiftCardAmountUsed,
                EntryMethod = order.EntryMethod,
                SpecialInstructions = order.SpecialInstructions,
                ContactFirstName = order.ContactFirstName,
                ContactLastName = order.ContactLastName,
                ContactEmail = order.ContactEmail,
                ContactPhone = order.ContactPhone,
                ServiceAddress = order.ServiceAddress,
                AptSuite = order.AptSuite,
                City = order.City,
                State = order.State,
                ZipCode = order.ZipCode,
                TotalDuration = order.TotalDuration,
                MaidsCount = order.MaidsCount,
                IsPaid = order.IsPaid,
                PaidAt = order.PaidAt,
                Services = order.OrderServices?.Select(os => new OrderServiceDto
                {
                    Id = os.Id,
                    ServiceId = os.ServiceId,
                    ServiceName = os.Service?.Name ?? "",
                    Quantity = os.Quantity,
                    Cost = os.Cost,
                    Duration = os.Duration
                }).ToList() ?? new List<OrderServiceDto>(),
                ExtraServices = order.OrderExtraServices?.Select(oes => new OrderExtraServiceDto
                {
                    Id = oes.Id,
                    ExtraServiceId = oes.ExtraServiceId,
                    ExtraServiceName = oes.ExtraService?.Name ?? "",
                    Quantity = oes.Quantity,
                    Hours = oes.Hours,
                    Cost = oes.Cost,
                    Duration = oes.Duration
                }).ToList() ?? new List<OrderExtraServiceDto>()
            };
        }
    }
}