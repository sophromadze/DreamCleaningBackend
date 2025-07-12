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

        public OrderService(IOrderRepository orderRepository, ApplicationDbContext context)
        {
            _orderRepository = orderRepository;
            _context = context;
        }

        public async Task<List<OrderListDto>> GetAllOrdersForAdmin()
        {
            // Get ALL orders from the database without filtering by userId
            var orders = await _context.Orders
                .Include(o => o.ServiceType)
                .Include(o => o.User)
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
                OrderDate = o.OrderDate
            }).ToList();
        }

        public async Task<List<OrderListDto>> GetUserOrders(int userId)
        {
            var orders = await _orderRepository.GetUserOrdersAsync(userId);

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
                OrderDate = o.OrderDate
            }).ToList();
        }

        public async Task<OrderDto> GetOrderById(int orderId, int userId)
        {
            var order = await _orderRepository.GetByIdWithDetailsAsync(orderId);

            if (order == null || order.UserId != userId)
                throw new Exception("Order not found");

            return MapOrderToDto(order);
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

            // TOTALDURATION LOGGING - START
            Console.WriteLine("\n========== TOTALDURATION TRACKING ==========");
            Console.WriteLine($"Frontend sent TotalDuration: {updateOrderDto.TotalDuration} minutes");
            Console.WriteLine($"Current DB TotalDuration: {order.TotalDuration} minutes");

            // Store the original values
            var originalTotal = order.Total;
            var originalGiftCardAmountUsed = order.GiftCardAmountUsed;

            // Log the original total
            Console.WriteLine($"Original total: ${originalTotal:F2}");

            // Calculate the additional amount before updating
            var additionalAmount = await CalculateAdditionalAmount(orderId, updateOrderDto);

            // Log the additional amount
            Console.WriteLine($"Additional amount: ${additionalAmount:F2}");

            // Check if the new total would be less than the original
            if (additionalAmount < 0)
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
            order.UpdatedAt = DateTime.Now;

            // Update user's phone number if they don't have one
            var user = await _context.Users.FindAsync(userId);
            if (user != null && string.IsNullOrEmpty(user.Phone) && !string.IsNullOrEmpty(updateOrderDto.ContactPhone))
            {
                user.Phone = updateOrderDto.ContactPhone;
                user.UpdatedAt = DateTime.Now;
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

                    // Special handling for cleaner-hours relationship
                    if (service.ServiceRelationType == "cleaner")
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
                    else if (service.ServiceKey == "bedrooms" && serviceDto.Quantity == 0)
                    {
                        // Studio apartment - flat rate
                        serviceCost = 20 * priceMultiplier;
                        serviceDuration = 20; // 20 minutes for studio
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
                            CreatedAt = DateTime.Now
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
                        // Regular extra services
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
                        CreatedAt = DateTime.Now
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

            // IMPORTANT: This is where the issue is - backend uses its calculation instead of frontend value
            order.MaidsCount = updateOrderDto.MaidsCount;
            order.TotalDuration = newTotalDuration; // THIS IS THE PROBLEM - should be updateOrderDto.TotalDuration

            Console.WriteLine($"\nSAVING TO DB:");
            Console.WriteLine($"  TotalDuration: {order.TotalDuration} minutes (using backend calculation)");
            Console.WriteLine($"  MaidsCount: {order.MaidsCount}");
            Console.WriteLine("========================================\n");

            // Recalculate totals
            order.SubTotal = newSubTotal;

            // Reapply original discount
            var totalDiscounts = order.DiscountAmount + (order.SubscriptionDiscountAmount == 0 ? 0 : order.SubscriptionDiscountAmount);
            var discountedSubTotal = newSubTotal - totalDiscounts;
            order.Tax = discountedSubTotal * 0.088m; // 8.8% tax

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
                            existingUsage.UsedAt = DateTime.Now; // Update timestamp
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
                                UsedAt = DateTime.Now
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
            if (order.Total < originalTotal)
            {
                throw new Exception($"Cannot save changes. The new total (${order.Total:F2}) is less than the original amount paid (${originalTotal:F2}). Please add more services or keep the current selection.");
            }

            await _orderRepository.UpdateAsync(order);
            await _orderRepository.SaveChangesAsync();

            return await GetOrderById(orderId, userId);
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
            // Check if service date is not too close (e.g., within 24 hours)
            if (order.ServiceDate <= DateTime.Now.AddHours(24))
                throw new Exception("Cannot cancel order within 24 hours of service date");
            order.Status = "Cancelled";
            order.CancellationReason = cancelOrderDto.Reason;
            order.UpdatedAt = DateTime.Now;
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

            // Calculate new subtotal
            decimal newSubTotal = order.ServiceType.BasePrice * priceMultiplier;
            decimal newTotalDuration = 0;

            if (order.ServiceType != null && order.ServiceType.TimeDuration > 0)
            {
                newTotalDuration += order.ServiceType.TimeDuration;
            }

            // Process services
            foreach (var serviceDto in updateOrderDto.Services)
            {
                var service = await _context.Services.FindAsync(serviceDto.ServiceId);
                if (service != null)
                {
                    decimal cost = 0;
                    decimal duration = 0;

                    if (service.ServiceRelationType == "hours")
                    {
                        var cleanerService = updateOrderDto.Services.FirstOrDefault(s =>
                        {
                            var svc = _context.Services.Find(s.ServiceId);
                            return svc?.ServiceRelationType == "cleaner" && svc.ServiceTypeId == service.ServiceTypeId;
                        });

                        if (cleanerService != null)
                        {
                            cost = service.Cost * serviceDto.Quantity * cleanerService.Quantity * priceMultiplier;
                            duration = service.TimeDuration * serviceDto.Quantity * cleanerService.Quantity;
                        }
                    }
                    else if (service.Cost > 0)
                    {
                        cost = service.Cost * serviceDto.Quantity * priceMultiplier;
                        duration = service.TimeDuration * serviceDto.Quantity;
                    }
                    else
                    {
                        duration = service.TimeDuration * serviceDto.Quantity;
                    }

                    newSubTotal += cost;
                    newTotalDuration += duration;
                }
            }

            // Process extra services
            foreach (var extraServiceDto in updateOrderDto.ExtraServices)
            {
                var extraService = await _context.ExtraServices.FindAsync(extraServiceDto.ExtraServiceId);
                if (extraService != null)
                {
                    decimal cost = 0;

                    if (!extraService.IsDeepCleaning && !extraService.IsSuperDeepCleaning)
                    {
                        var currentMultiplier = extraService.IsSameDayService ? extraService.PriceMultiplier : 1.0m;

                        if (extraService.HasHours && extraServiceDto.Hours > 0)
                        {
                            cost = extraService.Price * extraServiceDto.Hours * currentMultiplier;
                        }
                        else if (extraService.HasQuantity && extraServiceDto.Quantity > 0)
                        {
                            cost = extraService.Price * extraServiceDto.Quantity * currentMultiplier;
                        }
                        else if (!extraService.HasHours && !extraService.HasQuantity)
                        {
                            cost = extraService.Price * currentMultiplier;
                        }

                        newSubTotal += cost;
                    }
                    newTotalDuration += extraService.Duration;
                }
            }

            // Add deep cleaning fee AFTER all other calculations
            newSubTotal += deepCleaningFee;

            // If there's a significant mismatch, use the frontend value
            // The frontend has all the user selections and should be the source of truth
            if (Math.Abs(updateOrderDto.TotalDuration - newTotalDuration) > 5) // Allow 5 minutes tolerance
            {
                Console.WriteLine($"WARNING: Duration mismatch exceeds tolerance! Using frontend value: {updateOrderDto.TotalDuration}");
                newTotalDuration = updateOrderDto.TotalDuration;
            }

            // Now use the potentially updated newTotalDuration
            order.MaidsCount = updateOrderDto.MaidsCount;
            order.TotalDuration = newTotalDuration; // This will now use frontend value if there's a big difference

            // Recalculate totals
            order.SubTotal = newSubTotal;

            // Reapply original discount
            var discountedSubTotal = newSubTotal - order.DiscountAmount;
            order.Tax = discountedSubTotal * 0.088m; // 8.8% tax
            order.Total = discountedSubTotal + order.Tax + order.Tips + order.CompanyDevelopmentTips;

            // Calculate additional amount
            var additionalAmount = order.Total - order.Total; // This will be recalculated properly

            // Calculate new total with tips from DTO
            var newTax = discountedSubTotal * 0.088m;
            var newTotal = discountedSubTotal + newTax + updateOrderDto.Tips + updateOrderDto.CompanyDevelopmentTips;

            // Log for debugging
            Console.WriteLine($"CalculateAdditionalAmount Debug:");
            Console.WriteLine($"  Maids Count from DTO: {updateOrderDto.MaidsCount}");
            Console.WriteLine($"  New SubTotal: ${newSubTotal}");
            Console.WriteLine($"  Discount: ${order.DiscountAmount}");
            Console.WriteLine($"  Discounted SubTotal: ${discountedSubTotal}");
            Console.WriteLine($"  New Tax: ${newTax}");
            Console.WriteLine($"  Tips: ${updateOrderDto.Tips}");
            Console.WriteLine($"  New Total: ${newTotal}");
            Console.WriteLine($"  Original Total: ${order.Total}");
            Console.WriteLine($"  Additional Amount: ${newTotal - order.Total}");

            return newTotal - order.Total;
        }

        public async Task<bool> MarkOrderAsDone(int orderId)
        {
            var order = await _orderRepository.GetByIdAsync(orderId);

            if (order == null)
                throw new Exception("Order not found");

            if (order.Status == "Cancelled")
                throw new Exception("Cannot complete a cancelled order");

            order.Status = "Done";
            order.UpdatedAt = DateTime.Now;

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
                OrderDate = o.OrderDate
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